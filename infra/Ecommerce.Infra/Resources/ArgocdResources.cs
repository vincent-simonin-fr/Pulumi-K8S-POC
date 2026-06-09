using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class ArgocdResourcesArgs
{
    /// <summary>Version du chart Helm argo-cd (argoproj/argo-helm). chart 7.x = Argo CD 2.x.</summary>
    public string Version { get; set; } = "7.8.3";

    /// <summary>Domaine de base (ex : "wizzz.com"). Utilisé pour construire argocd.wizzz.com en prod.</summary>
    public string Domain { get; set; } = "wizzz.com";

    /// <summary>Si true, crée des Ingress nginx pour argocd.{domain} (HTTP) et argocd-grpc.{domain} (CLI).</summary>
    public bool IngressEnabled { get; set; } = false;

    /// <summary>
    /// TLS local : utilise le ClusterIssuer self-signed (au lieu de Let's Encrypt) pour les
    /// Ingress Argo CD. Aligné sur ingress:selfSigned. Cf. IngressResources / docs/ingress-local.md.
    /// </summary>
    public bool SelfSigned { get; set; } = false;

    /// <summary>
    /// Hash bcrypt du mot de passe admin Argo CD.
    /// Si vide, Argo CD génère un secret aléatoire "argocd-initial-admin-secret".
    ///
    /// Génération :
    ///   htpasswd -nbBC 10 "" monMotDePasse | tr -d ':\n'
    ///   # ou :
    ///   docker run --rm httpd:alpine htpasswd -nbBC 10 "" monMotDePasse | tr -d ':\n'
    ///
    /// Production : pulumi config set --secret argocd:adminPasswordHash &lt;hash&gt;
    /// </summary>
    public string AdminPasswordBcrypt { get; set; } = "";

    /// <summary>
    /// Réplicas du serveur Argo CD (API + UI).
    /// Dev : 1 — Prod HA : 2 minimum (stateless, scalable horizontalement).
    /// </summary>
    public int ServerReplicas { get; set; } = 1;

    /// <summary>
    /// Réplicas du repo-server (clone repos Git, render Helm/Kustomize, vérifie GPG).
    /// Dev : 1 — Prod HA : 2 minimum (goulot d'étranglement sur les gros clusters).
    /// </summary>
    public int RepoServerReplicas { get; set; } = 1;

    /// <summary>
    /// Réplicas de l'ApplicationSet controller.
    /// Dev : 1 — Prod HA : 2 (leader election intégrée).
    /// </summary>
    public int ApplicationSetReplicas { get; set; } = 1;
}

/// <summary>
/// Installe Argo CD via Helm et configure un setup production-grade :
///
///   - Mode insecure (TLS terminé par l'ingress nginx en prod, par port-forward en dev)
///   - RBAC : read-only par défaut, groupe "admins" avec droits complets
///   - Métriques Prometheus activées sur tous les composants
///   - Dex SSO désactivé (activable via OIDC GitHub/GitLab/Azure AD)
///   - Notifications activées (Slack, email — à configurer dans argocd-notifications-cm)
///   - Redis intégré (remplacer par Redis HA pour les clusters > 3 nœuds)
///
/// Accès dev (sans ingress) :
///   kubectl port-forward -n argocd svc/argocd-server 8080:80
///   → http://localhost:8080  (admin / mot de passe ci-dessous)
///   kubectl -n argocd get secret argocd-initial-admin-secret \
///     -o jsonpath="{.data.password}" | base64 -d
///
/// CLI :
///   argocd login localhost:8080 --username admin --insecure
///   argocd app list
///
/// Scaling prod HA :
///   pulumi config set argocd:serverReplicas         2
///   pulumi config set argocd:repoServerReplicas     2
///   pulumi config set argocd:applicationSetReplicas 2
///   pulumi up --yes
/// </summary>
public class ArgocdResources : ComponentResource
{
    /// <summary>URL Argo CD — http://localhost:8080 en dev, https://argocd.{domain} en prod.</summary>
    public Output<string> ArgocdUrl { get; }

