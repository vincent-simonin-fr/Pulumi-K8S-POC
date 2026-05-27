using MediatR;

namespace Order.Application.Carts.Commands.AddToCart;

public record AddToCartCommand : IRequest<AddToCartResult>
{
    public Guid? CartId     { get; init; } // null = create new cart
    public Guid CustomerId  { get; init; }
    public Guid ProductId   { get; init; }
    public string ProductName { get; init; } = default!;
    public decimal UnitPrice  { get; init; }
    public int Quantity       { get; init; }
}

public record AddToCartResult(Guid CartId, Guid CartItemId);
