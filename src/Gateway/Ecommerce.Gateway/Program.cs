using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
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

    // ── OpenTelemetry ──────────────────────────────────────────────────────────
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            builder.Configuration["OpenTelemetry:ServiceName"] ?? "gateway"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o =>
                o.Endpoint = new Uri(
                    builder.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317")));

    // ── Health checks ──────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddUrlGroup(new Uri("http://order-api:8080/health"), name: "order-api", tags: ["upstream"])
        .AddUrlGroup(new Uri("http://inventory-api:8080/health"), name: "inventory-api", tags: ["upstream"]);

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    app.MapReverseProxy();

    // ── Health endpoints ─────────────────────────────────────────────────────
    // Probes K8s = "shallow" : la gateway répond-elle ? Elles ne doivent JAMAIS
    // dépendre de la santé de l'aval — sinon une panne d'order-api/inventory-api
    // ferait crashlooper la gateway (liveness) et la sortirait du Service
    // (readiness), propageant l'incident au lieu de le contenir.
    //
    // liveness : le process vit-il ? (aucun check → 200 dès que l'app répond)
    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    // readiness : dépendances PROPRES de la gateway (on exclut les checks "upstream")
    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => !check.Tags.Contains("upstream")
    });

    // Agrégat (process + santé des upstreams) : pour humains / dashboards /
    // monitoring synthétique — NON câblé aux probes. YARP gère déjà le 503 par
    // route via son HealthCheck actif/passif par cluster.
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
    await Log.CloseAndFlushAsync();
    Environment.Exit(1);
}
finally
{
    await Log.CloseAndFlushAsync();
}
