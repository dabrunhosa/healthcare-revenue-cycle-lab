# Design decisions

## 001: Layered application with a separate mock payer

Use Domain, Application, Infrastructure, and API projects for the revenue-cycle application, plus an independent mock payer. This gives one clear orchestration owner and a real HTTP failure boundary without requiring a large distributed deployment.

Alternatives: a single API project is simpler but obscures boundaries; many microservices introduce deployment and consistency overhead before the business flow exists. The selected structure permits later separation without promising it will be necessary.

## 002: Deliver a runnable scaffold before the vertical slice

The first repository milestone contains working hosts, packaging, verification, and architecture documentation. Unimplemented capabilities are visible in both metadata and README. Domain records are initial vocabulary, not a working state machine.

This creates an honest initial GitHub milestone that can be reviewed independently. Behavioral tests, adapter packages, persistence, and telemetry will arrive with the functionality they verify.

## 003: Durable local storage for Phase 1

Plan to use EF Core SQLite and a transactional pending-work table. Compared with in-memory state, this supports restart/recovery demonstrations; compared with a broker and separate database server, it lowers local setup cost. Implementation and package selection are deferred to the first vertical slice.

Production database concurrency, horizontal worker coordination, and broker delivery semantics remain separate design work. SQLite is not evidence of production-scale behavior.
