#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
#  Configuration Vault (Option A — Job in-cluster). Idempotent.
#  Exécuté par VaultConfigResources (Job K8s) avec :
#    - VAULT_ADDR  : adresse du serveur Vault
#    - VAULT_TOKEN : token d'admin (root en dev), injecté depuis un Secret K8s
#  Le CLI vault lit VAULT_ADDR/VAULT_TOKEN dans l'env → pas de 'vault login'.
#
#  Configure : moteur database -> order-db (CNPG), rôle dynamique order-app,
#  auth Kubernetes, policy + rôle k8s order-app (lié au SA ecommerce/vault-auth).
# ─────────────────────────────────────────────────────────────────────────────
set -e

echo ">> [1/6] moteur database (idempotent)"
vault secrets enable database 2>/dev/null || echo "   (deja active)"

echo ">> [2/6] connexion order-db (postgres via pg_hba trust en dev)"
vault write database/config/order-db \
  plugin_name=postgresql-database-plugin \
  allowed_roles=order-app \
  connection_url='postgresql://{{username}}:{{password}}@order-db-rw.ecommerce.svc.cluster.local:5432/order_db?sslmode=disable' \
  username=postgres \
  password=ignored-by-trust

echo ">> [3/6] rôle dynamique order-app (user éphémère MEMBRE de 'app', TTL 1h)"
vault write database/roles/order-app \
  db_name=order-db \
  creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}' IN ROLE app;" \
  revocation_statements="REASSIGN OWNED BY \"{{name}}\" TO app; DROP OWNED BY \"{{name}}\"; DROP ROLE IF EXISTS \"{{name}}\";" \
  default_ttl=1h \
  max_ttl=24h

echo ">> [4/6] auth Kubernetes (idempotent)"
vault auth enable kubernetes 2>/dev/null || echo "   (deja active)"
vault write auth/kubernetes/config kubernetes_host=https://kubernetes.default.svc:443

echo ">> [5/6] policy order-app-policy"
vault policy write order-app-policy - <<'EOF'
path "database/creds/order-app" {
  capabilities = ["read"]
}
EOF

echo ">> [6/6] rôle k8s order-app (SA ecommerce/vault-auth)"
vault write auth/kubernetes/role/order-app \
  bound_service_account_names=vault-auth \
  bound_service_account_namespaces=ecommerce \
  policies=order-app-policy \
  ttl=1h

echo ">> [inventory 1/3] connexion inventory-db"
vault write database/config/inventory-db \
  plugin_name=postgresql-database-plugin \
  allowed_roles=inventory-app \
  connection_url='postgresql://{{username}}:{{password}}@inventory-db-rw.ecommerce.svc.cluster.local:5432/inventory_db?sslmode=disable' \
  username=postgres \
  password=ignored-by-trust

echo ">> [inventory 2/3] role dynamique inventory-app"
vault write database/roles/inventory-app \
  db_name=inventory-db \
  creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}' IN ROLE app;" \
  revocation_statements="REASSIGN OWNED BY \"{{name}}\" TO app; DROP OWNED BY \"{{name}}\"; DROP ROLE IF EXISTS \"{{name}}\";" \
  default_ttl=1h \
  max_ttl=24h

echo ">> [inventory 3/3] policy + role k8s inventory-app (SA ecommerce/vault-auth)"
vault policy write inventory-app-policy - <<'EOF'
path "database/creds/inventory-app" {
  capabilities = ["read"]
}
EOF
vault write auth/kubernetes/role/inventory-app \
  bound_service_account_names=vault-auth \
  bound_service_account_namespaces=ecommerce \
  policies=inventory-app-policy \
  ttl=1h

echo ">> Config Vault terminee."
