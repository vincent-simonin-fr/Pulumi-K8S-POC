# TODO — durcissement production

Chantiers de mise en production restants, **classés par priorité**. Chacun est cadré
par une proposition **OpenSpec** (`openspec/changes/<nom>/` : proposal + design + specs +
tasks) — implémentable via `/opsx:apply <nom>`.

> Contexte : la stack est fonctionnelle (CNPG HA, RabbitMQ, observabilité
> kube-prometheus-stack, KEDA, ArgoCD/GitOps, Vault + creds PostgreSQL dynamiques).
> Ces items comblent les écarts restants pour une **vraie prod**.

---

## 🔴 P0 — Critique (à faire en premier)

- [ ] **Sauvegardes CNPG (DR)** — `openspec/changes/cnpg-backups/`
  HA ≠ DR : 3 instances protègent d'une panne de nœud, **pas** d'un `DROP TABLE` ni d'une
  corruption. Backups Barman + WAL archiving (object storage) + `ScheduledBackup` + **PITR testé**.
  *C'est le manque le plus grave : aujourd'hui, une erreur = perte de données irréversible.*

---

## 🟠 P1 — Important (sécurité & exploitabilité)

- [ ] **Alerting** — `openspec/changes/observability-alerting/`
  Dashboards présents mais **zéro alerte** : rien ne réveille en cas d'incident. Activer
  Alertmanager + `PrometheusRule` (CrashLoop, CNPG primary down, RabbitMQ quorum, latence
  p95, saturation pool PG, **échec de backup**) + récepteur (Slack/PagerDuty).
  *Complète directement P0 : alerter sur l'échec des sauvegardes.*

- [ ] **Durcissement des secrets statiques** — `openspec/changes/secrets-hardening/`
  `rabbitmq-credentials`, Grafana, mot de passe `app` restent lisibles via
  `kubectl get secret | base64 -d`. RBAC least-privilege + chiffrement at-rest (KMS) +
  audit + sourcing Vault KV. *(Les creds DB sont déjà dynamiques — ceux-ci sont la suite.)*

## 🟡 P2 — Amélioration (résilience & cible prod)

- [ ] **PDB + NetworkPolicies** — `openspec/changes/pdb-networkpolicies/`
  Sans PDB, un drain peut évincer tous les réplicas d'un service ; sans NetworkPolicy, tout
  pod parle à tout pod. PDB `minAvailable` + default-deny + flux ciblés (CNI réel requis).
  *PDB = quick win ; NetworkPolicies = à valider sous Calico/Cilium (no-op sur kindnet).*

- [ ] **Config Vault déclarative (cible prod)** — `openspec/changes/vault-config-pulumi-provider/`
  Remplacer le Job de bootstrap in-cluster (Option A, dev) par le provider `pulumi-vault`
  (Option B) : auth k8s + DB engine + rôles + policies déclaratifs, idempotents, diffables.

---

## ✅ Déjà livré (pour mémoire)

- Observabilité migrée vers **kube-prometheus-stack** (Operator + ServiceMonitors, 6 dashboards).
- **HA multi-nœuds** validée (anti-affinité, failover CNPG/RabbitMQ, spike 1000 VU).
- **Vault** : serveur + VSO + config + **creds PostgreSQL dynamiques** (order-api + inventory-api).
- Fix `pg_hba` `/16` (auth superuser multi-nœuds), métriques RabbitMQ en dev, préchargement images Vault.

---

## Notes diverses

- ⚠️ **Vault dev** : le root token a transité en clair lors d'une session → re-init au besoin
  (cf. `docs/vault.md`). `vault-init.json` + `vault:rootToken` sont **par instance** (caducs au reset).
- Compte Vault **non-root** (userpass/AppRole) pour ne plus utiliser le root token au quotidien.
- `pg_hba` prod : passer `trust` → `scram-sha-256` + `enableSuperuserAccess` (paramétrage par stack).
