# ADR-0006: Diagnose root cause with an evidence engine plus an LLM narrator

- **Status:** Accepted
- **Date:** 2026-11-22

## Context

After detection fires, the on-call engineer needs the likely cause and a suggested fix, not just "cost is up 4x".

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| Rules only | No hallucination, free, private, works when the LLM vendor is down | Reports "no rule matched" for causes outside tracked dimensions |
| LLM only | Connects factors and reads naturally | Hallucination risk, spend, latency, data leaves the system |
| **Hybrid (chosen)** | Exact facts from code, readable explanation from the LLM | Two components to build and test |

## Decision

**Step 1: evidence engine (deterministic).** Cost decomposes as:

> cost = request volume × units per request × price per unit × (1 + retry rate)

The engine compares the anomaly window with the baseline and ranks which factor moved most, for example:

```
Top mover: average input tokens +310%  (87% of the increase)
Also:      retry rate 2% -> 3%         (4%)
Unexplained: 9%
```

For non-LLM routes, units are calls or segments and the same decomposition applies.

**Step 2: LLM narrator.** The LLM receives only this small, pre-aggregated, PII-free summary and returns structured JSON: diagnosis, confidence, the evidence it relied on and a suggested remediation. Fusebox validates it:

- It must match the JSON schema.
- Every claim must cite evidence that exists in the input.
- Numbers in the text must match the input.
- Confidence is capped when evidence is thin.

### How it works

- **Exact decomposition.** Spend is billable calls per minute, times tokens per call, times cost per token. The engine takes the logarithm of each factor's change, so the product becomes a sum and each factor's share of the increase is exact. The shares add up to 100% and the remainder is reported as unexplained. Tokens per call is split into input and output. Cost per token is split into traffic shifting between models and the effective cost within a model. The effective cost also moves when the balance of cheap input tokens and pricier output tokens does, so it is reported as an effective cost and not as a list-price change.
- **Other signals.** Error rate and tokens per call are ratios, split by model into a change within models and a shift between them. Volume is split by what became of the requests. Latency is traced to the models whose own 95th percentile moved (an approximate share, since percentiles do not add).
- **Compared with.** The anomaly window the detector judged, against the hour before it, using per-minute means from the stored totals.
- **Retry rate is a signal, not a factor.** Repeated requests (see ADR-0005) can be diagnosed as their own anomaly, with the same before-and-after evidence as an error rate. They are not yet a factor in the cost formula, so a spend jump caused by retries is explained as more calls per minute, with the repeat share shown only when that signal fires too.
- **The comparison period is the hour before the anomaly was noticed.** If detection is late, that hour can already contain the start of the problem, and the size of the change is understated. In the demo this showed as "from 84% to 93%" for a loop that was really 0% to 100%.
- **Top mover.** The factor with the biggest share, named in the explanation. A model is reported as where the change was, never as the cause.
- **Evidence ids.** Every fact has an id such as `spend.inputTokens`. The LLM must cite the ids it relies on.
- **Strict validation.** The LLM's answer must be a JSON object with exactly four fields. Every id it cites must exist. Every number in the text, including "6x", "$4.2k", "1,200ms" and percentages, must match a number in the evidence to within 3%. Confidence is capped by what the evidence supports (thin data or an unclear cause means low). Anything else is thrown away and the template is used, with the reason recorded.
- **Once per anomaly.** A background job picks up anomalies with no diagnosis, diagnoses each once, and saves the evidence with the explanation in PostgreSQL. A monthly count of LLM explanations is the spend cap, enforced from the saved rows.
- **LLM access.** Any OpenAI-compatible endpoint. Pointing the base address at Fusebox itself budgets and records these calls like any tenant's.

## Safeguards

- **Advisory only.** The LLM never triggers blocking or any enforcement.
- **Fallback.** If the LLM is slow, disabled or fails validation, the alert goes out with a templated explanation. Detection alerts never wait on the LLM.
- **Spend cap.** One call per incident, a small model and a hard monthly budget. Fusebox can route its own LLM calls through itself, so its spend is capped and visible like any other tenant.
- **Privacy.** Only aggregated numbers and labels leave the system, never prompts, user IDs or payloads.
- **Labeling.** Output is shown as "AI-suggested" next to the raw numbers.

## Why

- Both advocates ended up describing this design: rules are the fallback of the LLM approach, and the LLM adds only prose and cross-factor hypotheses.
- Facts stay deterministic and testable, while the LLM adds readability.

## Consequences

- A golden-incident evaluation set tests diagnosis quality: incidents with a known cause (a retry-style surge, prompt bloat, longer answers, a model swap, a price change, several causes at once, an error storm in one model, a slow model), where the engine must name the injected cause and the shares must add up. See `EvidenceEngineTests`. Schema validation, evidence checking, the spend cap, timeout and fallback are tested with fakes.
- The LLM path has been tested against a fake model and a fake HTTP endpoint, not a real model. How good a real model's wording is, and how often it passes validation, still needs measuring on real incidents.

## Revisit when

- No data may go to an LLM provider (use the rules-only path), or an evaluation set shows an LLM-first diagnosis is clearly better.
