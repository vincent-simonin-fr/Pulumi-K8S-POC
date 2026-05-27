using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Persistence;

public static class InventorySeeder
{
    private static readonly (string Name, string Sku, int Stock)[] SeedData =
    [
        ("T-Shirt Blanc",       "TSH-001", 100),
        ("Pantalon Jean Slim",  "JEA-001",  50),
        ("Sneakers Running",    "SNE-001",  75),
        ("Veste en Cuir",       "VES-001",  30),
        ("Robe d'été",          "ROB-001",  60),
        ("Casquette Sport",     "CAP-001", 150),
        ("Sac à Dos Urbain",    "SAC-001",  40),
        ("Montre Connectée",    "MON-001",  25),
        ("Lunettes de Soleil",  "LUN-001",  80),
        ("Écharpe Laine",       "ECA-001",  90),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();

        var db     = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

        if (await db.Products.AnyAsync())
        {
            logger.LogInformation("Inventory seed skipped — products already exist.");
            return;
        }

        var products = SeedData
            .Select(p => Product.Create(p.Name, p.Sku, p.Stock))
            .ToList();

        db.Products.AddRange(products);
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Inventory seed completed — {Count} products inserted.",
            products.Count);
    }
}
