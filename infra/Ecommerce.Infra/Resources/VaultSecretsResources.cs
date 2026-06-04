using Pulumi;
using Pulumi.Command.Local;

namespace Ecommerce.Infra.Resources;

public class VaultSecretsResourcesArgs
{
    /// <summary>Namespace applicatif où vivent les CRDs VSO + les Secrets rotatés.</summary>
    public string Namespace { get; set; } = "ecommerce";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  Livraison VSO — CRDs Vault Secrets Operator (Phase 3d).
///
///  Matérialise des creds PostgreSQL dynamiques (database/creds/order-app) dans un
///  Secret K8s 'order-db-dynamic', rotaté par VSO selon le bail (TTL 1h) :
///    - ServiceAccount vault-auth : identité autorisée par le rôle k8s Vault 'order-app'
///    - VaultConnection           : adresse du serveur Vault
///    - VaultAuth                 : auth Kubernetes (rôle order-app, SA vault-auth)
///    - VaultDynamicSecret        : database/creds/order-app -> Secret 'order-db-dynamic'
///
///  Appliqué via kubectl (Pulumi.Command) car les CRDs secrets.hashicorp.com sont
///  installées par le chart VSO pendant ce même pulumi up → absentes du cache GVK
///  du provider (même contrainte que CNPG/ServiceMonitors).
///
///  Pré-requis : VSO installé + Vault configuré (auth k8s + rôle order-app), assurés
///  par les DependsOn en amont (EcommerceStack).
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class VaultSecretsResources : ComponentResource
{
    public VaultSecretsResources(string name, VaultSecretsResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:VaultSecretsResources", name, opts)
    {
        var ns = args.Namespace;

        // Redémarrage rolling à la rotation : l'username dynamique change à chaque bail
        // → le pod doit relire le Secret au boot (pas juste rafraîchir le password Npgsql).
        const string rolloutRestart = @"
  rolloutRestartTargets:
    - kind: Deployment
      name: order-api";

        const string rolloutRestartInv = @"
  rolloutRestartTargets:
    - kind: Deployment
      name: inventory-api";

        var yaml = $@"
apiVersion: v1
kind: ServiceAccount
metadata:
  name: vault-auth
  namespace: {ns}
---
apiVersion: secrets.hashicorp.com/v1beta1
kind: VaultConnection
metadata:
  name: vault-connection
  namespace: {ns}
spec:
  address: http://vault.vault.svc.cluster.local:8200
---
apiVersion: secrets.hashicorp.com/v1beta1
kind: VaultAuth
metadata:
  name: vault-auth
  namespace: {ns}
spec:
  vaultConnectionRef: vault-connection
  method: kubernetes
  mount: kubernetes
  kubernetes:
    role: order-app
    serviceAccount: vault-auth
---
apiVersion: secrets.hashicorp.com/v1beta1
kind: VaultDynamicSecret
metadata:
  name: order-db-dynamic
  namespace: {ns}
spec:
  vaultAuthRef: vault-auth
  mount: database
  path: creds/order-app
  destination:
    create: true
    name: order-db-dynamic
    transformation:
      excludeRaw: true
      templates:
        ConnectionStrings__OrderDb:
          text: 'Host=order-db-rw;Port=5432;Database=order_db;Username={{{{ .Secrets.username }}}};Password={{{{ .Secrets.password }}}};Maximum Pool Size=15;Minimum Pool Size=0'{rolloutRestart}
---
apiVersion: secrets.hashicorp.com/v1beta1
kind: VaultAuth
metadata:
  name: vault-auth-inventory
  namespace: {ns}
spec:
  vaultConnectionRef: vault-connection
  method: kubernetes
  mount: kubernetes
  kubernetes:
    role: inventory-app
    serviceAccount: vault-auth
---
apiVersion: secrets.hashicorp.com/v1beta1
kind: VaultDynamicSecret
metadata:
  name: inventory-db-dynamic
  namespace: {ns}
spec:
  vaultAuthRef: vault-auth-inventory
  mount: database
  path: creds/inventory-app
  destination:
    create: true
    name: inventory-db-dynamic
    transformation:
      excludeRaw: true
      templates:
        ConnectionStrings__InventoryDb:
          text: 'Host=inventory-db-rw;Port=5432;Database=inventory_db;Username={{{{ .Secrets.username }}}};Password={{{{ .Secrets.password }}}};Maximum Pool Size=15;Minimum Pool Size=0'{rolloutRestartInv}
";

        _ = new Command("vault-secrets-apply", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = yaml
        }, new CustomResourceOptions { Parent = this });

        RegisterOutputs();
    }
}
