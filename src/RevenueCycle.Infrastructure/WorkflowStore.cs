using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RevenueCycle.Application;
using RevenueCycle.Domain;

namespace RevenueCycle.Infrastructure;

public sealed class WorkflowRow
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string EncounterId { get; set; } = "";
    public string Document { get; set; } = "";
    public string Version { get; set; } = "";
    public WorkflowStage Stage { get; set; }
    public long DueAt { get; set; }
    public long LeaseUntil { get; set; }
}

public sealed class WorkflowDb(DbContextOptions<WorkflowDb> options) : DbContext(options)
{
    public DbSet<WorkflowRow> Workflows => Set<WorkflowRow>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<WorkflowRow>();
        row.HasKey(w => w.Id);
        row.HasIndex(w => w.Key).IsUnique();
        row.HasIndex(w => w.EncounterId).IsUnique();
        row.HasIndex(w => new { w.Stage, w.DueAt });
    }
}

public sealed class WorkflowStore(IDbContextFactory<WorkflowDb> factory) : IWorkflowStore
{
    public async Task<IngestionResult> CreateAsync(string key, string fingerprint, Workflow workflow, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Workflows.Add(new WorkflowRow
        {
            Id = workflow.Correlation.WorkflowId, Key = key, Fingerprint = fingerprint,
            EncounterId = workflow.Clinical.EncounterId, Document = JsonSerializer.Serialize(workflow),
            Version = Guid.NewGuid().ToString("N"), Stage = workflow.Stage, DueAt = workflow.NextAttemptAt.ToUnixTimeMilliseconds()
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(workflow, true);
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.ChangeTracker.Clear();
            var existing = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Key == key, cancellationToken);
            if (existing is not null && existing.Fingerprint == fingerprint) return new(Decode(existing).Workflow, false);
            throw new ConflictException(existing is null ? "Encounter already belongs to another ingestion key." : "Idempotency-Key was already used with different input.");
        }
    }

    public async Task<StoredWorkflow?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, cancellationToken);
        return row is null ? null : Decode(row);
    }

    public async Task<Workflow[]> ListAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Workflows.AsNoTracking().OrderByDescending(w => w.DueAt).Take(100).ToArrayAsync(cancellationToken);
        return rows.Select(row => Decode(row).Workflow).ToArray();
    }

    public async Task<StoredWorkflow?> AcquireAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var timestamp = now.ToUnixTimeMilliseconds();
        var candidate = await db.Workflows.AsNoTracking().Where(w =>
            (w.Stage == WorkflowStage.SubmissionPending || w.Stage == WorkflowStage.RetryScheduled) &&
            w.DueAt <= timestamp && w.LeaseUntil <= timestamp).OrderBy(w => w.DueAt).FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return null;
        var version = Guid.NewGuid().ToString("N");
        var changed = await db.Workflows.Where(w => w.Id == candidate.Id && w.Version == candidate.Version)
            .ExecuteUpdateAsync(update => update.SetProperty(w => w.Version, version)
                .SetProperty(w => w.LeaseUntil, timestamp + 30000), cancellationToken);
        return changed == 1 ? Decode(candidate) with { Version = version } : null;
    }

    public async Task<StoredWorkflow?> SaveAsync(StoredWorkflow original, Workflow updated, bool releaseLease, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var version = Guid.NewGuid().ToString("N");
        var document = JsonSerializer.Serialize(updated);
        var due = updated.NextAttemptAt.ToUnixTimeMilliseconds();
        var changed = await db.Workflows.Where(w => w.Id == updated.Correlation.WorkflowId && w.Version == original.Version)
            .ExecuteUpdateAsync(update => update.SetProperty(w => w.Document, document).SetProperty(w => w.Version, version)
                .SetProperty(w => w.Stage, updated.Stage).SetProperty(w => w.DueAt, due)
                .SetProperty(w => w.LeaseUntil, w => releaseLease ? 0 : w.LeaseUntil), cancellationToken);
        return changed == 1 ? new(updated, version) : null;
    }

    private static StoredWorkflow Decode(WorkflowRow row) => new(
        JsonSerializer.Deserialize<Workflow>(row.Document) ?? throw new InvalidOperationException("Invalid workflow document."), row.Version);
}
