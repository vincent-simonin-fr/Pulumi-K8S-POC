using Ecommerce.Contracts.Events;
using Inventory.Application.Common;
using Inventory.Application.Reservations.Commands.ReserveProduct;
using Inventory.Domain.Exceptions;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.EventHandlers;

/// <summary>
/// Consomme l'événement ProductAddedToCartEvent publié par OrderApi.
/// Déclenche la réservation du stock via MediatR, puis invalide le cache
/// produits pour que le prochain GET /inventory reflète le nouveau stock réservé.
///
/// Gestion des exceptions :
///   DomainException (InsufficientStock, ProductNotFound)
///     → exception MÉTIER : pas de retry, pas de dead letter queue.
///     → publie un ProductReservationFailedEvent pour notification aval.
///
///   Exception technique (DB down, timeout réseau...)
///     → laissée remonter → MassTransit retry (3×) → error queue en dernier recours.
///
/// Ce design évite de polluer la queue d'erreur avec des cas business normaux
/// (stock épuisé lors d'un spike) tout en conservant la visibilité sur les vrais
/// problèmes techniques via la dead letter queue.
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

        try
        {
            var result = await sender.Send(new ReserveProductCommand
            {
                ProductId = evt.ProductId,
                CartId    = evt.CartId,
                Quantity  = evt.Quantity
            }, context.CancellationToken);

            logger.LogInformation(
                "Reservation {ReservationId} created for Cart={CartId}, expires at {ExpiresAt}",
                result.ReservationId, evt.CartId, result.ExpiresAt);

            // Invalider le cache produits : availableQuantity vient de changer
            await InvalidateCacheAsync(context.CancellationToken);
        }
        catch (InsufficientStockException ex)
        {
            // Exception MÉTIER — le stock est épuisé.
            // Ce n'est pas une erreur technique : inutile de retenter (le stock
            // ne reviendra pas entre deux tentatives). On publie un événement
            // compensatoire à la place de laisser MassTransit envoyer en error queue.
            logger.LogWarning(
                "Insufficient stock for Product={ProductId} in Cart={CartId}: " +
                "requested={Requested}, available={Available}",
                evt.ProductId, evt.CartId, ex.RequestedQuantity, ex.AvailableQuantity);

            await context.Publish(new ProductReservationFailedEvent
            {
                CorrelationId     = evt.CorrelationId,
                CartId            = evt.CartId,
                ProductId         = evt.ProductId,
                ProductName       = evt.ProductName,
                RequestedQuantity = ex.RequestedQuantity,
                AvailableQuantity = ex.AvailableQuantity,
                FailureReason     = ReservationFailureReason.InsufficientStock,
                Reason            = ex.Message,
                OccurredAt        = DateTimeOffset.UtcNow
            }, context.CancellationToken);

            // Ne pas relancer → pas de retry, pas de dead letter queue
        }
        catch (ProductNotFoundException ex)
        {
            // Exception MÉTIER — le produit n'existe pas.
            // Même logique : retry inutile, événement compensatoire.
            logger.LogWarning(
                "Product={ProductId} not found while processing Cart={CartId}: {Message}",
                evt.ProductId, evt.CartId, ex.Message);

            await context.Publish(new ProductReservationFailedEvent
            {
                CorrelationId     = evt.CorrelationId,
                CartId            = evt.CartId,
                ProductId         = evt.ProductId,
                ProductName       = evt.ProductName,
                RequestedQuantity = evt.Quantity,
                AvailableQuantity = 0,
                FailureReason     = ReservationFailureReason.ProductNotFound,
                Reason            = ex.Message,
                OccurredAt        = DateTimeOffset.UtcNow
            }, context.CancellationToken);

            // Ne pas relancer → pas de retry, pas de dead letter queue
        }
        // Les exceptions techniques (DB, réseau) remontent naturellement :
        // MassTransit applique le retry configuré dans DependencyInjection,
        // puis envoie en error queue si toutes les tentatives échouent.
    }

    private async Task InvalidateCacheAsync(CancellationToken ct)
    {
        try
        {
            await cache.RemoveAsync(CacheKeys.ProductsAll, ct);
            logger.LogDebug("Cache '{Key}' invalidated after reservation.", CacheKeys.ProductsAll);
        }
        catch (Exception ex)
        {
            // Ne pas bloquer le traitement si Redis est indisponible
            logger.LogWarning(ex, "Failed to invalidate cache '{Key}' — will expire naturally.", CacheKeys.ProductsAll);
        }
    }
}
