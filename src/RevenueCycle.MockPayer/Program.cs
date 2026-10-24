using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RevenueCycle.Application;
using RevenueCycle.Infrastructure;
using RevenueCycle.MockPayer;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddHealthChecks();
var path = ServiceRegistration.DatabasePath(builder.Configuration, "payer.db");
builder.Services.AddDbContext<PayerDb>(options => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString()));
var mode = builder.Configuration["Payer:Mode"] ?? "Accept";
if (mode is not ("Accept" or "Unavailable" or "Reject" or "AcceptThenTimeout"))
    throw new InvalidOperationException("Unknown mock payer mode.");
builder.AddLabTelemetry("mock-payer");
var app = builder.Build();
using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<PayerDb>().Database.EnsureCreatedAsync();
app.MapHealthChecks("/health");
app.MapGet("/", () => new
{
    name = "Synthetic Mock Payer", service = "mock-payer", stage = "phase-1b",
    syntheticDataOnly = true, claimSubmissionAvailable = true, mode
});
app.MapGet("/claims", async (PayerDb db, CancellationToken token) =>
    Results.Ok(await db.Receipts.AsNoTracking().Select(r => new { r.ClaimId, r.PayerClaimId }).ToArrayAsync(token)));
app.MapPost("/claims", async (PayerSubmission submission, HttpRequest request, PayerDb db, CancellationToken token) =>
{
    if (submission.ClaimId is null || !System.Text.RegularExpressions.Regex.IsMatch(submission.ClaimId, "^CL[a-f0-9]{18}\\z") ||
        request.Headers["Idempotency-Key"].ToString() != submission.ClaimId ||
        submission.EncounterId?.StartsWith("syn-", StringComparison.Ordinal) != true ||
        submission.Currency != "USD" || submission.Amount <= 0 || submission.Artifact is not { Length: > 0 and < 50000 })
        return Results.BadRequest(new { error = "Invalid synthetic submission." });
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(submission))));
    var existing = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r => r.ClaimId == submission.ClaimId, token);
    if (existing is not null) return Receipt(existing, fingerprint);
    if (mode == "Unavailable") return Results.StatusCode(503);
    if (mode == "Reject") return Results.UnprocessableEntity(new { error = "Simulated permanent rejection." });
    var row = new ReceiptRow { ClaimId = submission.ClaimId, Fingerprint = fingerprint, PayerClaimId = "PAY" + Guid.NewGuid().ToString("N") };
    db.Receipts.Add(row);
    try { await db.SaveChangesAsync(token); }
    catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
    {
        db.ChangeTracker.Clear();
        return Receipt(await db.Receipts.AsNoTracking().SingleAsync(r => r.ClaimId == submission.ClaimId, token), fingerprint);
    }
    if (mode == "AcceptThenTimeout") await Task.Delay(TimeSpan.FromSeconds(10), token);
    return Receipt(row, fingerprint);
});
app.Run();

static IResult Receipt(ReceiptRow row, string fingerprint) => row.Fingerprint == fingerprint
    ? Results.Ok(new PayerReceipt(row.ClaimId, row.PayerClaimId))
    : Results.Conflict(new { error = "Submission key conflicts with the original payload." });
