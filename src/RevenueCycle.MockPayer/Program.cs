var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapGet("/", () => new
{
    name = "Synthetic Mock Payer",
    service = "mock-payer",
    stage = "scaffold",
    syntheticDataOnly = true,
    claimSubmissionAvailable = false
});
app.Run();
