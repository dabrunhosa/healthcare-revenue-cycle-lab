using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RevenueCycle.Application;
using RevenueCycle.Domain;
using RevenueCycle.Infrastructure;
using Xunit;

namespace RevenueCycle.Tests;

public sealed class WorkflowTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "revenue-cycle-tests", Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private TestFactory factory = null!;
    private WorkflowStore store = null!;
    private WorkflowService service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        factory = new(new DbContextOptionsBuilder<WorkflowDb>().UseSqlite($"Data Source={Path.Combine(directory, "workflow.db")};Pooling=False").Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        store = new(factory);
        service = new(store, clock);
    }

    public Task DisposeAsync()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "revenue-cycle-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Test directory is outside the temporary test root.");
        Directory.Delete(directory, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task IngestionIsAtomicAndDuplicateSafe()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.IngestAsync("same-key", Input(), default)));
        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Workflow.Correlation.WorkflowId).Distinct());
        var workflow = results[0].Workflow;
        Assert.Equal(WorkflowStage.SubmissionPending, workflow.Stage);
        Assert.Equal(4, workflow.History.Length);
        Assert.Equal(125.50m, workflow.Claim.Amount);
        Assert.Contains("SV1*HC:DEMO001*125.50*UN*1", workflow.Claim.Artifact);
    }

    [Fact]
    public async Task KeyConflictAndEncounterReuseAreRejected()
    {
        await service.IngestAsync("key", Input(), default);
        await Assert.ThrowsAsync<ConflictException>(() => service.IngestAsync("key", Input() with { Billing = new("Z00.00", [new("DEMO001", 2, 125.50m)]) }, default));
        await Assert.ThrowsAsync<ConflictException>(() => service.IngestAsync("other-key", Input(), default));
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task RejectsInvalidClinicalAndBillingInputsWithoutPersisting()
    {
        var input = Input();
        IngestRequest[] invalid =
        [
            input with { Synthetic = false }, input with { Patient = null! },
            input with { Encounter = input.Encounter with { Subject = new("Patient/syn-other") } },
            input with { Encounter = input.Encounter with { Status = "in-progress" } },
            input with { Encounter = input.Encounter with { Period = null! } },
            input with { Billing = null! }, input with { Billing = new("Z00.00", []) },
            input with { Billing = new("Z00.00", [new("DEMO001", 0, 1)]) },
            input with { Billing = new("Z00.00", [new("DEMO001", 1, 1.001m)]) },
            input with { Billing = new("Z00.00", [new("DEMO001~INJECT", 1, 1)]) },
            input with { Billing = new("Z00.00", [new("DEMO001\n", 1, 1)]) },
            input with { Patient = input.Patient with { Id = "syn-patient-001\n" } }
        ];
        foreach (var request in invalid)
            await Assert.ThrowsAsync<InputException>(() => service.IngestAsync("key", request, default));
        Assert.Empty(await store.ListAsync(default));
    }

    [Fact]
    public async Task SuccessfulSubmissionPreservesBusinessCorrelations()
    {
        var created = await service.IngestAsync("key", Input(), default);
        var payer = new StubPayer(new("Accepted", "PAY-test"));
        await Processor(payer).ProcessNextAsync(default);
        var workflow = (await store.GetAsync(created.Workflow.Correlation.WorkflowId, default))!.Workflow;
        Assert.Equal(WorkflowStage.Submitted, workflow.Stage);
        Assert.Equal(created.Workflow.Claim.Id, payer.Submissions.Single().ClaimId);
        Assert.Equal("PAY-test", workflow.Correlation.PayerClaimId);
        Assert.Null(workflow.Correlation.TraceNumber);
        Assert.Null(workflow.Correlation.PaymentReference);
        Assert.Throws<InvalidOperationException>(() => workflow.MoveTo(WorkflowStage.SubmissionPending, clock.GetUtcNow(), "Invalid"));
        Assert.False(await Processor(payer).ProcessNextAsync(default));
    }

    [Fact]
    public async Task TransientRetriesAreScheduledAndBounded()
    {
        var created = await service.IngestAsync("key", Input(), default);
        var payer = new StubPayer(new("Timeout", Transient: true));
        var processor = Processor(payer);
        await processor.ProcessNextAsync(default);
        var first = (await store.GetAsync(created.Workflow.Correlation.WorkflowId, default))!.Workflow;
        Assert.Equal(WorkflowStage.RetryScheduled, first.Stage);
        Assert.InRange((first.NextAttemptAt - clock.GetUtcNow()).TotalSeconds, 1, 2);
        Assert.False(await processor.ProcessNextAsync(default));
        clock.Advance(31);
        await processor.ProcessNextAsync(default);
        var final = (await store.GetAsync(created.Workflow.Correlation.WorkflowId, default))!.Workflow;
        Assert.Equal(WorkflowStage.ManualReview, final.Stage);
        Assert.Equal(2, final.Attempts.Length);
        Assert.Equal("RetryBudgetExhausted", final.History[^1].Reason);
    }

    [Fact]
    public async Task PermanentFailureNeedsExplicitRecoveryAndKeepsClaimIdentity()
    {
        var created = await service.IngestAsync("key", Input(), default);
        var rejected = new StubPayer(new("PayerRejected"));
        await Processor(rejected).ProcessNextAsync(default);
        var stored = (await store.GetAsync(created.Workflow.Correlation.WorkflowId, default))!.Workflow;
        Assert.Equal(WorkflowStage.ManualReview, stored.Stage);
        Assert.Single(stored.Attempts);
        await Assert.ThrowsAsync<InputException>(() => service.RecoverAsync(stored.Correlation.WorkflowId, new("free text"), default));
        await service.RecoverAsync(stored.Correlation.WorkflowId, new("PayerRestored"), default);
        await Assert.ThrowsAsync<ConflictException>(() => service.RecoverAsync(stored.Correlation.WorkflowId, new("PayerRestored"), default));
        var accepted = new StubPayer(new("Accepted", "PAY-test"));
        await Processor(accepted).ProcessNextAsync(default);
        var final = (await store.GetAsync(stored.Correlation.WorkflowId, default))!.Workflow;
        Assert.Equal(WorkflowStage.Submitted, final.Stage);
        Assert.Equal(1, final.RecoveryCycle);
        Assert.Equal(rejected.Submissions.Single().ClaimId, accepted.Submissions.Single().ClaimId);
        Assert.Equal(2, final.Attempts.Length);
    }

    [Fact]
    public async Task LeasePreventsConcurrentProcessingAndFencesStaleWrites()
    {
        await service.IngestAsync("key", Input(), default);
        var acquisitions = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.AcquireAsync(clock.GetUtcNow(), default)));
        var first = Assert.Single(acquisitions, a => a is not null)!;
        clock.Advance(31);
        var second = await store.AcquireAsync(clock.GetUtcNow(), default);
        Assert.NotNull(second);
        Assert.Null(await store.SaveAsync(first, first.Workflow, true, default));
        Assert.NotNull(await store.SaveAsync(second, second.Workflow, true, default));
    }

    [Fact]
    public async Task RestartRecoversInFlightAttemptWithoutResettingBudget()
    {
        var created = await service.IngestAsync("key", Input(), default);
        var leased = (await store.AcquireAsync(clock.GetUtcNow(), default))!;
        var interrupted = leased.Workflow with { Attempts = [new(1, 0, clock.GetUtcNow())] };
        await store.SaveAsync(leased, interrupted, false, default);
        clock.Advance(31);
        var reopened = new WorkflowStore(factory);
        var payer = new StubPayer(new("Accepted", "PAY-existing"));
        await new WorkflowProcessor(reopened, payer, clock, new() { MaxAttempts = 2 }).ProcessNextAsync(default);
        var workflow = (await reopened.GetAsync(created.Workflow.Correlation.WorkflowId, default))!.Workflow;
        Assert.Equal("UnknownAfterRestart", workflow.Attempts[0].Outcome);
        Assert.Equal(2, workflow.Attempts.Length);
        Assert.Equal(created.Workflow.Claim.Id, payer.Submissions.Single().ClaimId);
    }

    [Fact]
    public async Task RestartWithExhaustedBudgetDoesNotSendAgain()
    {
        var created = await service.IngestAsync("key", Input(), default);
        var leased = (await store.AcquireAsync(clock.GetUtcNow(), default))!;
        await store.SaveAsync(leased, leased.Workflow with { Attempts = [new(1, 0, clock.GetUtcNow()), new(2, 0, clock.GetUtcNow())] }, false, default);
        clock.Advance(31);
        var payer = new StubPayer(new("Accepted", "PAY-existing"));
        await Processor(payer).ProcessNextAsync(default);
        Assert.Empty(payer.Submissions);
        Assert.Equal(WorkflowStage.ManualReview, (await store.GetAsync(created.Workflow.Correlation.WorkflowId, default))!.Workflow.Stage);
    }

    [Fact]
    public async Task TelemetryEmitsOutcomeWithoutClinicalPayloadOrMetricIdentifiers()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == LabTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        var tags = new List<string>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == LabTelemetry.Name) l.EnableMeasurementEvents(instrument); };
        meter.SetMeasurementEventCallback<long>((_, _, values, _) => { foreach (var tag in values) tags.Add(tag.Key); });
        meter.Start();
        await service.IngestAsync("key", Input(), default);
        await Processor(new(new("Accepted", "PAY-test"))).ProcessNextAsync(default);
        Assert.Single(activities);
        var serialized = JsonSerializer.Serialize(activities[0].TagObjects);
        Assert.DoesNotContain("syn-patient", serialized);
        Assert.DoesNotContain("DEMO001", serialized);
        Assert.Equal(["outcome"], tags);
    }

    [Fact]
    public void EducationalClaimHasConsistentTransactionCount()
    {
        var input = Input();
        var claim = ClaimWriter.Create(ClinicalInput.Validate(input), input.Billing, "CL123456789012345678", clock.GetUtcNow());
        var segments = claim.Artifact.Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var start = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var end = Array.FindIndex(segments, s => s.StartsWith("SE*"));
        Assert.Equal(end - start + 1, int.Parse(segments[end].Split('*')[1]));
        Assert.Equal(105, segments[0].Length);
    }

    private WorkflowProcessor Processor(StubPayer payer) => new(store, payer, clock, new() { MaxAttempts = 2 });
    private static IngestRequest Input() => new(true, new("Patient", "syn-patient-001"),
        new("Encounter", "syn-encounter-001", "finished", new("Patient/syn-patient-001"),
            new("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB"),
            new(DateTimeOffset.Parse("2026-10-24T09:00:00Z"), DateTimeOffset.Parse("2026-10-24T09:30:00Z"))),
        new("Z00.00", [new("DEMO001", 1, 125.50m)]));

    private sealed class TestFactory(DbContextOptions<WorkflowDb> options) : IDbContextFactory<WorkflowDb>
    {
        public WorkflowDb CreateDbContext() => new(options);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed class StubPayer(PayerResult result) : IPayerClient
    {
        public List<PayerSubmission> Submissions { get; } = [];
        public Task<PayerResult> SubmitAsync(PayerSubmission submission, CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            return Task.FromResult(result);
        }
    }
}
