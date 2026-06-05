## Context

Les déploiements applicatifs n'ont pas de PDB → un drain peut tout évincer malgré
l'anti-affinité. Aucune NetworkPolicy → trafic est-ouest non restreint (CNPG/RabbitMQ/
Redis joignables par tout pod). Ce change ajoute résilience (PDB) et isolation réseau
(default-deny + flux ciblés), en restant portable dev (kindnet n'applique pas les
NetworkPolicies).

## Goals / Non-Goals

**Goals:**
- PDB `minAvailable: 1` pour order-api/inventory-api/gateway.
- NetworkPolicies default-deny ingress + autorisations ciblées.
- Portabilité dev (no-op sur kindnet) ; effectif en prod (Calico/Cilium).

**Non-Goals:**
- Egress stricte exhaustive (1ʳᵉ itération : DNS + flux nécessaires).
- Service mesh / mTLS.

## Decisions

- **PDB en ressources Pulumi** par service (dans les *ServiceResources). `minAvailable: 1`
  par défaut (configurable). Avec HPA, garder `minAvailable` cohérent (ne pas bloquer les
  évictions si min replicas = 1 → `minAvailable: 1` peut empêcher tout drain ; préférer
  un % ou s'assurer de ≥2 réplicas en prod). À calibrer.
- **NetworkPolicies déclaratives K8s** (objet natif, pas de CRD) → portables ; appliquées
  par le CNI en prod. default-deny ingress + policies ciblées par composant.
- **Cartographier les flux avant d'activer** (gateway→APIs, APIs→pooler/-rw, APIs→
  RabbitMQ:5672, inventory→Redis:6379, monitoring→ports métriques, DNS:53). Une policy
  manquante casse une connexion → tester en staging avec un CNI réel.
- **Activé par config** : PDB partout (utile au test de drain) ; NetworkPolicies posées
  partout mais réellement effectives seulement sous un CNI qui les applique.

## Risks / Trade-offs

- [NetworkPolicy trop stricte casse un flux légitime] → cartographie + tests staging avec
  Calico/Cilium ; commencer par default-deny + autoriser large, resserrer ensuite.
- [PDB bloque un drain (minAvailable = total réplicas)] → garantir ≥2 réplicas en prod
  pour les services critiques, ou utiliser `maxUnavailable`.
- [Faux sentiment de sécurité en dev] → documenter clairement que kindnet **n'applique
  pas** les NetworkPolicies (no-op) ; validation = staging avec vrai CNI.
- [Scrape Prometheus cassé par default-deny] → autoriser explicitement monitoring→exporters.

## Migration Plan

1. Ajouter les PDB (tester un drain de nœud → le service reste disponible).
2. Cartographier les flux réseau réels (logs/observabilité).
3. Poser default-deny + policies ciblées ; **valider en staging** sous Calico/Cilium.
4. Ajuster jusqu'à zéro régression, puis activer en prod.
5. Rollback : retirer les NetworkPolicies (retour au trafic ouvert) / les PDB.

## Open Questions

- `minAvailable` vs `maxUnavailable`, et valeur exacte par service (lié au min replicas HPA/KEDA).
- Egress : faut-il restreindre la sortie (au-delà de DNS + flux internes) dès la 1ʳᵉ itération ?
- CNI prod cible (Calico, Cilium) — confirme l'enforcement + d'éventuelles fonctions avancées.
