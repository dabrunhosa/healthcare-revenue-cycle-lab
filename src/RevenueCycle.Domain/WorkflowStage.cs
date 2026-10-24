namespace RevenueCycle.Domain;

public enum WorkflowStage
{
    EncounterReceived,
    ClinicalContextReady,
    ClaimCreated,
    SubmissionPending,
    Submitted,
    RetryScheduled,
    ManualReview
}
