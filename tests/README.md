# Verification plan

The scaffold is verified by `scripts/smoke.ps1`, which builds the solution, launches both hosts, and checks health, metadata, and the absence of workflow processing. No domain test suite exists yet.

Add domain and integration test projects alongside implementation. Required cases:

- Allowed and forbidden state transitions; missing or unsupported clinical/billing data.
- Repeated ingestion, changed payload under the same key, and concurrent processing.
- Stable payer submission identity, including accepted requests with lost responses.
- Retry budget, timeout, transient/permanent errors, circuit opening and recovery.
- Service restart and persisted pending work; audited manual requeue.
- Workflow inspection and correlation; metadata-only telemetry.

End-to-end tests must exercise HTTP and persistence, not only mocked application interfaces.
