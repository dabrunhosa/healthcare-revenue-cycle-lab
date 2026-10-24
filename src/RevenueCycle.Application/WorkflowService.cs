using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevenueCycle.Domain;

namespace RevenueCycle.Application;

public static class LabTelemetry
{
    public const string Name = "RevenueCycle.Lab";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Counter<long> Submissions = Meter.CreateCounter<long>("revenue_cycle.submissions");
}

public sealed partial class WorkflowService(IWorkflowStore store, TimeProvider clock)
{
    public async Task<IngestionResult> IngestAsync(string key, IngestRequest input, CancellationToken cancellationToken)
    {
        if (!KeyPattern().IsMatch(key)) throw new InputException("Idempotency-Key must contain 1-80 letters, digits, dots, underscores, or hyphens.");
        var clinical = ClinicalInput.Validate(input);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
        var now = clock.GetUtcNow();
        var claim = ClaimWriter.Create(clinical, input.Billing, "CL" + Guid.NewGuid().ToString("N")[..18], now);
        var workflow = new Workflow(new(Guid.NewGuid(), clinical.EncounterId, claim.Id), clinical, input.Billing, claim,
            WorkflowStage.EncounterReceived, now, now, [new(now, WorkflowStage.EncounterReceived, "SyntheticIngestion")], []);
        workflow = workflow.MoveTo(WorkflowStage.ClinicalContextReady, now, "ClinicalAndBillingValidated")
            .MoveTo(WorkflowStage.ClaimCreated, now, "Educational837Created")
            .MoveTo(WorkflowStage.SubmissionPending, now, "SubmissionQueued");
        return await store.CreateAsync(key, fingerprint, workflow, cancellationToken);
    }

    public async Task<Workflow> RecoverAsync(Guid id, RecoveryRequest request, CancellationToken cancellationToken)
    {
        if (request.ReasonCode is not ("PayerRestored" or "OperatorRetry"))
            throw new InputException("ReasonCode must be PayerRestored or OperatorRetry.");
        var stored = await store.GetAsync(id, cancellationToken) ?? throw new KeyNotFoundException();
        if (stored.Workflow.Stage != WorkflowStage.ManualReview)
            throw new ConflictException("Only workflows in ManualReview can be requeued.");
        var now = clock.GetUtcNow();
        var updated = stored.Workflow.MoveTo(WorkflowStage.SubmissionPending, now, request.ReasonCode) with
        {
            RecoveryCycle = stored.Workflow.RecoveryCycle + 1, NextAttemptAt = now
        };
        return (await store.SaveAsync(stored, updated, true, cancellationToken))?.Workflow
            ?? throw new ConflictException("Workflow changed; refresh before recovering.");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,80}\\z")]
    private static partial Regex KeyPattern();
}
