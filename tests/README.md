# Verification

`RevenueCycle.Tests` exercises real SQLite persistence with isolated temporary databases and deterministic clocks. Cases cover atomic concurrent ingestion, conflicting keys and encounters, invalid input, guarded transitions, successful correlation, finite retry scheduling, permanent failure, manual recovery, lease competition, stale writes, interrupted attempts, exhausted budgets after restart, telemetry labels/payload exclusion, and educational transaction counts.

Run `dotnet test RevenueCycle.slnx -c Release --disable-build-servers -m:1`.

`scripts/smoke.ps1` separately builds and launches the actual API and mock payer over HTTP with isolated database directories. It verifies duplicate/conflicting requests, OpenAPI, artifact retrieval, permanent/transient errors, circuit opening and recovery, manual requeue, lost responses, service restarts, payer deduplication, and console traces. It retains ignored logs/databases in `artifacts/smoke/` for inspection and stops only processes it starts.

Container startup and OTLP dashboard rendering require Docker and are explicitly unverified locally. Neither suite establishes full FHIR/X12 conformance or production scale.
