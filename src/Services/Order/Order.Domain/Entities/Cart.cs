using Order.Domain.Common;
using Order.Domain.Events;
using Order.Domain.Exceptions;

namespace Order.Domain.Entities;

public class Cart : BaseEntity
{
    public Guid CustomerId { get; private set; }
    public CartStatus Status { get; private set; } = CartStatus.Active;

    private readonly List<CartItem> _items = [];
    public IReadOnlyCollection<CartItem> Items => _items.AsReadOnly();

    public decimal Total => _items.Sum(i => i.SubTotal);

    private Cart() { } // EF Core

    public static Cart Create(Guid customerId) =>
        new() { CustomerId = customerId };

    public CartItem AddItem(Guid productId, string productName, decimal unitPrice, int quantity)
    {
        if (Status != CartStatus.Active)
            throw new InvalidCartOperationException("Cannot add items to a non-active cart.");

        var existing = _items.FirstOrDefault(i => i.ProductId == productId);
        if (existing is not null)
        {
            existing.IncreaseQuantity(quantity);
            AddDomainEvent(new ProductAddedToCartDomainEvent(Id, productId, productName, quantity));
            UpdatedAt = DateTimeOffset.UtcNow;
            return existing;
        }

        var item = CartItem.Create(Id, productId, productName, unitPrice, quantity);
        _items.Add(item);
        AddDomainEvent(new ProductAddedToCartDomainEvent(Id, productId, productName, quantity));
        UpdatedAt = DateTimeOffset.UtcNow;
        return item;
    }

    public void RemoveItem(Guid productId)
    {
        var item = _items.FirstOrDefault(i => i.ProductId == productId)
            ?? throw new InvalidCartOperationException($"Product '{productId}' is not in the cart.");
        _items.Remove(item);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Checkout()
    {
        if (Status != CartStatus.Active)
            throw new InvalidCartOperationException("Cart is not active.");
        if (_items.Count == 0)
            throw new InvalidCartOperationException("Cannot checkout an empty cart.");

        Status = CartStatus.CheckedOut;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public enum CartStatus { Active, CheckedOut, Abandoned }
