using System.Diagnostics;
using RevenueCycle.Domain;

namespace RevenueCycle.Application;

public sealed class WorkflowProcessor(IWorkflowStore store, IPayerClient payer, TimeProvider clock, WorkflowOptions options)
{
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var stored = await store.AcquireAsync(clock.GetUtcNow(), cancellationToken);
        if (stored is null) return false;
        var workflow = stored.Workflow;
        var now = clock.GetUtcNow();
        workflow = workflow with
        {
            Attempts = workflow.Attempts.Select(a => a.FinishedAt is null
                ? a with { FinishedAt = now, Outcome = "UnknownAfterRestart" } : a).ToArray()
        };
        var cycleAttempts = workflow.Attempts.Count(a => a.Cycle == workflow.RecoveryCycle);
        if (cycleAttempts >= options.MaxAttempts)
        {
            await store.SaveAsync(stored, workflow.MoveTo(WorkflowStage.ManualReview, now, "RetryBudgetExhausted"), true, cancellationToken);
            return true;
        }

        workflow = workflow with { Attempts = [.. workflow.Attempts, new(workflow.Attempts.Length + 1, workflow.RecoveryCycle, now)] };
        stored = await store.SaveAsync(stored, workflow, false, cancellationToken);
        if (stored is null) return true;
        using var activity = LabTelemetry.Activities.StartActivity("payer.submit", ActivityKind.Client);
        activity?.SetTag("workflow.id", workflow.Correlation.WorkflowId.ToString());
        activity?.SetTag("claim.id", workflow.Claim.Id);
        activity?.SetTag("encounter.id", workflow.Clinical.EncounterId);
        var result = await payer.SubmitAsync(new(workflow.Claim.Id, workflow.Clinical.EncounterId,
            workflow.Claim.Amount, workflow.Claim.Currency, workflow.Claim.Artifact), cancellationToken);
        now = clock.GetUtcNow();
        workflow = workflow with
        {
            Attempts = [.. workflow.Attempts[..^1], workflow.Attempts[^1] with { FinishedAt = now, Outcome = result.Outcome }]
        };
        if (result.PayerClaimId is not null)
        {
            workflow = workflow.MoveTo(WorkflowStage.Submitted, now, "PayerReceiptRecorded") with
            {
                Correlation = workflow.Correlation with { PayerClaimId = result.PayerClaimId }
            };
        }
        else if (result.Transient && cycleAttempts + 1 < options.MaxAttempts)
        {
            var delay = Math.Min(options.MaxDelaySeconds, options.BaseDelaySeconds * Math.Pow(2, cycleAttempts));
            workflow = workflow.MoveTo(WorkflowStage.RetryScheduled, now, result.Outcome) with
            {
                NextAttemptAt = now.AddSeconds(delay * (0.5 + Random.Shared.NextDouble() * 0.5))
            };
        }
        else workflow = workflow.MoveTo(WorkflowStage.ManualReview, now, result.Transient ? "RetryBudgetExhausted" : result.Outcome);
        activity?.SetTag("submission.outcome", result.Outcome);
        if (result.PayerClaimId is null) activity?.SetStatus(ActivityStatusCode.Error);
        LabTelemetry.Submissions.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome));
        await store.SaveAsync(stored, workflow, true, cancellationToken);
        return true;
    }
}
