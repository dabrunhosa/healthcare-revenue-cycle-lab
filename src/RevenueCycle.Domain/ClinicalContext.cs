namespace RevenueCycle.Domain;

public sealed record ClinicalContext(string PatientId, string EncounterId, DateOnly ServiceDate);
public sealed record BillingLine(string ProcedureCode, int Units, decimal UnitPrice);
public sealed record BillingContext(string DiagnosisCode, BillingLine[] Lines)
{
    public decimal Total => Lines.Sum(line => line.Units * line.UnitPrice);
}
public sealed record Claim(string Id, decimal Amount, string Currency, string Artifact);
