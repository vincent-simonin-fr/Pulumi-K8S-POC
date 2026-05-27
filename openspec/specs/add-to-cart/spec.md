# Spec : Ajouter un produit au panier

**Service** : OrderApi  
**Couche applicative** : `Order.Application/Carts/Commands/AddToCart/`

---

## Comportement attendu

Un client peut ajouter un produit à son panier via l'endpoint `POST /api/carts`.

- Si aucun `CartId` n'est fourni, un nouveau panier est créé pour le `CustomerId`.
- Si un `CartId` est fourni, le produit est ajouté au panier existant.
- Si le produit est déjà dans le panier, sa quantité est incrémentée (pas de doublon).
- L'ajout est rejeté si le panier n'est pas en statut `Active`.

## Événement publié

Après un ajout réussi, `ProductAddedToCartEvent` est publié sur RabbitMQ.

```
ProductAddedToCartEvent {
  CorrelationId : Guid   // généré automatiquement
  CartId        : Guid
  ProductId     : Guid
  ProductName   : string
  Quantity      : int
  OccurredAt    : DateTimeOffset
}
```

Cet événement est consommé par **InventoryApi** (`ProductAddedToCartConsumer`).

## Règles de validation

| Champ | Règle |
|-------|-------|
| `CustomerId` | Requis, non vide |
| `ProductId` | Requis, non vide |
| `ProductName` | Requis, max 200 caractères |
| `UnitPrice` | Strictement positif |
| `Quantity` | Entre 1 et 100 inclus |

## Règles métier (domaine)

- `Cart.AddItem()` lève `InvalidCartOperationException` si le panier n'est pas `Active`.
- Si le produit est déjà présent (`CartItem.ProductId` identique), `CartItem.IncreaseQuantity()` est appelé.
- Un `ProductAddedToCartDomainEvent` est levé dans l'entité `Cart` à chaque ajout.

## Réponse

- **201 Created** — corps : `{ cartId: Guid, cartItemId: Guid }`
- **400 Bad Request** — si la validation FluentValidation échoue (ProblemDetails RFC 7807)
- **404 Not Found** — si un `CartId` est fourni mais introuvable

## Endpoint

```
POST /api/carts
Content-Type: application/json

{
  "cartId":      "guid|null",
  "customerId":  "guid",
  "productId":   "guid",
  "productName": "string",
  "unitPrice":   number,
  "quantity":    int
}
```

## Fichiers concernés

| Fichier | Rôle |
|---------|------|
| `Order.Domain/Entities/Cart.cs` | Logique métier, domain events |
| `Order.Domain/Entities/CartItem.cs` | Item de panier |
| `Order.Application/Carts/Commands/AddToCart/AddToCartCommand.cs` | Commande MediatR |
| `Order.Application/Carts/Commands/AddToCart/AddToCartCommandHandler.cs` | Handler, publication MassTransit |
| `Order.Application/Carts/Commands/AddToCart/AddToCartCommandValidator.cs` | Validation FluentValidation |
| `Order.Api/Endpoints/CartEndpoints.cs` | Endpoint Minimal API |
| `Ecommerce.Contracts/Events/ProductAddedToCartEvent.cs` | Contrat partagé |
