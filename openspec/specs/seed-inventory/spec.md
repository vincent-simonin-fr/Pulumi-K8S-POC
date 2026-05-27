# Spec : Initialisation du catalogue produits (seed)

**Service** : InventoryApi  
**Couche** : `Inventory.Infrastructure/Persistence/`

---

## Comportement attendu

Au démarrage de l'application, si la table `products` est vide, un ensemble de 10 produits
de démonstration est inséré automatiquement via un seeder.

- Le seed est **idempotent** : il ne s'exécute que si aucun produit n'existe (`Products.Any() == false`).
- Il est **configurable** : désactivable via la clé `Seed:Enabled`.
- Un log structuré est émis à chaque exécution (skip ou insertion réussie).

## Règles métier

| Règle | Détail |
|-------|--------|
| Idempotence | Aucun produit inséré si la table `products` contient déjà des enregistrements |
| Activation | Contrôlée par `Seed:Enabled` (défaut : `true`) |
| Données | 10 produits distincts, SKUs uniques, stock initial strictement positif |
| Factory | Utiliser `Product.Create(name, sku, stock)` — jamais le constructeur direct |

## Non-goals

- Pas de seed via EF Core `HasData` (les données ne doivent pas être versionnées avec les migrations)
- Pas d'endpoint admin pour déclencher le seed à la demande
- Pas de seed dans OrderApi
- Pas de ré-insertion si des produits existent déjà (pas de merge/upsert)

## Catalogue seedé

| # | Nom | SKU | Stock initial |
|---|-----|-----|---------------|
| 1 | T-Shirt Blanc | TSH-001 | 100 |
| 2 | Pantalon Jean Slim | JEA-001 | 50 |
| 3 | Sneakers Running | SNE-001 | 75 |
| 4 | Veste en Cuir | VES-001 | 30 |
| 5 | Robe d'été | ROB-001 | 60 |
| 6 | Casquette Sport | CAP-001 | 150 |
| 7 | Sac à Dos Urbain | SAC-001 | 40 |
| 8 | Montre Connectée | MON-001 | 25 |
| 9 | Lunettes de Soleil | LUN-001 | 80 |
| 10 | Écharpe Laine | ECA-001 | 90 |

## Configuration

```json
// appsettings.json — InventoryApi
{
  "Seed": {
    "Enabled": true
  }
}
```

En production, positionner `Seed__Enabled=false` via variable d'environnement pour désactiver
l'initialisation automatique.

## Flux au démarrage

```
Program.cs (startup)
  └─ Seed:Enabled == true ?
       └─ InventorySeeder.SeedAsync(services)
            ├─ Products.Any() == true  → log "skipped", return
            └─ Products.Any() == false → Product.Create() × 10
                                          └─ db.SaveChangesAsync()
                                               └─ log "10 produits insérés"
```

## Fichiers concernés

| Fichier | Rôle |
|---------|------|
| `Inventory.Infrastructure/Persistence/InventorySeeder.cs` | Logique de seed (classe statique) |
| `Inventory.Api/Program.cs` | Appel du seeder après migration |
| `Inventory.Api/appsettings.json` | Clé `Seed:Enabled` (défaut `true`) |
