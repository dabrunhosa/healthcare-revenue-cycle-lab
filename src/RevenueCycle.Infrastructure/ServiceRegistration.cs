using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using RevenueCycle.Application;

namespace RevenueCycle.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddRevenueCycle(this IServiceCollection services, IConfiguration config)
    {
        var path = DatabasePath(config, "workflows.db");
        services.AddDbContextFactory<WorkflowDb>(options => options.UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString()));
        var workflowOptions = config.GetSection("Workflow").Get<WorkflowOptions>() ?? new();
        workflowOptions.Validate();
        services.AddSingleton(workflowOptions);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWorkflowStore, WorkflowStore>();
        services.AddTransient<WorkflowService>();
        services.AddTransient<WorkflowProcessor>();
        var payerUrl = new Uri(config["Payer:BaseUrl"] ?? "http://127.0.0.1:5222");
        if (payerUrl.Scheme != "http" || (!payerUrl.IsLoopback && payerUrl.Host != "mock-payer"))
            throw new InvalidOperationException("Payer must be a local mock endpoint.");
        services.AddHttpClient<IPayerClient, PayerClient>(client =>
        {
            client.BaseAddress = payerUrl;
            client.Timeout = TimeSpan.FromSeconds(5);
            client.MaxResponseContentBufferSize = 65536;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
            .AddResilienceHandler("payer", pipeline =>
            {
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = 0.5, MinimumThroughput = 3,
                    SamplingDuration = TimeSpan.FromSeconds(30), BreakDuration = TimeSpan.FromSeconds(10)
                });
                pipeline.AddTimeout(TimeSpan.FromSeconds(3));
            });
        services.AddHostedService<WorkflowWorker>();
        return services;
    }

    public static string DatabasePath(IConfiguration configuration, string fileName)
    {
        var directory = configuration["Storage:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, fileName);
    }

    public static IHostApplicationBuilder AddLabTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        var telemetry = builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService(serviceName));
        telemetry.WithTracing(tracing =>
        {
            tracing.AddSource(LabTelemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
            if (builder.Configuration.GetValue<bool>("Telemetry:Console")) tracing.AddConsoleExporter();
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter(LabTelemetry.Name).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
            if (builder.Configuration.GetValue<bool>("Telemetry:Console")) metrics.AddConsoleExporter();
        });
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) telemetry.UseOtlpExporter();
        return builder;
    }
}

public sealed class WorkflowWorker(IServiceScopeFactory scopes, ILogger<WorkflowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<WorkflowProcessor>().ProcessNextAsync(stoppingToken)) continue;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError("Workflow worker failed: {ErrorType}", exception.GetType().Name); }
            try { await Task.Delay(250, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
