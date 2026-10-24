# Phase 1b verification

Verified locally on 2026-10-24 with .NET SDK 10.0.401 on Windows.

| Check | Result |
| --- | --- |
| Release solution build | Exit 0; zero warnings and errors |
| Workflow/domain/SQLite suite | Exit 0; 11 passed, 0 failed, 0 skipped |
| Real HTTP smoke suite | Exit 0; all scenarios passed |
| NuGet vulnerability scan, including transitives | Exit 0; no vulnerable packages reported by configured sources |
| Diff whitespace and generated-comment review | Passed |
| Docker build/start and OTLP dashboard | Unverified: Docker unavailable |
| Remote GitHub Actions | Unverified for this change: not committed/pushed |

Commands used:

```powershell
dotnet test RevenueCycle.slnx -c Release --no-restore --disable-build-servers -m:1
pwsh -NoProfile -File scripts/smoke.ps1
dotnet list RevenueCycle.slnx package --vulnerable --include-transitive --no-restore
```

The smoke script rebuilt the entire solution before running. It verified synthetic ingestion, educational 837 text, payer receipt, duplicate/conflicting requests, strict input rejection, OpenAPI, permanent rejection, bounded retries, open circuit, circuit recovery, manual requeue, and payer acceptance followed by a lost response and restart of both services. It checked stable receipt identity, payer payload-conflict rejection, actual submission traces, and absence of the clinical fixture content in logs.

The test suite exercised actual SQLite uniqueness, competing leases, stale-write fencing, persisted attempt budgets, interrupted/exhausted recovery, guarded state transitions, billing validation, transaction counts, and low-cardinality telemetry outcomes.

Local evidence is retained in ignored `artifacts/smoke/ea934db9-1d27-4761-b8d9-5d74a3e54a0e/`. Logs/databases are local evidence, not repository deliverables. Tests use isolated temporary files and the smoke script stops its own hosts.

The sandbox blocked test-host/loopback connections; those checks passed with approved execution outside it. An initial HTTP test assertion incorrectly handled a JSON-array response; direct assignment corrected the harness and the full scenario passed. Vulnerability findings in the initial telemetry selection were resolved by upgrading to audited 1.20.1 packages, not by suppressing audit warnings.

These checks validate the declared educational subset and local execution behavior. They do not establish full FHIR/X12 conformance, production-scale guarantees, or compliance.
