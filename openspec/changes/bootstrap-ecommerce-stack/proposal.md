# Proposal : Bootstrap de la stack Ecommerce

## Résumé

Mise en place de la stack technique initiale : deux microservices .NET 10 en Clean Architecture (OrderApi et InventoryApi), communicant via RabbitMQ / MassTransit, déployables localement avec Podman Desktop + Kind via Pulumi.

## Contexte

Création from scratch d'un projet de démonstration e-commerce avec les contraintes suivantes :
- .NET 10 Minimal API
- Clean Architecture (Jason Taylor)
- Event bus RabbitMQ + MassTransit
- Déploiement Kubernetes local (Kind + Podman)
- Infrastructure-as-code Pulumi (C#)
- Observabilité dès le départ (Serilog, OpenTelemetry, Health checks)

## Périmètre

**Inclus :**
- Solution monorepo avec 13 projets
- OrderApi complet (panier, AddToCart, GetCart)
- InventoryApi complet (produits, réservations, expiry background service)
- Gateway YARP
- Contrats partagés MassTransit
- Dockerfiles multi-stage (non-root)
- docker-compose pour développement local
- Infrastructure Pulumi (Namespace, Deployments, Services, Jobs migrations)
- Tests d'intégration (Testcontainers + Respawn)

**Non-inclus (à venir) :**
- Authentification / autorisation
- Checkout / paiement
- Notification en temps réel (SignalR / WebSockets)
- Multi-tenancy
- Dashboard d'administration
- Pipeline CI/CD

## Décision technique principale

Les deux APIs partagent un projet `Ecommerce.Contracts` contenant les records d'événements MassTransit. Cela évite la duplication tout en maintenant des services indépendants.
