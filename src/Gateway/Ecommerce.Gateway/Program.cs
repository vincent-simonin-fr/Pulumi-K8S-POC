using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;
using Serilog.Formatting.Compact;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

    // ── YARP ──────────────────────────────────────────────────────────────────
    builder.Services
        .AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

    // ── Health checks ──────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddUrlGroup(new Uri("http://order-api:8080/health"), name: "order-api", tags: ["upstream"])
        .AddUrlGroup(new Uri("http://inventory-api:8080/health"), name: "inventory-api", tags: ["upstream"]);

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapReverseProxy();

    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/upstream", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("upstream")
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
