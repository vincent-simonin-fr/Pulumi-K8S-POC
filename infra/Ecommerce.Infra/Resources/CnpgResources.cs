using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class CnpgResourcesArgs
{
    /// <summary>
    /// Version du chart Helm cloudnative-pg (pas la version de l'operateur).
    /// Correspondance : chart 0.22.0 => operateur 1.24.0, chart 0.28.x => operateur 1.29.x.
    /// Configurable via cnpg:version dans Pulumi.*.yaml.
    /// </summary>
    public string Version { get; set; } = "0.22.0";
}

/// <summary>
/// Installe l'opérateur CloudNativePG (CNPG) via Helm dans le namespace cnpg-system.
///
/// CNPG est un opérateur K8s CNCF pour PostgreSQL HA. Il enregistre les CRDs :
///   Cluster, Pooler, Backup, ScheduledBackup dans l'API server K8s.
///
/// Pourquoi CNPG vs StatefulSet manuel ?
///   - HA automatique : failover en ~30 s si le primary crashe (streaming replication)
///   - PgBouncer intégré (Pooler CRD) : élimine les "too many clients" sous KEDA scaling
///   - Services automatiques : {cluster}-rw (primary), {cluster}-ro (replicas), {cluster}-r (any)
///   - Backup S3 (prod) : ScheduledBackup + WAL archiving sans plugin tiers
///
/// Workaround GVK cache Pulumi (identique à KEDA — voir KedaResources.cs) :
///   Le provider Pulumi.Kubernetes met en cache la liste des GVK au démarrage.
///   Les CRDs CNPG installées pendant ce même pulumi up ne sont pas connues du cache.
///   → DatabaseResources utilise kubectl apply via Pulumi.Command pour les appliquer,
///     contournant ainsi le cache de discovery GVK du provider Kubernetes.
///
/// Flux de provisionnement :
///   1. CnpgResources (ce composant) — Helm install, WaitForJobs=true
///   2. DatabaseResources (DependsOn CnpgResources dans EcommerceStack)
///      → kubectl apply Cluster + Pooler YAML via Pulumi.Command
/// </summary>
public class CnpgResources : ComponentResource
{
    public CnpgResources(string name, CnpgResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:CnpgResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // ── CNPG Helm release (namespace : cnpg-system) ───────────────────────
        // WaitForJobs = true : Pulumi attend que l'opérateur CNPG soit Ready
        // AVANT de continuer vers DatabaseResources.
        //
        // Obligatoire : les CRDs (Cluster, Pooler, Backup, ScheduledBackup) doivent
        // être enregistrées dans l'API K8s avant que DatabaseResources ne tente de
        // les appliquer via kubectl (même pulumi up = cache GVK non rafraîchi).
        //
        // Timeout = 300 s (5 min) : suffisant si les images sont pré-chargées dans
        // Kind via k8s_complete_launch.cmd (podman pull + kind load).
        // Sans pré-chargement : tirer depuis ghcr.io peut prendre plusieurs minutes
        // sur une connexion lente.
        _ = new Release("cnpg-operator", new ReleaseArgs
        {
            Chart           = "cloudnative-pg",
            Version         = args.Version,
            Namespace       = "cnpg-system",
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs
            {
                Repo = "https://cloudnative-pg.github.io/charts"
            },
            WaitForJobs = true,
            Timeout     = 300,
            Values      = new InputMap<object>
            {
                // Ressources conservatrices pour Kind (cluster local 1 nœud).
                // En production, ajuster selon la charge et le nombre de clusters gérés.
                ["resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new Dictionary<string, object>
                    {
                        ["cpu"]    = "10m",
                        ["memory"] = "64Mi"
                    },
                    ["limits"] = new Dictionary<string, object>
                    {
                        ["cpu"]    = "200m",
                        ["memory"] = "256Mi"
                    }
                }
            }
        }, baseOpts);

        RegisterOutputs();
    }
}
