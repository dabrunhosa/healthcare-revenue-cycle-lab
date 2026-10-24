# Synthetic scenarios

`encounter.json` is an authored fictional Patient/Encounter subset with explicit demo billing. Submit it to `POST /workflows` with an Idempotency-Key. No real patient information, employer messages, payer files, or bank details are included.

The fixture is intentionally narrow: synthetic identifiers, an ambulatory encounter, one diagnosis-shaped value, and a fictional procedure code. See [supported contract](../docs/contracts.md) for validation and standards limitations. Use a new encounter ID and key to create a separate workflow; replaying the same key/input returns the existing one.
