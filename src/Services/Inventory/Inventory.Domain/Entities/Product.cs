using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Entities;

public class Product
{
    public Guid Id              { get; private set; } = Guid.NewGuid();
    public string Name          { get; private set; } = default!;
    public string Sku           { get; private set; } = default!;
    public int StockQuantity    { get; private set; }
    public int ReservedQuantity { get; private set; }
    public int AvailableQuantity => StockQuantity - ReservedQuantity;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; private set; }

    private Product() { } // EF Core

    public static Product Create(string name, string sku, int initialStock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentOutOfRangeException.ThrowIfNegative(initialStock);

        return new Product { Name = name, Sku = sku, StockQuantity = initialStock };
    }

    public void Reserve(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity to reserve must be positive.");
        if (AvailableQuantity < quantity)
            throw new InsufficientStockException(Id, quantity, AvailableQuantity);

        ReservedQuantity += quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ReleaseReservation(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity to release must be positive.");
        ReservedQuantity = Math.Max(0, ReservedQuantity - quantity);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void ConfirmReservation(int quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity to confirm must be positive.");
        if (ReservedQuantity < quantity)
            throw new DomainException($"Cannot confirm {quantity} units; only {ReservedQuantity} reserved.");

        ReservedQuantity -= quantity;
        StockQuantity    -= quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void AddStock(int quantity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        StockQuantity += quantity;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
