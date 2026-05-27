using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Order.Api.Endpoints;
using Order.Application.Carts.Commands.AddToCart;
using Order.Application.Common.Behaviours;
using Order.Infrastructure;
using Order.Infrastructure.Persistence;
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
            document.Info.Title   = "Order API";
            document.Info.Version = "v1";
            document.Info.Description = "API de gestion des paniers et commandes.";
            return Task.CompletedTask;
        });
    });

    // ── MediatR + Validation pipeline ─────────────────────────────────────────
    builder.Services.AddMediatR(cfg =>
        cfg.RegisterServicesFromAssembly(typeof(AddToCartCommand).Assembly));

    builder.Services.AddValidatorsFromAssembly(typeof(AddToCartCommand).Assembly);
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
    builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehaviour<,>));

    // ── Infrastructure (EF + MassTransit) ─────────────────────────────────────
    builder.Services.AddInfrastructure(builder.Configuration);

    // ── OpenTelemetry ──────────────────────────────────────────────────────────
    var serviceName = builder.Configuration["OpenTelemetry:ServiceName"] ?? "order-api";
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
            builder.Configuration.GetConnectionString("OrderDb")!,
            name: "order-db",
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
        app.MapScalarApiReference(options => options.WithTitle("Order API"));
    }

    // ── Migrations au démarrage (tous environnements) ─────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.MigrateAsync();
    }

    // ── Endpoints ─────────────────────────────────────────────────────────────
    app.MapCartEndpoints();
    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/ready", new()
    {
        Predicate = check => check.Tags.Contains("db") || check.Tags.Contains("messaging")
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Order API terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Make Program accessible for integration tests
public partial class Program { }
