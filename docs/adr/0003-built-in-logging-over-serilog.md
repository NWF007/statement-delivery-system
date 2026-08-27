# ADR-0003: Use Microsoft.Extensions.Logging with the OpenTelemetry exporter instead of Serilog
**Status:** Accepted   **Date:** 2026-08-26

## Context
All four deployables already export traces and metrics over OTLP to a standalone Aspire Dashboard, wired once in ServiceDefaults. Logs are the third signal on a pipeline we already run.

Adding Serilog means running two logging stacks: Serilog sinks alongside the OTel log exporter. That is two configuration surfaces, two enrichment models (`ILogEventEnricher` versus log-record processors), and two places a redaction rule can be missed. Download.Gateway is unauthenticated and handles raw download tokens; a token in stdout is a live credential in a log store. One redaction gap is one incident.

The tamper-evident audit trail (~470M events/year) is a PostgreSQL table, not log output; sink durability is not a compliance requirement.

## Options considered

| Option | Pros | Cons |
| --- | --- | --- |
| Serilog with native sinks | Mature sink ecosystem; message-template destructuring; familiar to most .NET hires | Second pipeline beside OTel; correlation needs a hand-written enricher; redaction enforced twice |
| Serilog routed into OTel via `Serilog.Sinks.OpenTelemetry` | Keeps the Serilog API; single egress path | Still two config surfaces and two enrichment models; a bridge package to version and debug |
| `Microsoft.Extensions.Logging` + OTel log exporter | One pipeline, one config surface; `TraceId`/`SpanId` attached by the OTel logging integration; source-generated `LoggerMessage` for allocation-free hot paths | No sink ecosystem; no `@` destructuring; plainer console output |

## Decision
Built-in `ILogger<T>` with `AddJsonConsole` for structured stdout and `AddOpenTelemetry().WithLogging()` for OTLP export, registered once in ServiceDefaults. Correlation is automatic. Redaction is registered in ServiceDefaults from day one, before any sensitive field exists, because redaction that arrives after the feature is redaction that already leaked.

## Consequences
We give up Serilog's sinks and destructuring; we get one dependency set, one enrichment model, one chokepoint for redaction review. Generation.Worker's hot path uses source-generated `LoggerMessage`.

## Revisit when
- The redaction API cannot express a required masking rule (say, preserving an account number's last four digits) without a custom sink.
- A regulator requires diagnostic logs written to immutable storage by the process itself, with no collector hop.
- Burst log egress exceeds the Aspire Dashboard's in-memory retention window, forcing a real collector in front of it.
- The OpenTelemetry .NET logs exporter loses stable status, or Serilog ships an OTel bridge the OTel project maintains.
