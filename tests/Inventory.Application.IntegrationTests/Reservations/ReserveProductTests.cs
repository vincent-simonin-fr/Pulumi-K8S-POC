using System.Net;
using FluentAssertions;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Inventory.Application.IntegrationTests.Reservations;

[Collection(nameof(IntegrationTestCollection))]
public class ReserveProductTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    private async Task<Product> SeedProductAsync(int stock = 10)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var product = Product.Create("Test Product", $"SKU-{Guid.NewGuid():N}", stock);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public async Task GetProduct_AfterSeeding_ReturnsProduct()
    {
        // Arrange
        var product = await SeedProductAsync(stock: 5);

        // Act
        var response = await fixture.Client.GetAsync($"/api/products/{product.Id}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProducts_ReturnsAllProducts()
    {
        // Arrange
        await SeedProductAsync(10);
        await SeedProductAsync(20);

        // Act
        var response = await fixture.Client.GetAsync("/api/products");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetProduct_NotExisting_Returns404()
    {
        // Act
        var response = await fixture.Client.GetAsync($"/api/products/{Guid.NewGuid()}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetProductReservations_AfterReserving_ShowsReservation()
    {
        // Arrange
        var product = await SeedProductAsync(stock: 5);

        // Simuler une réservation directement via EF
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var p  = await db.Products.FindAsync(product.Id);
        p!.Reserve(2);
        var reservation = Reservation.Create(product.Id, Guid.NewGuid(), 2, TimeSpan.FromMinutes(10));
        db.Reservations.Add(reservation);
        await db.SaveChangesAsync();

        // Act
        var response = await fixture.Client.GetAsync($"/api/products/{product.Id}/reservations");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
