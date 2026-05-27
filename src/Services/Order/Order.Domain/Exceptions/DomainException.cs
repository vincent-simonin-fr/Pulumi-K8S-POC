namespace Order.Domain.Exceptions;

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}

public class CartNotFoundException : DomainException
{
    public CartNotFoundException(Guid cartId)
        : base($"Cart with id '{cartId}' was not found.") { }
}

public class InvalidCartOperationException : DomainException
{
    public InvalidCartOperationException(string message) : base(message) { }
}
