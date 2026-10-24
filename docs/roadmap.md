# Roadmap

## Phase 1a — Repository scaffold

- [x] Layered .NET 10 solution and independent mock payer host.
- [x] Health/metadata endpoints and executable smoke verification.
- [x] Initial state vocabulary and business correlation record.
- [x] Architecture narrative, demo criteria, Docker packaging, and CI definition.
- [ ] Observe a successful GitHub Actions run after publication.
- [ ] Verify Docker build and container startup on an available runtime.

## Phase 1b — First vertical slice

- [ ] Define supported FHIR R4 input and explicit billing schema with synthetic fixtures.
- [ ] Implement validated patient, encounter, billing, and claim models.
- [ ] Add durable workflow/history/pending-work storage and guarded transitions.
- [ ] Produce a declared 837 professional-claim subset and retain its artifact.
- [ ] Submit to the mock payer with persistent deduplication and correlation.
- [ ] Implement idempotency, bounded retries/backoff, circuit breaker, and manual recovery.
- [ ] Add workflow inspection, OpenAPI, and OpenTelemetry with local export.
- [ ] Verify normal flow, permanent failure, ambiguous timeout, duplicates, and restart recovery.

## Phase 2 — Clinical adapters and payer responses

Add a documented HL7 v2 adapter; distinguish acknowledgment and status semantics; implement selected 277CA/277 scenarios and an educational 835 remittance subset. Support out-of-order/duplicate responses and clear rejection versus adjudication outcomes.

## Phase 3 — Settlement and reconciliation

Add mock bank/ACH settlement, remittance-to-payment matching, partial and grouped payments, adjustments, unmatched items, and an exception queue. Keep remittance receipt separate from confirmed cash settlement.

## Phase 4 — Operational demonstrations and UI

Add a workflow dashboard, broader fault scenarios, richer trace visualization, and reproducible walkthroughs. Evaluate production hardening separately from the synthetic local lab.
