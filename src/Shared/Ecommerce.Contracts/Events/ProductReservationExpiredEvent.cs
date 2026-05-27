namespace Ecommerce.Contracts.Events;

/// <summary>
/// Événement publié par InventoryApi lorsque la réservation d'un produit expire.
/// Consommé par OrderApi pour notifier l'utilisateur ou annuler l'item du panier.
/// </summary>
public record ProductReservationExpiredEvent
{
    public Guid CorrelationId   { get; init; } = Guid.NewGuid();
    public Guid ReservationId   { get; init; }
    public Guid CartId          { get; init; }
    public Guid ProductId       { get; init; }
    public int Quantity         { get; init; }
    public DateTimeOffset ExpiredAt { get; init; } = DateTimeOffset.UtcNow;
}
