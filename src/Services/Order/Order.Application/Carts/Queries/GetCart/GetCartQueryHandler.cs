using MediatR;
using Microsoft.EntityFrameworkCore;
using Order.Application.Common.Interfaces;

namespace Order.Application.Carts.Queries.GetCart;

public sealed class GetCartQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<GetCartQuery, CartDto?>
{
    public async Task<CartDto?> Handle(GetCartQuery request, CancellationToken cancellationToken)
    {
        var cart = await dbContext.Carts
            .AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == request.CartId, cancellationToken);

        if (cart is null) return null;

        return new CartDto(
            cart.Id,
            cart.CustomerId,
            cart.Status.ToString(),
            cart.Total,
            cart.Items.Select(i => new CartItemDto(
                i.Id,
                i.ProductId,
                i.ProductName,
                i.UnitPrice,
                i.Quantity,
                i.SubTotal)).ToList());
    }
}