    public ArgocdResources(string name, ArgocdResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:ArgocdResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        var argocdUrl = args.IngressEnabled
            ? $"https://argocd.{args.Domain}"
            : "http://localhost:8080";

        // ── Valeurs Helm ──────────────────────────────────────────────────────
        // Organisées par composant (server, controller, repoServer, applicationSet,
        // dex, notifications, redis) pour faciliter la lecture et la surcharge prod.
        var values = new InputMap<object>
        {
            // ── Global ────────────────────────────────────────────────────────
            ["global"] = new Dictionary<string, object>
            {
                // Utilisé par le chart pour construire les hostnames d'ingress et l'URL de config.
                ["domain"] = $"argocd.{args.Domain}"
            },

            // ── Config ────────────────────────────────────────────────────────
            ["configs"] = BuildConfigs(args, argocdUrl),

            // ── Application Controller ────────────────────────────────────────
            // Composant stateful : réconcilie l'état Git ↔ cluster.
            // Une seule instance active via leader election (même en HA).
            ["controller"] = new Dictionary<string, object>
            {
                ["metrics"] = new Dictionary<string, object> { ["enabled"] = true },
                ["resources"] = Resources("100m", "500m", "128Mi", "512Mi")
            },

            // ── Server ────────────────────────────────────────────────────────
            ["server"] = BuildServer(args),

            // ── Repo Server ───────────────────────────────────────────────────
            // Clone les repos Git, render Helm/Kustomize, valide les manifestes.
            // Premier candidat au scale-out sur les gros clusters.
            ["repoServer"] = new Dictionary<string, object>
            {
                ["replicas"] = args.RepoServerReplicas,
                ["metrics"]  = new Dictionary<string, object> { ["enabled"] = true },
                ["resources"] = Resources("50m", "500m", "64Mi", "512Mi"),
                // Montées en mémoire sur les gros repos Git : augmenter les limits si OOMKill.
                ["env"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "ARGOCD_GIT_ATTEMPTS_COUNT", ["value"] = "5" }
                }
            },

            // ── ApplicationSet Controller ─────────────────────────────────────
            // Génère des Applications Argo CD à partir de templates (multi-cluster, matrix, etc.).
            ["applicationSet"] = new Dictionary<string, object>
            {
                ["replicas"] = args.ApplicationSetReplicas,
                ["metrics"]  = new Dictionary<string, object> { ["enabled"] = true },
                ["resources"] = Resources("10m", "100m", "32Mi", "128Mi")
            },

            // ── Dex (SSO) ─────────────────────────────────────────────────────
            // Désactivé : auth locale uniquement.
            // Pour activer OIDC (GitHub, GitLab, Azure AD) :
            //   1. dex.enabled: true
            //   2. configs.cm.oidc.config: ... (voir docs/argocd.md → section SSO)
            ["dex"] = new Dictionary<string, object>
            {
                ["enabled"] = false
            },

            // ── Notifications ─────────────────────────────────────────────────
            // Activé sans template : configure les notifications dans le ConfigMap
            // argocd-notifications-cm (Slack, Teams, email, webhook).
            ["notifications"] = new Dictionary<string, object>
            {
                ["enabled"]   = true,
                ["resources"] = Resources("10m", "100m", "32Mi", "128Mi")
            },

            // ── Redis ─────────────────────────────────────────────────────────
            // Redis intégré d'Argo CD (différent du Redis ecommerce).
            // Pour prod HA multi-nœuds : passer à redis-ha.enabled: true.
            ["redis"] = new Dictionary<string, object>
            {
                ["enabled"]   = true,
                ["resources"] = Resources("10m", "200m", "32Mi", "128Mi")
            }
        };

        // ── Helm release ──────────────────────────────────────────────────────
        // WaitForJobs = true : attend que les CRDs (Application, AppProject, ApplicationSet)
        // et le webhook soient enregistrés dans l'API K8s avant de terminer.
        // Timeout 600 s : les images Argo CD (quay.io/argoproj/argocd) peuvent être longues
        // à puller — pré-charger avec : podman pull + kind load (voir docs/argocd.md).
        _ = new Release("argocd", new ReleaseArgs
        {
            // Name force le nom du release Helm (sans quoi Pulumi ajoute un hash aléatoire :
            // "argocd-a57fc169-server" au lieu de "argocd-server").
            // Les services, ConfigMaps et RBAC créés par le chart utilisent ce nom comme préfixe.
            Name            = "argocd",
            Chart           = "argo-cd",
            Version         = args.Version,
            Namespace       = "argocd",
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs { Repo = "https://argoproj.github.io/argo-helm" },
            WaitForJobs     = true,
            Timeout         = 600,
            Values          = values
        }, baseOpts);

