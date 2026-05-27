using Inventory.Domain.Enums;

namespace Inventory.Domain.Entities;

public class Reservation
{
    public Guid Id          { get; private set; } = Guid.NewGuid();
    public Guid ProductId   { get; private set; }
    public Guid CartId      { get; private set; }
    public int Quantity     { get; private set; }
    public ReservationStatus Status { get; private set; } = ReservationStatus.Active;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    private Reservation() { } // EF Core

    public static Reservation Create(Guid productId, Guid cartId, int quantity, TimeSpan ttl) =>
        new()
        {
            ProductId = productId,
            CartId    = cartId,
            Quantity  = quantity,
            ExpiresAt = DateTimeOffset.UtcNow.Add(ttl)
        };

    public bool IsExpired => Status == ReservationStatus.Active && DateTimeOffset.UtcNow >= ExpiresAt;

    public void Expire()
    {
        Status    = ReservationStatus.Expired;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Confirm()
    {
        Status    = ReservationStatus.Confirmed;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Cancel()
    {
        Status    = ReservationStatus.Cancelled;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
