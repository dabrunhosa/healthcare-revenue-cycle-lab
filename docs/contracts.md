# Input and transaction contracts

## Supported clinical input

`POST /workflows` accepts the envelope in `samples/encounter.json`, with an `Idempotency-Key` of 1–80 letters, digits, dots, underscores, or hyphens. The maximum request body is 64 KiB. Unknown fields are rejected, including extra FHIR fields; this is a deliberately narrow adapter, not a general-purpose FHIR endpoint.

- `synthetic` must be true.
- `patient`: `resourceType = Patient`; `id` starts with `syn-` and contains 1–48 additional alphanumeric/hyphen characters. No names, demographics, addresses, or contacts are accepted.
- `encounter`: `resourceType = Encounter`, synthetic ID, `status = finished`, `subject.reference = Patient/{supplied patient ID}`.
- Encounter `class`: `system = http://terminology.hl7.org/CodeSystem/v3-ActCode`, `code = AMB`.
- Encounter `period`: explicit start/end timestamps, end no earlier than start, years between 2020 and 2035. Service date uses the start timestamp's local calendar date.
- `billing`: one diagnosis matching the supported syntax and 1–10 lines. Each line has fictional `DEMOddd` procedure code, 1–100 integer units, and positive unit price up to 10,000 with at most two decimal places. Currency is USD.

The adapter checks syntax and limited cross-resource consistency, not terminology validity, medical necessity, coverage, coding correctness, or comprehensive FHIR conformance. The envelope and billing fields are lab-specific. An explicit synthetic flag and identifier convention do not detect real data; only authored fixtures should be supplied.

## Educational 837 output

The writer emits ISA/GS envelopes and ST/BHT/NM1/PER/HL/SBR/CLM/HI/LX/SV1/DTP/SE segments, then GE/IEA trailers. It records calculated amounts, encounter service date, diagnosis syntax, fictional procedure lines, and stable claim identity. Transaction segment counts and envelope widths have focused tests.

This is an illustrative 005010X222A1-shaped artifact, **not a conformant 837P implementation**. Required loops/fields are omitted (including complete addresses and subscriber/provider demographics), control numbers are fixed for the single-transaction lab, and `DEMOddd` codes are intentionally fictional despite the illustrative HC qualifier. The example provider identifier is synthetic and must not be used for real routing. No external implementation guide or licensed code set is bundled.

## Mock payer wire contract

`POST /claims` accepts `claimId`, `encounterId`, `amount`, `currency`, and `artifact`, with `Idempotency-Key = claimId`. It performs envelope-level lab checks, not EDI parsing/validation. The first accepted request persists a fingerprint and generated PayerClaimId. Subsequent identical submissions return the original receipt; conflicting bodies return 409.

Failure modes are process configuration, not public mutation endpoints:

| Payer:Mode | Behavior for an unseen valid claim |
| --- | --- |
| Accept | Persist and return receipt |
| Unavailable | Return 503 without a receipt |
| Reject | Return 422 without a receipt |
| AcceptThenTimeout | Persist receipt, then delay first response ten seconds |

Existing matching receipts are returned before mode-specific failure logic. The payer stores receipt identities/fingerprints, not the full claim text. The API persists the educational artifact.

## Recovery contract

`POST /workflows/{id}/recover` takes `{ "reasonCode": "PayerRestored" }` or `OperatorRetry`. Only ManualReview accepts recovery; duplicate/concurrent recovery is rejected using a version comparison. The reason and state transition remain in history. Recovery does not edit the claim or its idempotency key.

## Standards references

- [HL7 FHIR R4 Encounter](https://hl7.org/fhir/R4/encounter.html)
- [HL7 FHIR R4 Patient](https://hl7.org/fhir/R4/patient.html)
- [X12 healthcare transaction flow](https://x12.org/flow/health-care)

277CA acknowledgment and 277 status remain separate later-phase concerns. An 835 will explain remittance/adjudication results; separate bank evidence will establish settlement.
