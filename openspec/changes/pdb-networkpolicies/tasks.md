## 1. PodDisruptionBudget

- [ ] 1.1 Ajouter un PDB par service (order-api, inventory-api, gateway) — `minAvailable` configurable
- [ ] 1.2 Calibrer `minAvailable`/`maxUnavailable` vs min replicas HPA/KEDA (≥2 en prod pour le critique)
- [ ] 1.3 Tester un drain de nœud (multi-nœuds) → le service reste disponible (lien ha-testing.md)

## 2. Cartographie des flux réseau

- [ ] 2.1 Lister les flux légitimes : gateway→APIs, APIs→pooler/-rw, APIs→RabbitMQ:5672, inventory→Redis:6379, monitoring→ports métriques, DNS:53
- [ ] 2.2 Identifier les flux des operators/exporters (CNPG, VSO, postgres-exporter)

## 3. NetworkPolicies

- [ ] 3.1 Créer `NetworkPolicyResources.cs` (ou par composant) — default-deny ingress namespace ecommerce
- [ ] 3.2 Ajouter les policies ciblées (autorisations du point 2)
- [ ] 3.3 Autoriser explicitement le scrape Prometheus (monitoring → exporters/metrics)
- [ ] 3.4 Gardé par config ; no-op assumé sur kindnet (dev)

## 4. Validation

- [ ] 4.1 Staging avec CNI réel (Calico/Cilium) : vérifier flux légitimes OK + trafic non autorisé bloqué
- [ ] 4.2 Vérifier qu'aucun composant ne casse (apps, scrape, operators)
- [ ] 4.3 Ajouter des tests d'intégration (connexion autorisée/refusée ; drain respecte le PDB)

## 5. Infra & documentation

- [ ] 5.1 Mettre à jour Pulumi : `Pulumi.prod.yaml` (activer) / `Pulumi.dev.yaml` (présent, no-op)
- [ ] 5.2 Documenter dans `production.md` (PDB + NetworkPolicy + CNI requis), `ha-testing.md`, `architecture.md` (flux)
