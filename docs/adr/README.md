# Architecture Decision Records

Each record explains one decision: the context, the options considered, what was chosen and why, the consequences, and when to revisit it. For the full picture, see [the architecture overview](../architecture.md).

| # | Decision | Choice |
|---|---|---|
| [0001](0001-interception-yarp-gateway.md) | Interception | YARP gateway |
| [0002](0002-enforcement-state-redis.md) | Budget and limit state | Redis with atomic scripts, config refreshed from Postgres |
| [0003](0003-telemetry-pipeline-in-process-queue.md) | Recording requests | In-process queue with background batch writer |
| [0004](0004-telemetry-storage-postgresql.md) | Telemetry storage | PostgreSQL with partitions and rollups |
| [0005](0005-anomaly-detection-statistics.md) | Anomaly detection | Statistics |
| [0006](0006-root-cause-hybrid-evidence-and-llm.md) | Root-cause analysis | Evidence engine plus LLM narrator |
| [0007](0007-alerting-durable-table-pagerduty-slack.md) | Alerting | Durable alert table, PagerDuty and Slack |
| [0008](0008-failure-mode-fail-open-degraded.md) | Budget store down | Fail-open, degraded |
| [0009](0009-visibility-grafana-opentelemetry.md) | Visibility | Grafana and OpenTelemetry |
| [0010](0010-llm-cost-usage-reservation-price-catalog.md) | LLM cost | Provider usage, reservation and price catalog |

## How these were decided

Each decision was debated by two agents, one arguing each side, and then judged and reviewed. In several cases the debate and review changed the outcome: ADR-0006 and ADR-0010 became hybrids, ADR-0007 was revised to guarantee delivery to the on-call engineer, and ADR-0008 settled on degraded fail-open.
