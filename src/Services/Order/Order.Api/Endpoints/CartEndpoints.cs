using MediatR;
using Order.Application.Carts.Commands.AddToCart;
using Order.Application.Carts.Queries.GetCart;

namespace Order.Api.Endpoints;

public static class CartEndpoints
{
    public static void MapCartEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/carts")
            .WithTags("Carts");

        // POST /api/carts  → Ajoute un produit au panier (crée ou met à jour)
        group.MapPost("/", AddToCart)
            .WithName("AddToCart")
            .WithSummary("Ajouter un produit au panier")
            .WithDescription("""
                Ajoute un produit au panier d'un client.
                Si aucun CartId n'est fourni, un nouveau panier est créé.
                Un événement est envoyé à InventoryApi pour réserver le stock pendant la durée configurée.
                """)
            .Produces<AddToCartResult>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // GET /api/carts/{cartId}
        group.MapGet("/{cartId:guid}", GetCart)
            .WithName("GetCart")
            .WithSummary("Récupérer un panier par son identifiant")
            .Produces<CartDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> AddToCart(
        AddToCartCommand command,
        ISender sender,
        CancellationToken cancellationToken)
    {
        var result = await sender.Send(command, cancellationToken);
        return Results.Created($"/api/carts/{result.CartId}", result);
    }

    private static async Task<IResult> GetCart(
        Guid cartId,
        ISender sender,
        CancellationToken cancellationToken)
    {
        var cart = await sender.Send(new GetCartQuery(cartId), cancellationToken);
        return cart is null
            ? Results.Problem(title: "Cart not found", statusCode: 404)
            : Results.Ok(cart);
    }
}
