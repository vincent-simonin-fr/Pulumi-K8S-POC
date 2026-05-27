# Tasks : Bootstrap de la stack Ecommerce

## Statut : ✅ Terminé

- [x] Créer la structure de la solution monorepo et le fichier `Ecommerce.sln`
- [x] Créer les 13 fichiers `.csproj` avec leurs dépendances NuGet
- [x] Implémenter `Ecommerce.Contracts` (ProductAddedToCartEvent, ProductReservationExpiredEvent)
- [x] Implémenter `Order.Domain` (Cart, CartItem, BaseEntity, domain events, exceptions)
- [x] Implémenter `Order.Application` (AddToCart command + validator + handler, GetCart query, ProductReservationExpiredConsumer, behaviours)
- [x] Implémenter `Order.Infrastructure` (ApplicationDbContext, EF configurations, DependencyInjection)
- [x] Implémenter `Order.Api` (Program.cs, CartEndpoints, appsettings, OpenAPI Scalar)
- [x] Implémenter `Inventory.Domain` (Product, Reservation, ReservationStatus, exceptions)
- [x] Implémenter `Inventory.Application` (ReserveProduct command + handler, ProductAddedToCartConsumer)
- [x] Implémenter `Inventory.Infrastructure` (ApplicationDbContext, EF configurations, ReservationExpiryService, DependencyInjection)
- [x] Implémenter `Inventory.Api` (Program.cs, ProductEndpoints, appsettings, OpenAPI Scalar)
- [x] Créer `Ecommerce.Gateway` (YARP, appsettings routes/clusters, Program.cs)
- [x] Créer les Dockerfiles multi-stage (order-api, inventory-api, gateway)
- [x] Créer `docker-compose.yml` (postgres x2, rabbitmq, tous les services)
- [x] Créer l'infrastructure Pulumi (EcommerceStack, DatabaseResources, MessagingResources, OrderServiceResources, InventoryServiceResources, GatewayResources)
- [x] Créer les tests d'intégration OrderApi (Testcontainers + Respawn, 4 tests)
- [x] Créer les tests d'intégration InventoryApi (Testcontainers + Respawn, 4 tests)
- [x] Créer README.md avec guide de démarrage complet
- [x] Configurer OpenSpec (config.yaml, specs add-to-cart / reserve-product / reservation-expiry)
