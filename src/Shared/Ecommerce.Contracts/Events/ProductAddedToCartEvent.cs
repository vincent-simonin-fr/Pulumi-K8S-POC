namespace Ecommerce.Contracts.Events;

/// <summary>
/// Événement publié par OrderApi lorsqu'un produit est ajouté au panier.
/// Consommé par InventoryApi pour déclencher une réservation.
/// </summary>
public record ProductAddedToCartEvent
{
    public Guid CorrelationId { get; init; } = Guid.NewGuid();
    public Guid CartId        { get; init; }
    public Guid ProductId     { get; init; }
    public string ProductName { get; init; } = default!;
    public int Quantity       { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}
