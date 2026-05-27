using Inventory.Application.Common.Interfaces;
using Inventory.Domain.Entities;
using Inventory.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Reservations.Commands.ReserveProduct;

public sealed class ReserveProductCommandHandler(
    IApplicationDbContext dbContext,
    IConfiguration configuration,
    ILogger<ReserveProductCommandHandler> logger) : IRequestHandler<ReserveProductCommand, ReserveProductResult>
{
    public async Task<ReserveProductResult> Handle(
        ReserveProductCommand request,
        CancellationToken cancellationToken)
    {
        var product = await dbContext.Products
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, cancellationToken)
            ?? throw new ProductNotFoundException(request.ProductId);

        // Durée de réservation configurable via appsettings
        var ttlMinutes = configuration.GetValue<int>("Reservation:TtlMinutes", 10);
        var ttl = TimeSpan.FromMinutes(ttlMinutes);

        product.Reserve(request.Quantity);

        var reservation = Reservation.Create(request.ProductId, request.CartId, request.Quantity, ttl);
        dbContext.Reservations.Add(reservation);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Reserved {Quantity} unit(s) of product {ProductId} for cart {CartId}. Expires at {ExpiresAt}",
            request.Quantity, request.ProductId, request.CartId, reservation.ExpiresAt);

        return new ReserveProductResult(reservation.Id, reservation.ExpiresAt);
    }
}
