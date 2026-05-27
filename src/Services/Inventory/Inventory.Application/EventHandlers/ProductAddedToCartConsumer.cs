using Ecommerce.Contracts.Events;
using Inventory.Application.Reservations.Commands.ReserveProduct;
using MassTransit;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.EventHandlers;

/// <summary>
/// Consomme l'événement ProductAddedToCartEvent publié par OrderApi.
/// Déclenche la réservation du stock via MediatR.
/// </summary>
public sealed class ProductAddedToCartConsumer(
    ISender sender,
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
    }
}