        ArgocdUrl = Output.Create(argocdUrl);
        RegisterOutputs(new Dictionary<string, object?> { ["argocdUrl"] = ArgocdUrl });
    }

    /// <summary>Construit la section configs (params, cm, rbac, secret).</summary>
    private static Dictionary<string, object> BuildConfigs(ArgocdResourcesArgs args, string argocdUrl)
    {
        var secretValues = string.IsNullOrEmpty(args.AdminPasswordBcrypt)
            // Sans hash : Argo CD génère argocd-initial-admin-secret au premier démarrage.
            // kubectl -n argocd get secret argocd-initial-admin-secret -o jsonpath="{.data.password}" | base64 -d
            ? (object)new Dictionary<string, object>()
            : new Dictionary<string, object>
            {
                // Hash bcrypt du mot de passe admin.
                // Le mtime force Argo CD à recharger le hash si la valeur change.
                ["argocdServerAdminPassword"]      = args.AdminPasswordBcrypt,
                ["argocdServerAdminPasswordMtime"] = "2024-01-01T00:00:00Z"
            };

        return new Dictionary<string, object>
        {
            ["params"] = new Dictionary<string, object>
            {
                // Mode insecure : Argo CD expose HTTP, TLS terminé en amont.
                // En prod : l'ingress nginx gère le TLS (cert-manager + Let's Encrypt).
                // En dev  : kubectl port-forward expose le port 80 en local.
                ["server.insecure"] = "true"
            },
            ["cm"] = new Dictionary<string, object>
            {
                // URL de l'instance — apparaît dans les liens des notifications.
                ["url"]           = argocdUrl,
                // Compte admin local activé. Désactivez-le après avoir configuré un SSO.
                ["admin.enabled"] = "true",
                // Label utilisé pour associer les ressources K8s à une Application Argo CD.
                // Valeur par défaut : "app.kubernetes.io/instance" (compatible avec le projet).
                ["application.instanceLabelKey"] = "app.kubernetes.io/instance"
            },
            ["rbac"] = new Dictionary<string, object>
            {
                // Sécurité par défaut : lecture seule pour tous les utilisateurs non-admins.
                // Empêche les modifications accidentelles depuis l'UI.
                ["policy.default"] = "role:readonly",
                // Le groupe "admins" reçoit tous les droits.
                // Avec SSO : mapper le groupe de votre IDP à "admins".
                ["policy.csv"] = "g, admins, role:admin\n"
            },
            ["secret"] = secretValues
        };
    }

    /// <summary>Construit la section server (replicas, resources, ingress).</summary>
    private static Dictionary<string, object> BuildServer(ArgocdResourcesArgs args)
    {
        var server = new Dictionary<string, object>
        {
            ["replicas"]  = args.ServerReplicas,
            ["metrics"]   = new Dictionary<string, object> { ["enabled"] = true },
            ["resources"] = Resources("50m", "500m", "64Mi", "256Mi")
        };

        if (!args.IngressEnabled)
            return server;

        // ── Ingress prod ──────────────────────────────────────────────────────
        // Deux ingress nginx sont nécessaires pour Argo CD :
        //   1. HTTP (UI + API REST)  → argocd.{domain}
        //   2. gRPC (CLI argocd)     → argocd-grpc.{domain}
        // Sans le second, "argocd app list" échoue avec "transport: Error while dialing".

        // Ingress HTTP : UI Argo CD + API REST
        server["ingress"] = new Dictionary<string, object>
        {
            ["enabled"]          = true,
            ["ingressClassName"] = "nginx",
            ["hostname"]         = $"argocd.{args.Domain}",
            ["annotations"] = new Dictionary<string, object>
            {
                // cert-manager émet un certificat Let's Encrypt.
                ["cert-manager.io/cluster-issuer"]                  = args.SelfSigned ? "selfsigned" : "letsencrypt-prod",
                // Argo CD tourne en mode insecure → le backend est HTTP pur.
                ["nginx.ingress.kubernetes.io/backend-protocol"]    = "HTTP",
                ["nginx.ingress.kubernetes.io/force-ssl-redirect"]  = "true"
            },
            ["tls"] = true
        };

        // Ingress gRPC : CLI argocd (argocd login, argocd app sync...)
        // Nécessite que le backend soit exposé via GRPC (pas HTTP).
        server["ingressGrpc"] = new Dictionary<string, object>
        {
            ["enabled"]          = true,
            ["ingressClassName"] = "nginx",
            ["hostname"]         = $"argocd-grpc.{args.Domain}",
            ["annotations"] = new Dictionary<string, object>
            {
                ["cert-manager.io/cluster-issuer"]                 = args.SelfSigned ? "selfsigned" : "letsencrypt-prod",
                ["nginx.ingress.kubernetes.io/backend-protocol"]   = "GRPC",
                ["nginx.ingress.kubernetes.io/ssl-redirect"]       = "true"
            },
            ["tls"] = true
        };

        return server;
    }

    /// <summary>Helper — génère une section resources Kubernetes (requests + limits).</summary>
    private static Dictionary<string, object> Resources(
        string cpuRequest, string cpuLimit,
        string memRequest, string memLimit) =>
        new()
        {
            ["requests"] = new Dictionary<string, object> { ["cpu"] = cpuRequest, ["memory"] = memRequest },
            ["limits"]   = new Dictionary<string, object> { ["cpu"] = cpuLimit,   ["memory"] = memLimit  }
        };
}
