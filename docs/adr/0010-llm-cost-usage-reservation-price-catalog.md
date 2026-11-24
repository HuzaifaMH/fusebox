# ADR-0010: Account for LLM cost with provider usage, a reservation and a price catalog

- **Status:** Accepted (extended during review)
- **Date:** 2026-11-22

## Context

The true cost of an LLM call is unknown until the response finishes, because output length is not known up front. Fusebox must still decide, before forwarding, whether to allow a request, while many are in flight. Real cost also depends on **price**, which is not always the public list price: enterprises have negotiated discounts, volume tiers, prepaid credits, batch pricing, and separate rates for cached input, cache writes and reasoning tokens.

This ADR applies to **LLM routes**. Flat-price APIs (SMS, maps, payments) use a price table, so their cost is known before the call and needs no reservation or settlement.

## Options considered

| Option | Strengths | Weaknesses |
|---|---|---|
| Provider usage + reservation | Matches billed tokens. Bounded overshoot under concurrency | Reservation can over-block if output is not capped |
| Pre-flight estimate with a local tokenizer | Enables early downgrade decisions | Unbounded error on output, blind to cache discounts, tools and images. Tokenizers differ per vendor |
| **Hybrid (chosen)** | Accurate accounting and early routing | More components |

## Decision

### Token accounting

1. **Reserve.** Before forwarding, atomically reserve the worst case in Redis: estimated input plus capped output at the effective price (ADR-0002).
2. **Forward.** Stream the response straight through.
3. **Settle.** Read the vendor's reported usage and replace the reservation with the real cost, releasing the difference.

Edge cases:

- **Streaming:** read usage as it passes. For OpenAI-style streams Fusebox adds the option that requests a final usage chunk. Anthropic reports usage in its stream events.
- **Client disconnects:** the vendor usually keeps billing. Keep reading, or settle on tokens counted so far.
- **Usage missing:** settle at the reservation, the conservative choice.
- **No output limit set:** use a per-route default cap, or the typical (p95) output length learned from history.
- **Routing estimate:** a cheap heuristic (about 4 characters per token) is used only to choose a fallback model or reject an obviously huge prompt. It never decides billing.
- **Per-vendor extractors:** one small usage extractor per provider.

### Price catalog

```
cost = sum over token kinds of ( tokens of that kind × rate for that kind )
       kinds: input, output, cached-input, cache-write, reasoning, batch

rate lookup, first match wins:
  1. tenant or account override  (negotiated rate, or "list price x 0.85")
  2. vendor list price           (default, versioned by effective date)
```

- The catalog lives in Postgres and reaches Redis through the config refresh (ADR-0002).
- Prices are **effective-dated**, so old requests keep their old rate when a vendor or contract changes.
- Volume tiers are modeled as an updatable effective blended rate, since exact tiers are known to the vendor only at invoice time.
- **Credits are not a price.** A credit balance changes how you pay, not what a request costs. Cost is computed from the effective rate, and any credit balance is tracked separately or ignored at first.
- **Reconciliation.** A periodic job compares Fusebox totals with the vendor's own usage and cost reporting where available, reports drift (for example, "3% under the invoice") and can suggest an adjusted multiplier. Check current vendor APIs before building this.

### MVP scope

List prices plus a per-tenant multiplier. Custom per-model rates and vendor reconciliation follow later.

## How it works

- **What a tenant pays.** Each tenant has a multiplier on list prices (0.85 for a 15% discount, kept as whole ten-thousandths so no digits are lost) and, optionally, its own rate for particular models. A model with its own rate uses it as it is; any other model uses the list price times the multiplier. Scaling rounds up to the next micro-dollar, so a price is never understated. A model with neither a list price nor a negotiated rate is refused as an unknown model.
- **One price, used twice.** The same lookup prices the worst case held before the call and the real cost settled after it, so a discounted tenant is held less and charged less. Checked through the real gateway at half price: both the held amount and the charge are exactly half.
- **Effective-dated.** Negotiated rates are rows with a start time, like list prices, so one can be scheduled ahead and the old one is kept. Removing a tenant's rows puts it back on list price.
- **Where it lives.** Postgres (a multiplier column on tenants, and a table of negotiated rates), the configuration source for development, and the admin API (`PUT /admin/tenants/{id}/pricing`, `PUT` and `DELETE /admin/tenants/{id}/prices/{model}`). Changes reach the gateway through the same catalog reload as everything else, and the catalog's hash includes pricing so other instances notice. A catalog cached before this existed reads as list price.
- **Reconciliation.** `POST /admin/reconciliation` takes a vendor statement for a period (one total, or a line per model) and compares it with what Fusebox charged, from the per-hour totals. It reports each difference as a fraction of the vendor's figure, says whether Fusebox is under or over, and suggests the factor that would close the gap. It changes nothing. Noise is ignored on purpose: a 2% tolerance, and nothing is judged when both sides are under $1.
- **Names are matched, never guessed.** If the statement says `gpt-4o-2024-08-06` and callers send `gpt-4o`, the report shows two lines, one "not on the statement" and one "not recorded by Fusebox", which is the honest answer. The period must be whole hours in UTC and end at least three hours ago, so every call in it has been recorded.

## Why

- Vendor-reported usage is the only token count that matches the bill. Local tokenizers are routinely wrong for images, tools, caching and reasoning tokens.
- The reservation closes the concurrency gap that approximate gateways leave.
- A price layer is needed because tokens alone do not give dollars.

## Consequences

- Fusebox's dollar figure is an **estimate**. The invoice is the truth, and reconciliation closes the loop.
- Fusebox will not reproduce complex enterprise billing exactly, such as retroactive tier discounts or credit expiry.
- Cost still has two token kinds, input and output. Cached input, cache writes, reasoning and batch rates are not modelled, so a vendor that bills those differently will show as drift in reconciliation.
- Reconciliation takes a statement someone supplies. It does not fetch from vendors' usage or billing APIs, because those differ, change, and often need separate admin credentials, and none was checked. A job that fetches them can feed the same comparison.
- Reconciliation suggests a factor but never applies it. Fusebox's total includes calls whose usage was unreadable and were charged at their reservation, which makes it read high, so a person should look before adjusting prices.
- Differences are reported for the account as a whole, not per tenant, because a vendor bills the account.

## Revisit when

- Huge prompts must be rejected before any provider spend, or a vendor reports no usage data.
