using Ecommerce.Contracts.Events;
using Inventory.Application.Common;
using Inventory.Application.Reservations.Commands.ReserveProduct;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.EventHandlers;

/// <summary>
/// Consomme l'événement ProductAddedToCartEvent publié par OrderApi.
/// Déclenche la réservation du stock via MediatR, puis invalide le cache
/// produits pour que le prochain GET /inventory reflète le nouveau stock réservé.
/// </summary>
public sealed class ProductAddedToCartConsumer(
    ISender sender,
    IDistributedCache cache,
    ILogger<ProductAddedToCartConsumer> logger)
    : IConsumer<ProductAddedToCartEvent>
{
    public async Task Consume(ConsumeContext<ProductAddedToCartEvent> context)
    {
        var evt = context.Message;

        logger.LogInformation(
            "Received ProductAddedToCartEvent: Cart={CartId}, Product={ProductId}, Qty={Quantity}",
            evt.CartId, evt.ProductId, evt.Quantity);

        var result = await sender.Send(new ReserveProductCommand
        {
            ProductId = evt.ProductId,
            CartId    = evt.CartId,
            Quantity  = evt.Quantity
        }, context.CancellationToken);

        logger.LogInformation(
            "Reservation {ReservationId} created, expires at {ExpiresAt}",
            result.ReservationId, result.ExpiresAt);

        // Invalider le cache produits : availableQuantity vient de changer
        try
        {
            await cache.RemoveAsync(CacheKeys.ProductsAll, context.CancellationToken);
            logger.LogDebug("Cache '{Key}' invalidated after reservation.", CacheKeys.ProductsAll);
        }
        catch (Exception ex)
        {
            // Ne pas bloquer le traitement si Redis est indisponible
            logger.LogWarning(ex, "Failed to invalidate cache '{Key}' — will expire naturally.", CacheKeys.ProductsAll);
        }
    }
}
