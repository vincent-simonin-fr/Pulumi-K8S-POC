using Order.Domain.Common;

namespace Order.Domain.Events;

public record ProductAddedToCartDomainEvent(
    Guid CartId,
    Guid ProductId,
    string ProductName,
    int Quantity) : BaseEvent;
