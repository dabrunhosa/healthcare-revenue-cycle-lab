namespace RevenueCycle.Domain;

public sealed record WorkflowCorrelation(
    Guid WorkflowId,
    string EncounterId,
    string? ClaimId = null,
    string? PayerClaimId = null,
    string? TraceNumber = null,
    string? PaymentReference = null);
