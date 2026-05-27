using Ecommerce.Contracts.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Order.Application.Common.Interfaces;

namespace Order.Application.EventHandlers;

/// <summary>
/// Consomme l'événement d'expiration de réservation publié par InventoryApi.
/// Retire l'item du panier ou notifie l'utilisateur.
/// </summary>
public sealed class ProductReservationExpiredConsumer(
    IApplicationDbContext dbContext,
    ILogger<ProductReservationExpiredConsumer> logger)
    : IConsumer<ProductReservationExpiredEvent>
{
    public async Task Consume(ConsumeContext<ProductReservationExpiredEvent> context)
    {
        var evt = context.Message;
        logger.LogWarning(
            "Reservation expired for Product {ProductId} in Cart {CartId}. ReservationId: {ReservationId}",
            evt.ProductId, evt.CartId, evt.ReservationId);

        var cart = await dbContext.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == evt.CartId, context.CancellationToken);

        if (cart is null)
        {
            logger.LogWarning("Cart {CartId} not found while processing reservation expiry.", evt.CartId);
            return;
        }

        cart.RemoveItem(evt.ProductId);
        await dbContext.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "Removed item {ProductId} from Cart {CartId} due to reservation expiry.",
            evt.ProductId, evt.CartId);
    }
}
