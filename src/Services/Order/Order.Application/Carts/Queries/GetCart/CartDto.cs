namespace Order.Application.Carts.Queries.GetCart;

public record CartDto(
    Guid Id,
    Guid CustomerId,
    string Status,
    decimal Total,
    IReadOnlyList<CartItemDto> Items);

public record CartItemDto(
    Guid Id,
    Guid ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal SubTotal);
