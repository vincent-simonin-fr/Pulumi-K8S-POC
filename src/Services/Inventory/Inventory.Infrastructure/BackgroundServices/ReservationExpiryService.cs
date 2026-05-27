using Ecommerce.Contracts.Events;
using Inventory.Domain.Enums;
using Inventory.Infrastructure.Persistence;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.BackgroundServices;

/// <summary>
/// Service de fond qui détecte les réservations expirées, libère le stock
/// et publie un ProductReservationExpiredEvent vers OrderApi.
/// L'intervalle de vérification est configurable via Reservation:CheckIntervalSeconds.
/// </summary>
public sealed class ReservationExpiryService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ReservationExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var checkInterval = TimeSpan.FromSeconds(
            configuration.GetValue<int>("Reservation:CheckIntervalSeconds", 30));

        logger.LogInformation(
            "ReservationExpiryService started. Check interval: {Interval}s", checkInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessExpiredReservationsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error processing expired reservations.");
            }

            await Task.Delay(checkInterval, stoppingToken);
        }
    }

    private async Task ProcessExpiredReservationsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext       = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var publishEndpoint = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var expiredReservations = await dbContext.Reservations
            .Where(r => r.Status == ReservationStatus.Active && r.ExpiresAt <= DateTimeOffset.UtcNow)
            .ToListAsync(cancellationToken);

        if (expiredReservations.Count == 0) return;

        logger.LogInformation("Processing {Count} expired reservation(s).", expiredReservations.Count);

        // Charger tous les produits concernés en une seule requête
        var productIds = expiredReservations.Select(r => r.ProductId).Distinct().ToList();
        var products = await dbContext.Products
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var reservation in expiredReservations)
        {
            if (products.TryGetValue(reservation.ProductId, out var product))
                product.ReleaseReservation(reservation.Quantity);

            reservation.Expire();

            await publishEndpoint.Publish(new ProductReservationExpiredEvent
            {
                ReservationId = reservation.Id,
                CartId        = reservation.CartId,
                ProductId     = reservation.ProductId,
                Quantity      = reservation.Quantity,
                ExpiredAt     = DateTimeOffset.UtcNow
            }, cancellationToken);

            logger.LogWarning(
                "Reservation {ReservationId} expired. Stock released for Product {ProductId}.",
                reservation.Id, reservation.ProductId);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
