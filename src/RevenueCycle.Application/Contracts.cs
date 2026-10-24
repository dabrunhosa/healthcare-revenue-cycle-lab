using RevenueCycle.Domain;

namespace RevenueCycle.Application;

public sealed record StoredWorkflow(Workflow Workflow, string Version);
public sealed record IngestionResult(Workflow Workflow, bool Created);
public sealed record PayerSubmission(string ClaimId, string EncounterId, decimal Amount, string Currency, string Artifact);
public sealed record PayerReceipt(string ClaimId, string PayerClaimId);
public sealed record PayerResult(string Outcome, string? PayerClaimId = null, bool Transient = false);

public interface IWorkflowStore
{
    Task<IngestionResult> CreateAsync(string key, string fingerprint, Workflow workflow, CancellationToken cancellationToken);
    Task<StoredWorkflow?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Workflow[]> ListAsync(CancellationToken cancellationToken);
    Task<StoredWorkflow?> AcquireAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<StoredWorkflow?> SaveAsync(StoredWorkflow original, Workflow updated, bool releaseLease, CancellationToken cancellationToken);
}

public interface IPayerClient
{
    Task<PayerResult> SubmitAsync(PayerSubmission submission, CancellationToken cancellationToken);
}

public sealed record RecoveryRequest(string ReasonCode);

public sealed class WorkflowOptions
{
    public int MaxAttempts { get; init; } = 4;
    public double BaseDelaySeconds { get; init; } = 2;
    public double MaxDelaySeconds { get; init; } = 30;

    public void Validate()
    {
        if (MaxAttempts is < 1 or > 10 || BaseDelaySeconds is < 0.1 or > 60 ||
            MaxDelaySeconds < BaseDelaySeconds || MaxDelaySeconds > 300 ||
            !double.IsFinite(BaseDelaySeconds) || !double.IsFinite(MaxDelaySeconds))
            throw new InvalidOperationException("Invalid workflow retry options.");
    }
}
