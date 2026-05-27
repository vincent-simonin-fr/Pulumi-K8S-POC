using MediatR;

namespace Order.Application.Carts.Queries.GetCart;

public record GetCartQuery(Guid CartId) : IRequest<CartDto?>;
