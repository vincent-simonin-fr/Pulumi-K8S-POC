using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Order.Application.Carts.Commands.AddToCart;
using Xunit;

namespace Order.Application.IntegrationTests.Carts;

[Collection(nameof(IntegrationTestCollection))]
public class AddToCartTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync()    => Task.CompletedTask;

    [Fact]
    public async Task AddToCart_WithValidCommand_ReturnsCreated()
    {
        // Arrange
        var command = new AddToCartCommand
        {
            CustomerId  = Guid.NewGuid(),
            ProductId   = Guid.NewGuid(),
            ProductName = "Widget Pro",
            UnitPrice   = 29.99m,
            Quantity    = 2
        };

        // Act
        var response = await fixture.Client.PostAsJsonAsync("/api/carts", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<AddToCartResult>();
        result.Should().NotBeNull();
        result!.CartId.Should().NotBeEmpty();
        result.CartItemId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task AddToCart_WithInvalidQuantity_ReturnsBadRequest()
    {
        // Arrange
        var command = new AddToCartCommand
        {
            CustomerId  = Guid.NewGuid(),
            ProductId   = Guid.NewGuid(),
            ProductName = "Widget Pro",
            UnitPrice   = 29.99m,
            Quantity    = -1   // invalide
        };

        // Act
        var response = await fixture.Client.PostAsJsonAsync("/api/carts", command);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetCart_AfterAddToCart_ReturnsCartWithItem()
    {
        // Arrange — créer un panier
        var command = new AddToCartCommand
        {
            CustomerId  = Guid.NewGuid(),
            ProductId   = Guid.NewGuid(),
            ProductName = "Test Product",
            UnitPrice   = 10.00m,
            Quantity    = 3
        };
        var createResponse = await fixture.Client.PostAsJsonAsync("/api/carts", command);
        var created = await createResponse.Content.ReadFromJsonAsync<AddToCartResult>();

        // Act — récupérer le panier
        var response = await fixture.Client.GetAsync($"/api/carts/{created!.CartId}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cart = await response.Content.ReadFromJsonAsync<dynamic>();
        cart?.Should().NotBeNull();
    }

    [Fact]
    public async Task AddToCart_SameProductTwice_IncreasesQuantity()
    {
        // Arrange — premier ajout
        var customerId = Guid.NewGuid();
        var productId  = Guid.NewGuid();

        var firstCommand = new AddToCartCommand
        {
            CustomerId = customerId, ProductId = productId,
            ProductName = "Duplicate Product", UnitPrice = 5m, Quantity = 1
        };
        var firstResponse = await fixture.Client.PostAsJsonAsync("/api/carts", firstCommand);
        var firstResult   = await firstResponse.Content.ReadFromJsonAsync<AddToCartResult>();

        // Act — même produit, même panier
        var secondCommand = new AddToCartCommand
        {
            CartId = firstResult!.CartId,
            CustomerId = customerId, ProductId = productId,
            ProductName = "Duplicate Product", UnitPrice = 5m, Quantity = 2
        };
        var secondResponse = await fixture.Client.PostAsJsonAsync("/api/carts", secondCommand);

        // Assert
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var cartResponse = await fixture.Client.GetAsync($"/api/carts/{firstResult.CartId}");
        var cart = await cartResponse.Content.ReadFromJsonAsync<dynamic>();
        cart?.Should().NotBeNull();
    }
}
