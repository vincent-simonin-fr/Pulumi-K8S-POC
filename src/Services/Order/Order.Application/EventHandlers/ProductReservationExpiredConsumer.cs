using Ecommerce.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Order.Application.Common.Interfaces;
using Order.Domain.Exceptions;

namespace Order.Application.EventHandlers;

/// <summary>
/// Consomme l'événement d'expiration de réservation publié par InventoryApi.
/// Retire l'item du panier lorsque la réservation de stock a expiré.
///
/// Gestion des exceptions :
///   DomainException (InvalidCartOperationException, CartNotFoundException)
///     → exception MÉTIER : le panier ou l'item n'existe plus — état cohérent.
///       Pas de retry, pas de dead letter queue.
///       Cas normaux : panier déjà expiré, item déjà retiré, panier checkout.
///
///   Exception technique (DB down, timeout réseau...)
///     → laissée remonter → MassTransit retry (3×) → error queue en dernier recours.
/// </summary>
public sealed class ProductReservationExpiredConsumer(
    IApplicationDbContext dbContext,
    ILogger<ProductReservationExpiredConsumer> logger)
    : IConsumer<ProductReservationExpiredEvent>
{
    public async Task Consume(ConsumeContext<ProductReservationExpiredEvent> context)
    {
        var evt = context.Message;

        logger.LogInformation(
            "Processing reservation expiry for Product={ProductId} Cart={CartId} Reservation={ReservationId}",
            evt.ProductId, evt.CartId, evt.ReservationId);

        try
        {
            var cart = await dbContext.Carts
                .Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.Id == evt.CartId, context.CancellationToken);

            if (cart is null)
            {
                // Le panier n'existe pas — cas normal si l'ordre n'a jamais été persisté
                // (ex: DB timeout lors de la création) ou si le panier a été purgé.
                logger.LogWarning(
                    "Cart {CartId} not found while processing reservation expiry for Product={ProductId} — skipping.",
                    evt.CartId, evt.ProductId);
                return;
            }

            cart.RemoveItem(evt.ProductId);
            await dbContext.SaveChangesAsync(context.CancellationToken);

            logger.LogInformation(
                "Removed item Product={ProductId} from Cart={CartId} due to reservation expiry.",
                evt.ProductId, evt.CartId);
        }
        catch (InvalidCartOperationException ex)
        {
            // Exception MÉTIER — l'item n'est plus dans le panier.
            // Cas normaux sous charge : même produit ajouté puis expiré deux fois,
            // ou item déjà retiré par une autre voie (checkout, annulation manuelle).
            // Pas de retry : l'état final est cohérent, retenter ne changera rien.
            logger.LogWarning(
                "Cannot remove Product={ProductId} from Cart={CartId}: {Message} — skipping.",
                evt.ProductId, evt.CartId, ex.Message);

            // Ne pas relancer → pas de retry, pas de dead letter queue
        }
        // Les exceptions techniques (DB, réseau) remontent naturellement :
        // MassTransit applique le retry configuré dans DependencyInjection.
    }
}
