using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class MetricsServerResourcesArgs
{
    /// <summary>
    /// Version du chart Helm metrics-server (kubernetes-sigs/metrics-server).
    /// Correspondance chart → app :
    ///   3.12.2 → v0.7.2  (actuelle, recommandée)
    /// Configurable via metricsServer:version dans Pulumi.*.yaml.
    /// </summary>
    public string Version { get; set; } = "3.12.2";

    /// <summary>
    /// Active --kubelet-insecure-tls pour contourner l'absence d'IP SANs dans
    /// les certificats auto-signés des kubelets Kind.
    /// true  = dev (Kind)  — OBLIGATOIRE, sinon x509 certificate error
    /// false = prod        — les kubelets exposent des certs signés par l'autorité cluster
    /// </summary>
    public bool KubeletInsecureTls { get; set; } = true;
}

/// <summary>
/// Installe le Metrics Server via Helm dans kube-system.
///
/// Le Metrics Server collecte les métriques CPU et mémoire des pods/nœuds
/// depuis les kubelets (API /metrics/resource) et les expose via l'API K8s
/// (kubectl top, HPA v2).
///
/// Pourquoi Pulumi vs kubectl manuel ?
///   Sans Metrics Server, kubectl top et les HPA CPU affichent &lt;unknown&gt;.
///   En l'intégrant à Pulumi, il est provisionné automatiquement à chaque
///   `pulumi up` — fini l'étape manuelle post-déploiement.
///
/// Spécificité Kind :
///   Les kubelets Kind utilisent des certificats TLS auto-signés sans IP SAN.
///   Le Metrics Server échoue avec "x509: cannot validate certificate" lors
///   de la connexion aux kubelets → workaround : --kubelet-insecure-tls.
///   Ce flag est activé via KubeletInsecureTls = true (défaut dev).
///   En production, ne PAS activer ce flag (kubelets avec certs valides).
///
/// Métriques disponibles après déploiement :
///   kubectl top nodes
///   kubectl top pods -n ecommerce
///   kubectl get hpa -n ecommerce  →  cpu: XX%/70% (au lieu de &lt;unknown&gt;)
///
/// Accès :
///   kubectl port-forward -n kube-system svc/metrics-server 4443:443
///   → https://localhost:4443/apis/metrics.k8s.io/v1beta1/pods
/// </summary>
public class MetricsServerResources : ComponentResource
{
    public MetricsServerResources(string name, MetricsServerResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:MetricsServerResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // Args supplémentaires selon l'environnement.
        // --kubelet-insecure-tls : contourne la vérification x509 sur Kind.
        // Les defaultArgs du chart (--cert-dir, --kubelet-preferred-address-types, etc.)
        // sont conservés ; on n'ajoute que ce flag en extra.
        var extraArgs = args.KubeletInsecureTls
            ? new List<object> { "--kubelet-insecure-tls" }
            : new List<object>();

        // ── Helm release (namespace : kube-system) ────────────────────────────
        // WaitForJobs = true : Pulumi attend que le Deployment soit Ready.
        // Timeout = 180 s : léger (l'image est petite, ~70 MB).
        // Pré-charger l'image dans Kind avant pulumi up :
        //   podman pull registry.k8s.io/metrics-server/metrics-server:v0.7.2
        //   kind load docker-image registry.k8s.io/metrics-server/metrics-server:v0.7.2 --name ecommerce
        _ = new Release("metrics-server", new ReleaseArgs
        {
            // Force le nom du release pour avoir "metrics-server" comme préfixe
            // dans les ressources (Service, Deployment, RBAC).
            Name            = "metrics-server",
            Chart           = "metrics-server",
            Version         = args.Version,
            Namespace       = "kube-system",
            CreateNamespace = false, // kube-system existe toujours
            RepositoryOpts  = new RepositoryOptsArgs
            {
                Repo = "https://kubernetes-sigs.github.io/metrics-server/"
            },
            WaitForJobs = true,
            Timeout     = 180,
            Values      = new InputMap<object>
            {
                // Args additionnels (s'ajoutent aux defaultArgs du chart).
                // defaultArgs inclut déjà :
                //   --cert-dir=/tmp
                //   --kubelet-preferred-address-types=InternalIP,ExternalIP,Hostname
                //   --kubelet-use-node-status-port
                //   --metric-resolution=15s
                ["args"] = extraArgs,

                // Ressources conservatives pour Kind (1 nœud).
                // En prod multi-nœuds, augmenter requests/limits selon la taille du cluster.
                ["resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new Dictionary<string, object>
                    {
                        ["cpu"]    = "10m",
                        ["memory"] = "32Mi"
                    },
                    ["limits"] = new Dictionary<string, object>
                    {
                        ["cpu"]    = "100m",
                        ["memory"] = "128Mi"
                    }
                }
            }
        }, baseOpts);

        RegisterOutputs();
    }
}
