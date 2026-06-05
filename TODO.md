# TODO — durcissement production

Chantiers de mise en production restants, **classés par priorité**. Chacun est cadré
par une proposition **OpenSpec** (`openspec/changes/<nom>/` : proposal + design + specs +
tasks) — implémentable via `/opsx:apply <nom>`.

> Contexte : la stack est fonctionnelle (CNPG HA, RabbitMQ, observabilité
> kube-prometheus-stack, KEDA, ArgoCD/GitOps, Vault + creds PostgreSQL dynamiques).
> Ces items comblent les écarts restants pour une **vraie prod**.

---

## 🟠 P1 — Important (sécurité & exploitabilité)

- [ ] **Alerting** — `openspec/changes/observability-alerting/`
      Dashboards présents mais **zéro alerte** : rien ne réveille en cas d'incident. Activer
      Alertmanager + `PrometheusRule` (CrashLoop, CNPG primary down, RabbitMQ quorum, latence
      p95, saturation pool PG, **échec de backup**) + récepteur (Slack/PagerDuty).
      _Désormais le chantier le plus prioritaire : les backups existent (P0 livré) mais rien
      n'alerte si un `ScheduledBackup` échoue ou si l'archivage WAL décroche._

- [ ] **Durcissement des secrets statiques** — `openspec/changes/secrets-hardening/`
      `rabbitmq-credentials`, Grafana, mot de passe `app` restent lisibles via
      `kubectl get secret | base64 -d`. RBAC least-privilege + chiffrement at-rest (KMS) +
      audit + sourcing Vault KV. _(Les creds DB sont déjà dynamiques — ceux-ci sont la suite.)_

## 🟡 P2 — Amélioration (résilience & cible prod)

- [ ] **PDB + NetworkPolicies** — `openspec/changes/pdb-networkpolicies/`
      Sans PDB, un drain peut évincer tous les réplicas d'un service ; sans NetworkPolicy, tout
      pod parle à tout pod. PDB `minAvailable` + default-deny + flux ciblés (CNI réel requis).
      _PDB = quick win ; NetworkPolicies = à valider sous Calico/Cilium (no-op sur kindnet)._

- [ ] **Config Vault déclarative (cible prod)** — `openspec/changes/vault-config-pulumi-provider/`
      Remplacer le Job de bootstrap in-cluster (Option A, dev) par le provider `pulumi-vault`
      (Option B) : auth k8s + DB engine + rôles + policies déclaratifs, idempotents, diffables.

---

## ✅ Déjà livré (pour mémoire)

- **Sauvegardes CNPG (DR)** — Barman + WAL archiving → **MinIO** (S3), `ScheduledBackup`
  quotidien, **PITR testé de bout en bout** (DROP TABLE récupéré à l'instant T), **RPO/RTO
  documentés** (`docs/backups.md`). _(ex-P0)_
- **Probes gateway « shallow »** — liveness/readiness découplées de la santé de l'aval
  (`/health/live` + `/health/ready`) : une panne order-api/inventory-api ne fait plus
  crashlooper la gateway ni déclencher de scale HPA parasite (`docs/architecture.md`).
- Observabilité migrée vers **kube-prometheus-stack** (Operator + ServiceMonitors, 6 dashboards).
- **HA multi-nœuds** validée (anti-affinité, failover CNPG/RabbitMQ, spike 1000 VU).
- **Vault** : serveur + VSO + config + **creds PostgreSQL dynamiques** (order-api + inventory-api).
- Fix `pg_hba` `/16` (auth superuser multi-nœuds), métriques RabbitMQ en dev, préchargement images Vault.

---

## Notes diverses

- Compte Vault **non-root** (userpass/AppRole) pour ne plus utiliser le root token au quotidien.
- `pg_hba` prod : passer `trust` → `scram-sha-256` + `enableSuperuserAccess` (paramétrage par stack).
