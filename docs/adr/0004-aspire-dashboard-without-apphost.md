# ADR-0004: Use the Aspire Dashboard container without an Aspire AppHost
**Status:** Accepted   **Date:** 2026-08-26

## Context
Four deployables emit OpenTelemetry traces, metrics and structured logs. Debugging a token redemption in Download.Gateway, or a stalled render in Generation.Worker at 400 replicas, means correlating all three signals against one trace ID. The conventional local stack is Jaeger + Prometheus + Grafana + Seq: four containers, four UIs, four configs, no shared correlation. The .NET Aspire Dashboard (`mcr.microsoft.com/dotnet/aspire-dashboard`) ingests OTLP and renders all three, trace-linked, in one container.

The deliverable is a Dockerfile per service plus a `docker-compose.yml` that runs from a clean clone alongside PostgreSQL, PgBouncer, Redis and MinIO.

## Options considered
| Option | Pros | Cons |
| --- | --- | --- |
| Full Aspire AppHost | Automatic resource wiring, service discovery, dashboard-driven local orchestration | Owns process launch itself, so it duplicates or contradicts the compose file; emits no images for the deployment target; adds a project no environment runs |
| Jaeger + Prometheus + Grafana + Seq | Production-grade, each best in class, familiar | Four containers and four UIs on a dev machine; no cross-signal correlation without Grafana wiring; heavy for a clean clone |
| Standalone Aspire Dashboard container | One container, three correlated signals, OTLP-native, zero code coupling | Development tool: no persistence, restart loses history, thin auth story |

## Decision
Run the Aspire Dashboard as an ordinary container in `docker-compose.yml`. Do not add an AppHost project. Services are configured only through `OTEL_EXPORTER_OTLP_ENDPOINT`, so the Dashboard is one address among many.

## Consequences
We forgo AppHost's automatic wiring and its local orchestration; compose does that work explicitly instead, which is the topology the deployment target actually runs. Repointing production at a managed OTLP collector is an environment-variable change, not a code change — no vendor SDK is referenced anywhere. The Dashboard holds telemetry in memory only and must never be published beyond a developer machine or a private compose network.

## Revisit when
- Export needs anything beyond OTLP — vendor-specific enrichment, or a sampling policy the SDK cannot express in configuration.
- Debugging a monthly generation burst requires telemetry history that survives a container restart.
- Microsoft ships the Dashboard as a supported collector with persistence and authentication.
- The team adopts Aspire for real deployment, making the AppHost the source of truth for images.
