# Spec : Expiration des réservations

**Service** : InventoryApi  
**Couche infrastructure** : `Inventory.Infrastructure/BackgroundServices/ReservationExpiryService.cs`

---

## Comportement attendu

Un service de fond (`IHostedService`) surveille en continu les réservations actives et expire celles dont la date `ExpiresAt` est dépassée.

Pour chaque réservation expirée :
1. `product.ReleaseReservation(quantity)` est appelé (le stock reservé est libéré)
2. `reservation.Expire()` passe le statut en `Expired`
3. `ProductReservationExpiredEvent` est publié sur RabbitMQ
4. Les modifications sont persistées en base

## Configuration

```json
// appsettings.json — InventoryApi
{
  "Reservation": {
    "TtlMinutes": 10,
    "CheckIntervalSeconds": 30
  }
}
```

| Paramètre | Description | Défaut |
|-----------|-------------|--------|
| `Reservation:TtlMinutes` | Durée de vie d'une réservation | 10 min |
| `Reservation:CheckIntervalSeconds` | Intervalle entre deux passes du service | 30 sec |

En `Development` : TtlMinutes=2, CheckIntervalSeconds=10.

## Événement publié

```
ProductReservationExpiredEvent {
  CorrelationId  : Guid
  ReservationId  : Guid
  CartId         : Guid
  ProductId      : Guid
  Quantity       : int
  ExpiredAt      : DateTimeOffset
}
```

Consommé par **OrderApi** (`ProductReservationExpiredConsumer`) qui retire le produit du panier.

## Flux complet d'expiration

```
ReservationExpiryService (loop, toutes les CheckIntervalSeconds)
  ├─ SELECT reservations WHERE status='Active' AND expires_at <= NOW()
  ├─ Pour chaque réservation :
  │    ├─ product.ReleaseReservation(qty)
  │    ├─ reservation.Expire()
  │    └─ publish ProductReservationExpiredEvent
  └─ SaveChangesAsync()

OrderApi / ProductReservationExpiredConsumer
  └─ cart.RemoveItem(productId)
  └─ SaveChangesAsync()
```

## Index de base de données

```sql
-- Optimise la requête du background service
CREATE INDEX ix_reservations_status_expires_at
  ON reservations (status, expires_at);
```

Défini dans `ReservationConfiguration.cs` :
```csharp
builder.HasIndex(r => new { r.Status, r.ExpiresAt });
```

## Fichiers concernés

| Fichier | Rôle |
|---------|------|
| `Inventory.Infrastructure/BackgroundServices/ReservationExpiryService.cs` | Service de fond |
| `Inventory.Domain/Entities/Reservation.cs` | `Expire()`, `IsExpired` |
| `Inventory.Domain/Entities/Product.cs` | `ReleaseReservation()` |
| `Order.Application/EventHandlers/ProductReservationExpiredConsumer.cs` | Consumer OrderApi |
| `Ecommerce.Contracts/Events/ProductReservationExpiredEvent.cs` | Contrat partagé |
