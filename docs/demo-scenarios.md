# Demo acceptance criteria

## Successful submission

Submit `samples/encounter.json` with a unique idempotency key. Inspect the workflow until Submitted. Verify an educational 837 artifact, ordered history, and linked EncounterId, ClaimId, and PayerClaimId. TraceNumber and PaymentReference remain unset: receipt does not mean payment.

## Duplicate and conflicting requests

Repeat identical ingestion and verify the original workflow is returned. Change a billing value under that key and verify 409. Another key for the same encounter also conflicts. The payer separately rejects changed content for an existing submission key.

## Bounded failure and recovery

Start the payer in Unavailable mode. A new workflow reaches ManualReview after four attempts; history includes transient failures and a circuit-open outcome. Restart payer in Accept mode, allow the circuit break to expire, then recover with PayerRestored. Verify the original claim identity, a new recovery cycle, and eventual Submitted state. Reject mode must enter ManualReview after one attempt.

## Accepted request with lost response and process restart

Start payer in AcceptThenTimeout mode. Submit an encounter; once GET /claims exposes its receipt, stop the API before the response completes. Restart both services with their original storage paths. After the thirty-second lease expires, verify UnknownAfterRestart in attempt history, eventual Submitted state, and exactly one persisted payer receipt for that claim. This demonstrates deduplication under at-least-once delivery, not exactly-once transport.

The automated smoke script performs all scenarios against isolated files. It also checks metadata, health, OpenAPI, actual submission traces, and absence of clinical fixture content in logs.

## Later: remittance versus settlement

Receive an 835 while bank settlement is missing and keep the workflow unreconciled. Add matching settlement and reconcile. Amount mismatch, partial payment, and duplicate callbacks are future separate outcomes.
