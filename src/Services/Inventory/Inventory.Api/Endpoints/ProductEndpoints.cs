using Inventory.Application.Common;
using Inventory.Application.Common.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace Inventory.Api.Endpoints;

public static class ProductEndpoints
{
    public static void MapProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/products")
            .WithTags("Products");

        // GET /api/products
        // Cache-aside pattern : Redis → PostgreSQL (fallback gracieux si Redis indisponible)
        group.MapGet("/", async (IApplicationDbContext db, IDistributedCache cache, CancellationToken ct) =>
        {
            // ── 1. Cache hit ──────────────────────────────────────────────────
            try
            {
                var cached = await cache.GetStringAsync(CacheKeys.ProductsAll, ct);
                if (cached is not null)
                    return Results.Content(cached, "application/json");
            }
            catch
            {
                // Redis indisponible → on passe directement à la base
            }

            // ── 2. Cache miss → base de données ──────────────────────────────
            var products = await db.Products
                .AsNoTracking()
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Sku,
                    p.StockQuantity,
                    p.ReservedQuantity,
                    p.AvailableQuantity
                })
                .ToListAsync(ct);

            // ── 3. Écriture en cache ──────────────────────────────────────────
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(products,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                    });

                await cache.SetStringAsync(
                    CacheKeys.ProductsAll,
                    json,
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = CacheTtl.Products
                    },
                    ct);
            }
            catch
            {
                // Redis indisponible → on retourne la réponse sans mise en cache
            }

            return Results.Ok(products);
        })
        .WithName("GetProducts")
        .WithSummary("Lister tous les produits et leur stock disponible")
        .Produces(StatusCodes.Status200OK);

        // GET /api/products/{id}
        group.MapGet("/{id:guid}", async (Guid id, IApplicationDbContext db, CancellationToken ct) =>
        {
            var product = await db.Products
                .AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Sku,
                    p.StockQuantity,
                    p.ReservedQuantity,
                    p.AvailableQuantity
                })
                .FirstOrDefaultAsync(ct);

            return product is null
                ? Results.Problem(title: "Product not found", statusCode: 404)
                : Results.Ok(product);
        })
        .WithName("GetProduct")
        .WithSummary("Récupérer un produit par son identifiant")
        .Produces(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound);

        // GET /api/products/{id}/reservations
        group.MapGet("/{id:guid}/reservations", async (Guid id, IApplicationDbContext db, CancellationToken ct) =>
        {
            var reservations = await db.Reservations
                .AsNoTracking()
                .Where(r => r.ProductId == id)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new
                {
                    r.Id,
                    r.CartId,
                    r.Quantity,
                    r.Status,
                    r.CreatedAt,
                    r.ExpiresAt
                })
                .ToListAsync(ct);

            return Results.Ok(reservations);
        })
        .WithName("GetProductReservations")
        .WithSummary("Lister les réservations d'un produit")
        .Produces(StatusCodes.Status200OK);
    }
}
