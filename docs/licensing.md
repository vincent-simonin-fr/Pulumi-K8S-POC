# Licences & Coûts (équipe < 10 personnes)

> **Base tarifaire** : pricing public 2025–2026. Les tarifs sont indicatifs — vérifier les pages officielles avant tout engagement contractuel.  
> **Contexte** : usage interne entreprise, auto-hébergement, équipe de développement < 10 personnes.

---

## Sommaire

- [Synthèse financière](#synthèse-financière)
- [Outils open source sans coût de licence](#outils-open-source-sans-coût-de-licence)
- [Outils à surveiller](#outils-à-surveiller)
- [Infrastructure cloud (si prod hébergée)](#infrastructure-cloud-si-prod-hébergée)
- [Recommandations](#recommandations)

---

## Synthèse financière

| Catégorie | Coût licence logiciel | Remarque |
|---|---|---|
| Runtime .NET + frameworks | **0 €** | MIT / Apache 2.0 |
| Messaging (RabbitMQ, MassTransit) | **0 €** | MPL 2.0 / Apache 2.0 |
| Base de données (PostgreSQL, CNPG, PgBouncer) | **0 €** | PostgreSQL License / Apache 2.0 |
| Cache (Redis CE ou Valkey) | **0 €** | SSPL ou BSD — voir section dédiée |
| Observabilité (OTel, Prometheus, Grafana OSS, Jaeger) | **0 €** | Apache 2.0 / AGPLv3 auto-hébergé |
| Kubernetes + outillage (Kind, KEDA, ESO, cert-manager) | **0 €** | Apache 2.0 |
| Load testing (k6 OSS) | **0 €** | AGPLv3 |
| Conteneurisation (Podman) | **0 €** | Apache 2.0 |
| IaC (Pulumi CLI) | **0 €** | Apache 2.0 |
| **Pulumi Cloud** *(optionnel)* | **0–600 €/mois** | Gratuit si état local/S3 |
| **Total licences logicielles** | **0 €/mois** | Sans Pulumi Cloud |

Les seuls coûts récurrents sont **l'infrastructure** (cloud provider en prod) et optionnellement **Pulumi Cloud**.

---

## Outils open source sans coût de licence

### Langage & Runtime

| Outil | Version | Licence | Usage |
|---|---|---|---|
| .NET 9 | 9.x | MIT | Runtime ASP.NET Core |
| ASP.NET Core | 9.x | MIT | APIs REST (order-api, inventory-api, gateway) |
| Entity Framework Core | 9.x | MIT | ORM + migrations PostgreSQL |
| Npgsql | 9.x | PostgreSQL License | Driver ADO.NET |
| MassTransit | 8.x | Apache 2.0 | Bus de messages (RabbitMQ) |
| OpenTelemetry SDK .NET | 1.x | Apache 2.0 | Traces, métriques, logs |

Toutes ces licences permettent un usage commercial sans restriction ni redevance.

---

### Bases de données

| Outil | Licence | Remarque |
|---|---|---|
| PostgreSQL 16 | PostgreSQL License (BSD-like) | Aucune restriction d'usage commercial |
| CloudNativePG (CNPG) | Apache 2.0 | Opérateur K8s, maintenu par EDB — gratuit |
| PgBouncer | ISC | Connection pooler — gratuit |

> **Note CNPG** : EDB (EnterpriseDB) propose un support commercial optionnel pour CNPG, non obligatoire. La version communautaire est complète.

---

### Messaging & Cache

| Outil | Licence | Remarque |
|---|---|---|
| RabbitMQ | MPL 2.0 | Usage commercial autorisé sans redevance |
| **Redis CE** | **SSPL + RSALv2** | **⚠️ Voir section dédiée** |
| **Valkey** | BSD-3-Clause | Alternative drop-in recommandée |

---

### Observabilité

| Outil | Licence | Usage dans le projet |
|---|---|---|
| OpenTelemetry Collector | Apache 2.0 | Agrégation traces + métriques |
| Prometheus | Apache 2.0 | Scrape + stockage métriques |
| Grafana OSS | **AGPLv3** | Dashboards (auto-hébergé) — **voir note** |
| Jaeger | Apache 2.0 | Distributed tracing |
| postgres_exporter | Apache 2.0 | Métriques PostgreSQL |
| kube-state-metrics | Apache 2.0 | Métriques K8s |
| node-exporter | Apache 2.0 | Métriques système |

> **Note Grafana AGPLv3** : l'AGPLv3 impose de partager les modifications du code source si vous exposez Grafana comme service réseau **à des tiers**. Pour un usage interne (équipe dev uniquement), sans modification du code Grafana, il n'y a **aucune obligation** et **aucun coût**. La licence n'affecte pas les dashboards JSON (qui sont vos propres fichiers).

---

### Infrastructure Kubernetes

| Outil | Licence | Remarque |
|---|---|---|
| Kubernetes | Apache 2.0 | Gratuit — les distributions cloud (EKS, AKS, GKE) facturent l'infra, pas la licence K8s |
| Kind | Apache 2.0 | Cluster local dev |
| KEDA | Apache 2.0 | Event-driven autoscaling |
| External Secrets Operator | Apache 2.0 | Gestion secrets K8s |
| cert-manager | Apache 2.0 | Certificats TLS Let's Encrypt |
| nginx Ingress Controller | Apache 2.0 | Ingress production |

---

### Outillage développeur

| Outil | Licence | Remarque |
|---|---|---|
| Podman | Apache 2.0 | Build et run conteneurs — 100% gratuit, y compris en entreprise |
| k6 (OSS) | AGPLv3 | Usage interne en entreprise : aucun coût, aucune obligation de partage |
| Pulumi CLI | Apache 2.0 | Le CLI est open source et gratuit |

---

## Outils à surveiller

### Redis CE — Changement de licence (mars 2024)

**Situation** : depuis la version 7.4, Redis n'est plus sous licence BSD. La licence est désormais **duale** :
- **SSPL** (Server Side Public License) — non approuvée OSI
- **RSALv2** (Redis Source Available License v2)

**Impact pour une entreprise** :

| Scénario | Obligation | Coût |
|---|---|---|
| Usage interne (cache applicatif) | Aucune obligation de partage | **0 €** |
| Fourniture de Redis *as a service* à des clients | SSPL impose de publier le code de votre service | Non applicable ici |
| Politique interne "OSI-only" | La licence SSPL peut bloquer l'adoption | Voir alternative |

**Recommandation** : remplacer Redis par **Valkey** (fork BSD-3-Clause maintenu par la Linux Foundation, Redis Labs, AWS, Google). Valkey est un drop-in replacement — aucun changement de code applicatif requis.

```yaml
# docker-compose.yml — remplacement trivial
# Avant :
image: redis:7-alpine
# Après :
image: valkey/valkey:8-alpine
```

---

### Pulumi Cloud *(optionnel)*

Le CLI Pulumi est gratuit. Le **state** (état de l'infrastructure) peut être stocké :

| Option | Coût | Remarque |
|---|---|---|
| **Local** (`pulumi login --local`) | **0 €** | OK pour dev solo, pas de partage d'état |
| **S3 / Azure Blob / GCS** | Coût stockage uniquement (~0 €) | Recommandé en équipe |
| **Pulumi Cloud** | Voir tableau ci-dessous | SaaS hébergé par Pulumi |

**Tarifs Pulumi Cloud 2025–2026** (source : pulumi.com/pricing) :

| Plan | Prix | Inclus |
|---|---|---|
| Individual | **Gratuit** | 1 utilisateur, stacks illimitées |
| Team | **~120 $/mois** | 3 utilisateurs inclus + 60 $/utilisateur/mois supplémentaire |
| Enterprise | Sur devis | SSO, audit logs, RBAC avancé |

**Pour une équipe de 10 personnes** :
- Team : 120 $ + 7 × 60 $ = **~540 $/mois (~500 €/mois)**
- Alternative gratuite : state dans un bucket S3 partagé (< 1 €/mois)

> **Recommandation** : utiliser un bucket S3/Azure Blob comme backend d'état. Coût négligeable, pas de dépendance à un SaaS tiers, collaboration d'équipe native.
>
> ```bash
> pulumi login s3://votre-bucket-infra-state
> # ou Azure :
> pulumi login azblob://votre-container-infra-state
> ```

---

### Grafana Enterprise *(non utilisé, pour information)*

La version OSS auto-hébergée utilisée dans ce projet est **gratuite**. Grafana Enterprise ajoute :
- SSO SAML/LDAP avancé
- Audit logs
- Reporting automatique
- Data source caching

**Prix 2025–2026** : ~500 $/mois pour 10 utilisateurs (source : grafana.com/pricing).  
**Verdict** : inutile pour < 10 personnes en usage interne. La version OSS couvre tous les cas d'usage du projet.

---

## Infrastructure cloud (si prod hébergée)

Les licences logicielles sont gratuites, mais l'infrastructure cloud est facturée. Estimations pour une prod minimale (région Europe) :

### Azure Kubernetes Service (AKS)

| Ressource | Spec | Coût estimé/mois |
|---|---|---|
| Nœuds K8s (×2) | Standard_D2s_v3 (2 vCPU, 8 Go RAM) | ~140 € |
| Disques managés (×4) | Premium SSD P10 (128 Go) | ~80 € |
| Load Balancer | Standard | ~20 € |
| Registre conteneurs (ACR) | Basic | ~5 € |
| Transfert réseau sortant | ~10 Go/mois | ~1 € |
| **Total AKS minimal** | | **~246 €/mois** |

### Amazon EKS (équivalent)

| Ressource | Spec | Coût estimé/mois |
|---|---|---|
| Cluster EKS (control plane) | — | ~70 € |
| Nœuds (×2) | t3.medium (2 vCPU, 4 Go RAM) | ~60 € |
| EBS (×4, gp3) | 100 Go | ~40 € |
| ALB | — | ~25 € |
| ECR | Basic | ~1 € |
| **Total EKS minimal** | | **~196 €/mois** |

### Domaine & TLS

| Ressource | Coût |
|---|---|
| Domaine `wizzz.com` (renouvellement annuel) | ~15 €/an |
| Certificats TLS | **0 €** — Let's Encrypt via cert-manager |

---

## Recommandations

### Actions immédiates

1. **Remplacer Redis par Valkey** — évite toute ambiguïté sur la licence SSPL, drop-in replacement sans changement de code.

2. **Utiliser un bucket cloud comme backend Pulumi** — évite 500 €/mois de Pulumi Cloud tout en permettant la collaboration d'équipe.

3. **Documenter la conformité AGPLv3** pour Grafana et k6 auprès du service juridique si une politique "OSI-only" existe (AGPLv3 est approuvée OSI, contrairement à SSPL).

### Tableau récapitulatif des risques

| Outil | Licence | Risque | Action |
|---|---|---|---|
| Redis CE | SSPL | ⚠️ Moyen | Migrer vers Valkey |
| Grafana OSS | AGPLv3 | 🟢 Faible | Usage interne sans modification = OK |
| k6 OSS | AGPLv3 | 🟢 Faible | Usage interne = OK |
| Pulumi CLI | Apache 2.0 | 🟢 Aucun | — |
| Pulumi Cloud | Propriétaire | 💰 Coût | Utiliser S3/blob comme backend |
| Tous les autres | Apache 2.0 / MIT / BSD | 🟢 Aucun | — |

### Budget total recommandé

| Poste | Mensuel |
|---|---|
| Licences logicielles | **0 €** |
| Backend Pulumi (S3/blob) | **< 1 €** |
| Infrastructure prod (AKS/EKS minimal) | **200–250 €** |
| Domaine | **~1 €** (lissé) |
| **Total** | **~200–250 €/mois** |

L'intégralité du coût est de l'**infrastructure**, pas de la licence logicielle.
