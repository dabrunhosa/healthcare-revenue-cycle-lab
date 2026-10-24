# Demo acceptance criteria

## Available now: scaffold

Run `pwsh -File scripts/smoke.ps1` from the repository root. It must build successfully, start both hosts, receive healthy responses, confirm synthetic-only scaffold metadata, and stop its own processes. Workflow processing and payer submission must report unavailable.

## Next: successful submission

A fictional encounter plus supplied billing context yields one persisted claim artifact and one payer receipt. Inspection shows ordered transitions and matching EncounterId, ClaimId, and PayerClaimId. Payment references remain unset. Submitted means received, not paid.

## Next: duplicate and ambiguous delivery

Replay identical ingestion and verify the original workflow is returned. Reuse its key with different content and verify rejection. Simulate payer acceptance followed by a lost response; retry and verify one payer claim, including after a restart.

## Next: bounded failure and recovery

Simulate transient failures until the retry budget is exhausted. Inspect attempt history and manual-review state. Restore the payer, request recovery, and verify the original submission identity is reused. Permanent validation failures must not consume automatic retries.

## Later: remittance versus settlement

Receive an 835 while bank settlement is missing and verify the workflow remains unreconciled. Introduce matching settlement and verify reconciliation. Exercise amount mismatch, partial payment, and duplicate callbacks as separate outcomes.
