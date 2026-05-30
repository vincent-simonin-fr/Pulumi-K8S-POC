namespace Ecommerce.Contracts.Events;

/// <summary>
/// Événement publié par InventoryApi lorsqu'une réservation de stock échoue.
///
/// Causes possibles (voir <see cref="FailureReason"/>) :
///   InsufficientStock — le stock disponible est inférieur à la quantité demandée.
///   ProductNotFound   — le produit n'existe pas dans l'inventaire.
///
/// Consommateurs potentiels :
///   OrderApi  — annuler ou notifier l'item du panier.
///   NotifApi  — informer l'utilisateur ("article indisponible").
///
/// Corrélation : <see cref="CorrelationId"/> = même valeur que dans
/// <see cref="ProductAddedToCartEvent"/> d'origine → traçabilité end-to-end.
/// </summary>
public record ProductReservationFailedEvent
{
    public Guid   CorrelationId      { get; init; } = Guid.NewGuid();
    public Guid   CartId             { get; init; }
    public Guid   ProductId          { get; init; }
    public string ProductName        { get; init; } = default!;
    public int    RequestedQuantity  { get; init; }
    public int    AvailableQuantity  { get; init; }

    /// <summary>Raison structurée — préférer à la lecture du message libre.</summary>
    public ReservationFailureReason FailureReason { get; init; }

    /// <summary>Message lisible pour les logs et le debug.</summary>
    public string Reason             { get; init; } = default!;

    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum ReservationFailureReason
{
    InsufficientStock,
    ProductNotFound
}
