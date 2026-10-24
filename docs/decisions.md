# Design decisions

## 001: Layered application with a separate mock payer

Use Domain, Application, Infrastructure, and API projects for the revenue-cycle application, plus an independent mock payer. This gives one clear orchestration owner and a real HTTP failure boundary without requiring a large distributed deployment.

Alternatives: a single API project is simpler but obscures boundaries; many microservices introduce deployment and consistency overhead before the business flow exists. The selected structure permits later separation without promising it will be necessary.

## 002: Deliver a runnable scaffold before the vertical slice

The first repository milestone contains working hosts, packaging, verification, and architecture documentation. Unimplemented capabilities are visible in both metadata and README. Domain records are initial vocabulary, not a working state machine.

This creates an honest initial GitHub milestone that can be reviewed independently. Behavioral tests, adapter packages, persistence, and telemetry will arrive with the functionality they verify.

## 003: Durable local storage for Phase 1

Phase 1b uses EF Core SQLite. Pending work is represented by indexed stage/due-time/lease fields on the same row as the workflow document. This makes workflow creation and scheduling atomic without a separate outbox table or broker. Compared with in-memory state, it supports restart/recovery demonstrations; compared with a broker and separate database server, it lowers local setup cost.

Production database concurrency, horizontal worker coordination, and broker delivery semantics remain separate design work. SQLite is not evidence of production-scale behavior.

## 004: Durable retry budget with HTTP circuit breaking

Persist each attempt before sending and fence updates with a version token and lease. The worker owns backoff and retry limits; the HTTP resilience pipeline only supplies circuit breaking and timeout. This avoids hidden retry multiplication and preserves evidence after process interruption. Expired in-flight attempts are ambiguous, not assumed failed at the payer.

The mock payer maintains its own unique claim receipt and input fingerprint. Stable claim identity plus persistent deduplication resolves repeated delivery after a lost response. Manual recovery starts a new finite retry cycle and preserves the artifact and key.

## 005: Explicitly limited standards adapters

Accept a strict FHIR R4 subset with separately supplied billing and emit an educational 837-shaped artifact using fictional procedure codes. Full standards validation and trading-partner requirements are outside this milestone. Reject unsupported inputs instead of silently dropping them. This keeps the demonstration inspectable without implying certification or reusing real clinical assets.
