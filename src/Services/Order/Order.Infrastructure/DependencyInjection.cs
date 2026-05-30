using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Order.Application.Common.Interfaces;
using Order.Application.EventHandlers;
using Order.Domain.Exceptions;
using Order.Infrastructure.Persistence;

namespace Order.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── EF Core / PostgreSQL ──────────────────────────────────────────────
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("OrderDb"),
                b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        services.AddScoped<IApplicationDbContext>(p =>
            p.GetRequiredService<ApplicationDbContext>());

        // ── MassTransit / RabbitMQ ────────────────────────────────────────────
        services.AddMassTransit(x =>
        {
            // Retry sélectif : uniquement les exceptions techniques.
            // DomainException (InvalidCartOperationException) → pas de retry :
            // l'item n'est plus dans le panier — état cohérent, retenter inutile.
            x.AddConsumer<ProductReservationExpiredConsumer>(cfg =>
            {
                cfg.UseMessageRetry(r =>
                {
                    r.Incremental(retryLimit: 3,
                                  initialInterval: TimeSpan.FromSeconds(1),
                                  intervalIncrement: TimeSpan.FromSeconds(2));
                    r.Ignore<DomainException>();
                });
            });

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

        return services;
    }
}
