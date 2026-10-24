namespace RevenueCycle.Domain;

public sealed record WorkflowEvent(DateTimeOffset At, WorkflowStage Stage, string Reason);
public sealed record SubmissionAttempt(int Number, int Cycle, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null, string Outcome = "InFlight");

public sealed record Workflow(
    WorkflowCorrelation Correlation,
    ClinicalContext Clinical,
    BillingContext Billing,
    Claim Claim,
    WorkflowStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    WorkflowEvent[] History,
    SubmissionAttempt[] Attempts,
    int RecoveryCycle = 0)
{
    public Workflow MoveTo(WorkflowStage next, DateTimeOffset now, string reason)
    {
        var allowed = (Stage, next) switch
        {
            (WorkflowStage.EncounterReceived, WorkflowStage.ClinicalContextReady) => true,
            (WorkflowStage.ClinicalContextReady, WorkflowStage.ClaimCreated) => true,
            (WorkflowStage.ClaimCreated, WorkflowStage.SubmissionPending) => true,
            (WorkflowStage.SubmissionPending or WorkflowStage.RetryScheduled,
                WorkflowStage.Submitted or WorkflowStage.RetryScheduled or WorkflowStage.ManualReview) => true,
            (WorkflowStage.ManualReview, WorkflowStage.SubmissionPending) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Transition {Stage} -> {next} is not allowed.");
        return this with { Stage = next, History = [.. History, new(now, next, reason)] };
    }
}
