using Pulumi;
using Vault = Pulumi.Vault;

namespace Ecommerce.Infra.Resources;

public class VaultDeclarativeConfigResourcesArgs
{
    /// <summary>
    /// Adresse de Vault JOIGNABLE DEPUIS L'HÔTE Pulumi (≠ DNS in-cluster) :
    ///   - dev   : http://127.0.0.1:8200 via `kubectl port-forward -n vault svc/vault 8200:8200`
    ///   - prod  : https://vault.{domain} (Ingress + TLS)
    /// </summary>
    public required string VaultAddress { get; set; }

    /// <summary>Token d'admin Vault (secret). Jamais committé. Idéalement court-vécu (AppRole) en prod.</summary>
    public required Input<string> AdminToken { get; set; }

    /// <summary>API server Kubernetes (utilisé par l'auth method kubernetes côté Vault).</summary>
    public string KubernetesHost { get; set; } = "https://kubernetes.default.svc:443";

    /// <summary>Compte admin PostgreSQL utilisé par le DB engine. Dev : postgres (pg_hba trust).</summary>
    public Input<string> DbAdminUser { get; set; } = "postgres";

    /// <summary>Mot de passe admin PostgreSQL. Dev : ignoré (trust). Prod : fournir via secret.</summary>
    public Input<string> DbAdminPassword { get; set; } = "ignored-by-trust";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  Configuration Vault DÉCLARATIVE (Option B — provider pulumi-vault, cible prod).
///
///  Équivalent déclaratif/idempotent du Job in-cluster (Option A, scripts/vault-config.sh).
///  Crée, en ressources Pulumi.Vault (donc diffables au `pulumi up`) :
///    - le database secrets engine (mount "database"),
///    - une connexion par cluster CNPG (order-db / inventory-db, via {cluster}-rw),
///    - un rôle dynamique par DB (SQL création/révocation, TTL bornés 1h/24h),
///    - l'auth method Kubernetes (+ config API server),
///    - une policy de moindre privilège + un rôle k8s par DB (liés au SA ecommerce/vault-auth).
///
///  ⚠️ Le provider pulumi-vault s'exécute sur l'HÔTE Pulumi → Vault doit être JOIGNABLE
///     depuis l'hôte (port-forward en dev, Ingress/TLS en prod) et un token d'admin fourni.
///     Si Vault est scellé/injoignable, `pulumi up` échoue explicitement (c'est voulu).
///
///  PARITÉ avec l'Option A : mêmes noms (order-app/inventory-app), même SQL, mêmes TTL,
///  même policy → bascule dev(job)↔prod(provider) sans divergence de comportement.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class VaultDeclarativeConfigResources : ComponentResource
{
    // SQL identique au script (scripts/vault-config.sh) — user éphémère MEMBRE de 'app',
    // révocation qui réassigne les objets à 'app' pour que le DROP ROLE réussisse.
    private const string CreationSql =
        "CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}' IN ROLE app;";
    private const string RevocationSql =
        "REASSIGN OWNED BY \"{{name}}\" TO app; DROP OWNED BY \"{{name}}\"; DROP ROLE IF EXISTS \"{{name}}\";";

    public VaultDeclarativeConfigResources(string name, VaultDeclarativeConfigResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:VaultDeclarativeConfigResources", name, opts)
    {
        // Provider explicite : toutes les ressources Vault ci-dessous ciblent CE Vault.
        var provider = new Vault.Provider("vault-provider", new Vault.ProviderArgs
        {
            Address = args.VaultAddress,
            Token   = args.AdminToken
        }, new CustomResourceOptions { Parent = this });

        var vaultOpts = new CustomResourceOptions { Parent = this, Provider = provider };

        // ── Database secrets engine ──────────────────────────────────────────────
        var dbMount = new Vault.Mount("vault-db-engine", new Vault.MountArgs
        {
            Path = "database",
            Type = "database"
        }, vaultOpts);

        // ── Auth method Kubernetes ───────────────────────────────────────────────
        var k8sAuth = new Vault.AuthBackend("vault-k8s-auth", new Vault.AuthBackendArgs
        {
            Type = "kubernetes"
        }, vaultOpts);

        _ = new Vault.Kubernetes.AuthBackendConfig("vault-k8s-auth-config", new Vault.Kubernetes.AuthBackendConfigArgs
        {
            Backend        = k8sAuth.Path,
            KubernetesHost = args.KubernetesHost
        }, new CustomResourceOptions { Parent = this, Provider = provider, DependsOn = { k8sAuth } });

        // ── Une config complète (connexion + rôle dynamique + policy + rôle k8s) par DB ─
        ConfigureDatabase(args, provider, dbMount, k8sAuth,
            id: "order",     dbName: "order-db",
            rwHost: "order-db-rw.ecommerce.svc.cluster.local", database: "order_db",
            roleName: "order-app", policyName: "order-app-policy");

        ConfigureDatabase(args, provider, dbMount, k8sAuth,
            id: "inventory", dbName: "inventory-db",
            rwHost: "inventory-db-rw.ecommerce.svc.cluster.local", database: "inventory_db",
            roleName: "inventory-app", policyName: "inventory-app-policy");

        RegisterOutputs();
    }

    private void ConfigureDatabase(
        VaultDeclarativeConfigResourcesArgs args, Vault.Provider provider,
        Vault.Mount dbMount, Vault.AuthBackend k8sAuth,
        string id, string dbName, string rwHost, string database, string roleName, string policyName)
    {
        var opts = new CustomResourceOptions { Parent = this, Provider = provider };

        // Connexion au cluster CNPG via le service {cluster}-rw (résolu par Vault in-cluster).
        var connection = new Vault.Database.SecretBackendConnection($"vault-db-conn-{id}", new Vault.Database.SecretBackendConnectionArgs
        {
            Backend      = dbMount.Path,
            Name         = dbName,
            AllowedRoles = { roleName },
            Postgresql   = new Vault.Database.Inputs.SecretBackendConnectionPostgresqlArgs
            {
                ConnectionUrl = $"postgresql://{{{{username}}}}:{{{{password}}}}@{rwHost}:5432/{database}?sslmode=disable",
                Username      = args.DbAdminUser,
                Password      = args.DbAdminPassword
            }
        }, new CustomResourceOptions { Parent = this, Provider = provider, DependsOn = { dbMount } });

        // Rôle dynamique : génère un user éphémère membre de 'app', TTL 1h / max 24h.
        _ = new Vault.Database.SecretBackendRole($"vault-db-role-{id}", new Vault.Database.SecretBackendRoleArgs
        {
            Backend             = dbMount.Path,
            Name                = roleName,
            DbName              = dbName,
            CreationStatements  = { CreationSql },
            RevocationStatements = { RevocationSql },
            DefaultTtl          = 3600,    // 1h
            MaxTtl              = 86400    // 24h
        }, new CustomResourceOptions { Parent = this, Provider = provider, DependsOn = { connection } });

        // Policy de moindre privilège : lecture du seul chemin de creds de ce rôle.
        var policy = new Vault.Policy($"vault-policy-{id}", new Vault.PolicyArgs
        {
            Name           = policyName,
            PolicyContents = $"path \"database/creds/{roleName}\" {{\n  capabilities = [\"read\"]\n}}\n"
        }, opts);

        // Rôle k8s : le SA ecommerce/vault-auth obtient un token portant cette policy.
        _ = new Vault.Kubernetes.AuthBackendRole($"vault-k8s-role-{id}", new Vault.Kubernetes.AuthBackendRoleArgs
        {
            Backend                       = k8sAuth.Path,
            RoleName                      = roleName,
            BoundServiceAccountNames      = { "vault-auth" },
            BoundServiceAccountNamespaces = { "ecommerce" },
            TokenPolicies                 = { policyName },
            TokenTtl                      = 3600
        }, new CustomResourceOptions { Parent = this, Provider = provider, DependsOn = { policy, k8sAuth } });
    }
}
