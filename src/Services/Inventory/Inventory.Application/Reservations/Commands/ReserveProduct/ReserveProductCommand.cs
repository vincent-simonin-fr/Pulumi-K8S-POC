using MediatR;

namespace Inventory.Application.Reservations.Commands.ReserveProduct;

public record ReserveProductCommand : IRequest<ReserveProductResult>
{
    public Guid ProductId { get; init; }
    public Guid CartId    { get; init; }
    public int Quantity   { get; init; }
}

public record ReserveProductResult(Guid ReservationId, DateTimeOffset ExpiresAt);
