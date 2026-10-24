using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RevenueCycle.Application;
using RevenueCycle.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.AddRevenueCycle(builder.Configuration);
builder.AddLabTelemetry("revenue-cycle-api");

var app = builder.Build();
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<WorkflowDb>>().CreateDbContextAsync())
    await db.Database.EnsureCreatedAsync();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (Exception exception) when (exception is InputException or ConflictException or KeyNotFoundException)
    {
        var status = exception switch { InputException => 400, ConflictException => 409, _ => 404 };
        await Results.Problem(statusCode: status, title: status == 404 ? "Workflow not found." : exception.Message).ExecuteAsync(context);
    }
});
app.MapHealthChecks("/health");
app.MapOpenApi();
app.MapGet("/", () => new
{
    name = "Healthcare Revenue Cycle Integration Lab", service = "revenue-cycle-api",
    stage = LabCapabilities.ImplementationStage, syntheticDataOnly = true,
    workflowProcessingAvailable = LabCapabilities.WorkflowProcessingAvailable
});
app.MapPost("/workflows", async (IngestRequest input, HttpRequest request, WorkflowService service, CancellationToken token) =>
{
    var result = await service.IngestAsync(request.Headers["Idempotency-Key"].ToString(), input, token);
    return result.Created ? Results.Created($"/workflows/{result.Workflow.Correlation.WorkflowId}", result.Workflow) : Results.Ok(result.Workflow);
});
app.MapGet("/workflows", async (IWorkflowStore store, CancellationToken token) =>
    Results.Ok((await store.ListAsync(token)).Select(w => new { w.Correlation, w.Stage, w.CreatedAt, w.NextAttemptAt, AttemptCount = w.Attempts.Length })));
app.MapGet("/workflows/{id:guid}", async (Guid id, IWorkflowStore store, CancellationToken token) =>
    await store.GetAsync(id, token) is { } stored ? Results.Ok(stored.Workflow) : Results.NotFound());
app.MapGet("/workflows/{id:guid}/claim", async (Guid id, IWorkflowStore store, CancellationToken token) =>
    await store.GetAsync(id, token) is { } stored ? Results.Text(stored.Workflow.Claim.Artifact, "text/plain") : Results.NotFound());
app.MapPost("/workflows/{id:guid}/recover", async (Guid id, RecoveryRequest request, WorkflowService service, CancellationToken token) =>
    Results.Ok(await service.RecoverAsync(id, request, token)));
app.Run();
