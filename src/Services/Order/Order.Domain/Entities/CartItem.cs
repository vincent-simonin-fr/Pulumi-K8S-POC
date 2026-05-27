using Order.Domain.Common;

namespace Order.Domain.Entities;

public class CartItem : BaseEntity
{
    public Guid CartId      { get; private set; }
    public Guid ProductId   { get; private set; }
    public string ProductName { get; private set; } = default!;
    public decimal UnitPrice  { get; private set; }
    public int Quantity       { get; private set; }

    private CartItem() { } // EF Core

    public static CartItem Create(Guid cartId, Guid productId, string productName, decimal unitPrice, int quantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);

        return new CartItem
        {
            CartId = cartId,
            ProductId = productId,
            ProductName = productName,
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }

    public void IncreaseQuantity(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        Quantity += amount;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public decimal SubTotal => UnitPrice * Quantity;
}
