using Ecommerce.Contracts.Events;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Order.Application.Common.Interfaces;
using Order.Domain.Entities;

namespace Order.Application.Carts.Commands.AddToCart;

public sealed class AddToCartCommandHandler(
    IApplicationDbContext dbContext,
    IPublishEndpoint publishEndpoint) : IRequestHandler<AddToCartCommand, AddToCartResult>
{
    public async Task<AddToCartResult> Handle(AddToCartCommand request, CancellationToken cancellationToken)
    {
        Cart cart;

        if (request.CartId.HasValue)
        {
            cart = await dbContext.Carts
                .Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.Id == request.CartId.Value, cancellationToken)
                ?? throw new KeyNotFoundException($"Cart '{request.CartId}' not found.");
        }
        else
        {
            cart = Cart.Create(request.CustomerId);
            dbContext.Carts.Add(cart);
        }

        var item = cart.AddItem(
            request.ProductId,
            request.ProductName,
            request.UnitPrice,
            request.Quantity);

        await dbContext.SaveChangesAsync(cancellationToken);

        // Publier l'événement vers InventoryApi via RabbitMQ
        await publishEndpoint.Publish(new ProductAddedToCartEvent
        {
            CartId = cart.Id,
            ProductId = request.ProductId,
            ProductName = request.ProductName,
            Quantity = request.Quantity
        }, cancellationToken);

        return new AddToCartResult(cart.Id, item.Id);
    }
}
