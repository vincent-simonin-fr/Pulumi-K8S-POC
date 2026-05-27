namespace Inventory.Domain.Exceptions;

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

public class ProductNotFoundException : DomainException
{
    public ProductNotFoundException(Guid productId)
        : base($"Product with id '{productId}' was not found.") { }
}

public class InsufficientStockException : DomainException
{
    public InsufficientStockException(Guid productId, int requested, int available)
        : base($"Insufficient stock for product '{productId}': requested {requested}, available {available}.") { }
}
