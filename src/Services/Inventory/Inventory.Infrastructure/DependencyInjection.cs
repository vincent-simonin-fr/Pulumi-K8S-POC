using Inventory.Application.Common.Interfaces;
using Inventory.Application.EventHandlers;
using Inventory.Infrastructure.BackgroundServices;
using Inventory.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── EF Core / PostgreSQL ──────────────────────────────────────────────
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("InventoryDb"),
                b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        services.AddScoped<IApplicationDbContext>(p =>
            p.GetRequiredService<ApplicationDbContext>());

        // ── Cache distribué ───────────────────────────────────────────────────
        // Redis si la connection string est configurée, MemoryCache sinon (dev local).
        // La même interface IDistributedCache est utilisée partout — aucun code
        // applicatif ne connaît l'implémentation sous-jacente.
        var redisCs = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisCs))
        {
            services.AddStackExchangeRedisCache(opts =>
            {
                opts.Configuration = redisCs;
                opts.InstanceName   = "inventory:";   // préfixe toutes les clés Redis
            });
        }
        else
        {
            // Fallback pour dev local sans Redis (podman-compose, tests)
            services.AddDistributedMemoryCache();
        }

        // ── MassTransit / RabbitMQ ────────────────────────────────────────────
        services.AddMassTransit(x =>
        {
            // KebabCaseEndpointNameFormatter :
            //   ProductAddedToCartConsumer → queue "product-added-to-cart"
            // ⚠️ DefaultEndpointNameFormatter produirait "ProductAddedToCart" (PascalCase).
            // Le nom kebab-case est celui configuré dans KEDA (keda:queueName dans Pulumi.dev.yaml).
            x.SetKebabCaseEndpointNameFormatter();

            x.AddConsumer<ProductAddedToCartConsumer>();

            x.UsingRabbitMq((ctx, cfg) =>
            {
                var rmq = configuration.GetSection("RabbitMQ");
                cfg.Host(rmq["Host"] ?? "localhost", rmq["VirtualHost"] ?? "/", h =>
                {
                    h.Username(rmq["Username"] ?? "guest");
                    h.Password(rmq["Password"] ?? "guest");
                });

                cfg.ConfigureEndpoints(ctx);
            });
        });

        // ── Background service de réservation ─────────────────────────────────
        services.AddHostedService<ReservationExpiryService>();

        return services;
    }
}
