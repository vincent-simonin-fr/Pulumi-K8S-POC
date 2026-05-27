# Spec : Réserver un produit

**Service** : InventoryApi  
**Couche applicative** : `Inventory.Application/Reservations/Commands/ReserveProduct/`

---

## Comportement attendu

Lorsque `ProductAddedToCartEvent` est reçu depuis OrderApi, InventoryApi réserve la quantité demandée du produit pour la durée configurée.

- Si le produit n'existe pas → `ProductNotFoundException` (404 via ProblemDetails)
- Si le stock disponible est insuffisant → `InsufficientStockException` (400 via ProblemDetails)
- Une `Reservation` est créée avec le statut `Active` et une date d'expiration.
- Le `ReservedQuantity` du produit est incrémenté.

## Règles métier

| Règle | Détail |
|-------|--------|
| Stock disponible | `AvailableQuantity = StockQuantity - ReservedQuantity` |
| Réservation impossible si | `AvailableQuantity < quantity` |
| Durée de réservation | Configurable — `Reservation:TtlMinutes` (défaut : 10 minutes) |
| Statuts de réservation | `Active`, `Expired`, `Confirmed`, `Cancelled` |

## Configuration

```json
// appsettings.json — InventoryApi
{
  "Reservation": {
    "TtlMinutes": 10
  }
}
```

En environnement `Development`, `TtlMinutes` est réduit à 2 pour faciliter les tests.

## Flux déclenché par événement

```
OrderApi
  └─ publie ProductAddedToCartEvent (RabbitMQ)
       └─ InventoryApi / ProductAddedToCartConsumer
            └─ ISender.Send(ReserveProductCommand)
                 └─ ReserveProductCommandHandler
                      ├─ product.Reserve(qty)
                      └─ Reservation.Create(productId, cartId, qty, ttl)
```

## Fichiers concernés

| Fichier | Rôle |
|---------|------|
| `Inventory.Domain/Entities/Product.cs` | `Reserve()`, `ReleaseReservation()`, `ConfirmReservation()` |
| `Inventory.Domain/Entities/Reservation.cs` | Entité réservation |
| `Inventory.Domain/Enums/ReservationStatus.cs` | Enum des statuts |
| `Inventory.Application/Reservations/Commands/ReserveProduct/ReserveProductCommandHandler.cs` | Handler |
| `Inventory.Application/EventHandlers/ProductAddedToCartConsumer.cs` | Consumer MassTransit |
| `Ecommerce.Contracts/Events/ProductAddedToCartEvent.cs` | Contrat entrant |
