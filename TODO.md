# TODO — durcissement production

Chantiers de mise en production restants, **classés par priorité**. Chacun est cadré
par une proposition **OpenSpec** (`openspec/changes/<nom>/` : proposal + design + specs +
tasks) — implémentable via `/opsx:apply <nom>`.

> Contexte : la stack est fonctionnelle (CNPG HA, RabbitMQ, observabilité
> kube-prometheus-stack, KEDA, ArgoCD/GitOps, Vault + creds PostgreSQL dynamiques).
> Ces items comblent les écarts restants pour une **vraie prod**.

---

## 🟠 P1 — Important (sécurité & exploitabilité)

- [ ] **Durcissement des secrets statiques** — `openspec/changes/secrets-hardening/`
      `rabbitmq-credentials`, Grafana, mot de passe `app` restent lisibles via
      `kubectl get secret | base64 -d`. RBAC least-privilege + chiffrement at-rest (KMS) +
      audit + sourcing Vault KV. _(Les creds DB sont déjà dynamiques — ceux-ci sont la suite.)_

## 🟡 P2 — Amélioration (résilience & cible prod)

- [ ] **PDB + NetworkPolicies** — `openspec/changes/pdb-networkpolicies/`
      Sans PDB, un drain peut évincer tous les réplicas d'un service ; sans NetworkPolicy, tout
      pod parle à tout pod. PDB `minAvailable` + default-deny + flux ciblés (CNI réel requis).
      _PDB = quick win ; NetworkPolicies = à valider sous Calico/Cilium (no-op sur kindnet)._

---

## ✅ Déjà livré (pour mémoire)

- **Config Vault déclarative (Option B) — généralisée dev + prod** — `VaultDeclarativeConfigResources.cs`
  (provider `pulumi-vault` 7.10) : auth k8s + DB engine + rôles dynamiques + policies déclaratifs.
  **`configMode=provider` par défaut partout** (dev = Vault en **NodePort 30820** joignable depuis
  l'hôte ; prod = **Ingress `vault.{domain}` + TLS** créé par `IngressResources`) ; `job` = filet
  de secours. Auth provider par **AppRole** (token court-vécu scopé, repli root token). Docs à
  jour (README, vault.md, access.md, kubernetes.md, production.md).
  *(ex-P2 ; reste : validation live `pulumi up` mode provider + bootstrap AppRole prod, manuel une fois.)*
- **Alerting** — Alertmanager (chart) + 9 `PrometheusRule` (CrashLoop, CNPG no-primary /
  injoignable / lag / **backup en échec**, RabbitMQ down, latence p95, saturation pool PG),
  routage Slack par sévérité, seuils configurables (`alerting:*`), runbook + checklist
  (`docs/observability.md`). PromQL calibré sur les métriques réelles. *(ex-P1 ; validé live :
  `PodCrashLooping` pending→firing→Alertmanager, routage `critical` OK. Reste : webhook Slack réel en prod.)*
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

## Dette technique (à revisiter)

- **`pulumi-vault` 7.10 — workaround `VaultVersionOverride`** (`VaultDeclarativeConfigResources`).
  Bug du provider : le Diff de `kubernetes_auth_backend_config` panique sur une version serveur
  `nil` (nil pointer dans `go-version`). Ce **n'est pas** un souci de policy (le token lit bien
  la version). 7.10.0 = dernière **stable** (7.11 = alpha). On fournit la version explicitement
  (`vault:serverVersion`, défaut 1.21.2). **À faire** : suivre l'upstream pulumi-vault ; quand
  **7.11 est stable**, tester si le bug est corrigé → retirer `VaultVersionOverride` + `SkipGetVaultVersion`
  (ou les garder si on préfère l'épinglage explicite de version, légitime en IaC).
