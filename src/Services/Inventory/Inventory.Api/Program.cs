using Inventory.Api.Endpoints;
using Inventory.Application.EventHandlers;
using Inventory.Application.Reservations.Commands.ReserveProduct;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

// ── Bootstrap Serilog ──────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ────────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console(new RenderedCompactJsonFormatter()));

    // ── OpenAPI ────────────────────────────────────────────────────────────────
    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title   = "Inventory API";
            document.Info.Version = "v1";
            document.Info.Description = "API de gestion du stock et des réservations produit.";
            return Task.CompletedTask;
        });
    });

    // ── MediatR ────────────────────────────────────────────────────────────────
    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssemblies(
            typeof(ReserveProductCommand).Assembly,
            typeof(ProductAddedToCartConsumer).Assembly));

    // ── Infrastructure (EF + MassTransit + BackgroundService) ─────────────────
    builder.Services.AddInfrastructure(builder.Configuration);

    // ── OpenTelemetry ──────────────────────────────────────────────────────────
    var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "inventory-api";
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(serviceName))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddOtlpExporter(o =>
                o.Endpoint = new Uri(builder.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317")))
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o =>
                o.Endpoint = new Uri(builder.Configuration["OpenTelemetry:Endpoint"] ?? "http://localhost:4317")));

    // ── Health checks ──────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddNpgSql(
            builder.Configuration.GetConnectionString("InventoryDb")!,
            name: "inventory-db",
            tags: ["db", "postgres"])
        .AddRabbitMQ(
            (sp) => {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var connectionFactory = new ConnectionFactory();
                connectionFactory.HostName = builder.Configuration["RabbitMq:Host"]!;
                connectionFactory.UserName = builder.Configuration["RabbitMq:Username"]!;
                connectionFactory.Password = builder.Configuration["RabbitMq:Password"]!;
                connectionFactory.RequestedConnectionTimeout = TimeSpan.FromSeconds(3);
                return connectionFactory.CreateConnectionAsync();
            },
            name: "rabbitmq",
            tags: ["messaging"]);

    // ── Problem Details ────────────────────────────────────────────────────────
    builder.Services.AddProblemDetails();

    var app = builder.Build();

    // ── Middleware ─────────────────────────────────────────────────────────────
    app.UseSerilogRequestLogging();
    app.UseExceptionHandler();
    app.UseStatusCodePages();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options => options.WithTitle("Inventory API"));
    }

    // ── Migrations auto au démarrage (dev only) ────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────
    app.MapProductEndpoints();
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/ready", new()
    {
        Predicate = check => check.Tags.Contains("db") || check.Tags.Contains("messaging")
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Inventory API terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program { }
