using RevenueCycle.Application;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapGet("/", () => new
{
    name = "Healthcare Revenue Cycle Integration Lab",
    service = "revenue-cycle-api",
    stage = LabCapabilities.ImplementationStage,
    syntheticDataOnly = true,
    workflowProcessingAvailable = LabCapabilities.WorkflowProcessingAvailable
});
app.Run();
