using Pulumi;
using Pulumi.Command.Local;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Networking.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Networking.V1;

namespace Ecommerce.Infra.Resources;

public class IngressResourcesArgs
{
    /// <summary>
    /// Domaine racine — wizzz.com donne :
    ///   wizzz.com          → gateway (API publique)
    ///   grafana.wizzz.com  → Grafana  (basic-auth)
    ///   jaeger.wizzz.com   → Jaeger   (basic-auth)
    ///   vault.wizzz.com    → Vault    (TLS only, auth par token — si VaultEnabled)
    /// Configurable via ingress:domain dans Pulumi.*.yaml.
    /// </summary>
    public string Domain { get; set; } = "wizzz.com";

    /// <summary>
    /// Email pour les notifications Let's Encrypt (expiration, révocation).
    /// Configurable via ingress:acmeEmail dans Pulumi.*.yaml.
    /// </summary>
    public string AcmeEmail { get; set; } = "ops@wizzz.com";

    /// <summary>
    /// Contenu htpasswd pour l'accès au monitoring (Grafana + Jaeger).
    /// Générer  : htpasswd -nb admin &lt;password&gt;
    /// Commande : pulumi config set --secret ingress:monitoringBasicAuthHtpasswd "$(htpasswd -nb admin monpass)"
    /// </summary>
    public string MonitoringBasicAuthHtpasswd { get; set; } = "";

    /// <summary>Version du chart Helm cert-manager (jetstack).</summary>
    public string CertManagerVersion { get; set; } = "v1.16.2";

    /// <summary>Version du chart Helm ingress-nginx.</summary>
    public string NginxVersion { get; set; } = "4.11.3";

    public string EcommerceNamespace { get; set; } = "ecommerce";
    public string MonitoringNamespace { get; set; } = "monitoring";

    /// <summary>
    /// Crée l'Ingress Vault (`vault.{domain}`) — requis en prod pour que le provider
    /// pulumi-vault (configMode=provider) joigne Vault depuis l'hôte. Lié à vault:enabled.
    /// </summary>
    public bool VaultEnabled { get; set; } = false;

    /// <summary>Namespace de Vault (où vit le Service `vault` ciblé par l'Ingress).</summary>
    public string VaultNamespace { get; set; } = "vault";

    /// <summary>
    /// TLS LOCAL : ClusterIssuer **self-signed** (cert-manager) au lieu de Let's Encrypt.
    /// Pour Kind, où ACME HTTP-01 est impossible (pas de DNS public). Le navigateur avertit,
    /// mais la terminaison TLS par nginx fonctionne. Configurable via `ingress:selfSigned`.
    /// </summary>
    public bool SelfSigned { get; set; } = false;

    /// <summary>
    /// nginx en **hostPort 80/443** (au lieu de LoadBalancer) — pour Kind, où le Service
    /// LoadBalancer reste &lt;pending&gt;. À combiner avec les extraPortMappings 80/443 de
    /// kind-config (→ localhost). Force replicaCount=1. Configurable via `ingress:nginxHostPort`.
    /// </summary>
    public bool NginxHostPort { get; set; } = false;
}

public class IngressResources : ComponentResource
{
    public IngressResources(string name, IngressResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:IngressResources", name, opts)
    {
        var domain        = args.Domain;
        var grafanaDomain = $"grafana.{domain}";
        var jaegerDomain  = $"jaeger.{domain}";
        var vaultDomain   = $"vault.{domain}";

        var baseOpts = new CustomResourceOptions { Parent = this };

        // ── cert-manager ──────────────────────────────────────────────────────────
        // Gère le cycle de vie des certificats TLS : création + renouvellement automatique J-30.
        // installCRDs: true — les CRDs cert-manager sont incluses dans le chart.
        // ⚠️  Le webhook cert-manager doit être Ready avant que le ClusterIssuer soit créé ;
        //     Pulumi attend la complétion du Helm release (pods Ready) avant de continuer.
        var certManager = new Release("cert-manager", new ReleaseArgs
        {
            Chart           = "cert-manager",
            Version         = args.CertManagerVersion,
            Namespace       = "cert-manager",
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs { Repo = "https://charts.jetstack.io" },
            WaitForJobs     = true,
            Values          = new InputMap<object>
            {
                ["installCRDs"] = true,
                ["resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new Dictionary<string, object> { ["cpu"] = "10m",  ["memory"] = "32Mi"  },
                    ["limits"]   = new Dictionary<string, object> { ["cpu"] = "100m", ["memory"] = "128Mi" }
                }
            }
        }, baseOpts);

        // ── nginx-ingress-controller ──────────────────────────────────────────────
        // Prod : Service LoadBalancer → IP publique (cloud), 2 réplicas (HA).
        // Local/Kind (NginxHostPort) : le contrôleur bind 80/443 sur le nœud (+ kind
        // extraPortMappings) → localhost. force-ssl-redirect : HTTP → HTTPS.
        var nginxController = new Dictionary<string, object>
        {
            // hostPort : un seul pod peut binder 80/443 sur un nœud → replicaCount=1.
            ["replicaCount"] = args.NginxHostPort ? 1 : 2,
            ["resources"] = new Dictionary<string, object>
            {
                ["requests"] = new Dictionary<string, object> { ["cpu"] = "100m", ["memory"] = "90Mi"  },
                ["limits"]   = new Dictionary<string, object> { ["cpu"] = "500m", ["memory"] = "256Mi" }
            },
            ["config"] = new Dictionary<string, object>
            {
                ["ssl-redirect"]          = "true",
                ["force-ssl-redirect"]    = "true",
                ["use-forwarded-headers"] = "true",
                ["proxy-body-size"]       = "8m"
            }
        };
        if (args.NginxHostPort)
        {
            // Kind : pas de LoadBalancer → exposition via hostPort + Service ClusterIP.
            nginxController["hostPort"] = new Dictionary<string, object> { ["enabled"] = true };
            nginxController["service"]  = new Dictionary<string, object> { ["type"] = "ClusterIP" };
        }

        var nginx = new Release("nginx-ingress", new ReleaseArgs
        {
            Chart           = "ingress-nginx",
            Version         = args.NginxVersion,
            Namespace       = "ingress",
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs { Repo = "https://kubernetes.github.io/ingress-nginx" },
            WaitForJobs     = true,
            Values          = new InputMap<object> { ["controller"] = nginxController }
        }, baseOpts);

        // ── ClusterIssuer ─────────────────────────────────────────────────────────
        // Prod : Let's Encrypt (ACME HTTP-01 — domaine PUBLIC + port 80 requis).
        // Local/Kind (SelfSigned) : émetteur auto-signé — ACME impossible sans DNS public.
        //   → TLS terminé par nginx avec un cert auto-signé (le navigateur avertit, normal).
        var issuerName = args.SelfSigned ? "selfsigned" : "letsencrypt-prod";
        var issuerSpec = args.SelfSigned
            ? "  selfSigned: {}"
            : $@"  acme:
    server: https://acme-v02.api.letsencrypt.org/directory
    email: {args.AcmeEmail}
    privateKeySecretRef:
      name: letsencrypt-prod-key
    solvers:
    - http01:
        ingress:
          class: nginx";

        // Appliqué via kubectl (Pulumi.Command), PAS via ConfigGroup : la CRD ClusterIssuer
        // (cert-manager.io/v1) est installée par le chart cert-manager pendant CE même
        // pulumi up → absente du cache GVK du provider Kubernetes (même contrainte que
        // CNPG/KEDA/ServiceMonitors). kubectl, lui, résout la CRD à l'exécution.
        var issuerYaml = $@"apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata:
  name: {issuerName}
spec:
{issuerSpec}";

        var issuer = new Command("cluster-issuer", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = issuerYaml
        }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { certManager } });

        // ── Secret basic-auth monitoring (OPTIONNEL) ──────────────────────────────
        // Protège Grafana/Jaeger au niveau nginx. Créé SEULEMENT si un htpasswd est fourni
        // (ingress:monitoringBasicAuthHtpasswd). Vide (dev local) → pas de basic-auth →
        // Grafana/Jaeger accessibles directement (sinon nginx exigerait un mot de passe
        // inexistant → 401). En prod : poser le htpasswd → la protection s'active.
        var basicAuthEnabled = !string.IsNullOrEmpty(args.MonitoringBasicAuthHtpasswd);
        Secret? monitoringAuth = null;
        if (basicAuthEnabled)
            monitoringAuth = new Secret("monitoring-basic-auth", new SecretArgs
            {
                Metadata   = new ObjectMetaArgs { Namespace = args.MonitoringNamespace, Name = "monitoring-basic-auth" },
                Type       = "Opaque",
                StringData = new InputMap<string> { ["auth"] = args.MonitoringBasicAuthHtpasswd }
            }, baseOpts);

        // Annotations communes Grafana/Jaeger — basic-auth ajoutée uniquement si activée.
        InputMap<string> MonitoringAnnotations()
        {
            var a = new Dictionary<string, string>
            {
                ["cert-manager.io/cluster-issuer"]           = issuerName,
                ["nginx.ingress.kubernetes.io/ssl-redirect"] = "true"
            };
            if (basicAuthEnabled)
            {
                a["nginx.ingress.kubernetes.io/auth-type"]   = "basic";
                a["nginx.ingress.kubernetes.io/auth-secret"] = "monitoring-basic-auth";
                a["nginx.ingress.kubernetes.io/auth-realm"]  = "Monitoring";
            }
            return a;
        }

        // DependsOn des Ingress monitoring (inclut le secret basic-auth s'il existe).
        var monitoringDeps = monitoringAuth != null
            ? new Resource[] { issuer, nginx, monitoringAuth }
            : new Resource[] { issuer, nginx };

        // ── Ingress : gateway (API publique — wizzz.com) ──────────────────────────
        // Pas de basic-auth : l'API est publique (authentification gérée par l'application).
        _ = new Ingress("gateway-ingress", new IngressArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace   = args.EcommerceNamespace,
                Name        = "gateway",
                Annotations = new InputMap<string>
                {
                    ["cert-manager.io/cluster-issuer"]           = issuerName,
                    ["nginx.ingress.kubernetes.io/ssl-redirect"] = "true",
                    ["nginx.ingress.kubernetes.io/use-regex"]    = "true"
                }
            },
            Spec = new IngressSpecArgs
            {
                IngressClassName = "nginx",
                Tls = new IngressTLSArgs
                {
                    Hosts      = new InputList<string> { domain },
                    SecretName = "tls-gateway"
                },
                Rules = new IngressRuleArgs
                {
                    Host = domain,
                    Http = new HTTPIngressRuleValueArgs
                    {
                        Paths = new HTTPIngressPathArgs
                        {
                            Path     = "/",
                            PathType = "Prefix",
                            Backend  = new IngressBackendArgs
                            {
                                Service = new IngressServiceBackendArgs
                                {
                                    Name = "gateway",
                                    Port = new ServiceBackendPortArgs { Number = 8080 }
                                }
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { issuer, nginx } });

        // ── Ingress : Grafana (monitoring — grafana.wizzz.com) ────────────────────
        // Double protection : basic-auth nginx (externe) + login Grafana natif (interne).
        _ = new Ingress("grafana-ingress", new IngressArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace   = args.MonitoringNamespace,
                Name        = "grafana",
                Annotations = MonitoringAnnotations()
            },
            Spec = new IngressSpecArgs
            {
                IngressClassName = "nginx",
                Tls = new IngressTLSArgs
                {
                    Hosts      = new InputList<string> { grafanaDomain },
                    SecretName = "tls-grafana"
                },
                Rules = new IngressRuleArgs
                {
                    Host = grafanaDomain,
                    Http = new HTTPIngressRuleValueArgs
                    {
                        Paths = new HTTPIngressPathArgs
                        {
                            Path     = "/",
                            PathType = "Prefix",
                            Backend  = new IngressBackendArgs
                            {
                                Service = new IngressServiceBackendArgs
                                {
                                    // Service réel du chart kube-prometheus-stack (≠ "grafana"), port 80.
                                    Name = "kube-prometheus-stack-grafana",
                                    Port = new ServiceBackendPortArgs { Number = 80 }
                                }
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = monitoringDeps });

        // ── Ingress : Jaeger (tracing — jaeger.wizzz.com) ────────────────────────
        _ = new Ingress("jaeger-ingress", new IngressArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace   = args.MonitoringNamespace,
                Name        = "jaeger",
                Annotations = MonitoringAnnotations()
            },
            Spec = new IngressSpecArgs
            {
                IngressClassName = "nginx",
                Tls = new IngressTLSArgs
                {
                    Hosts      = new InputList<string> { jaegerDomain },
                    SecretName = "tls-jaeger"
                },
                Rules = new IngressRuleArgs
                {
                    Host = jaegerDomain,
                    Http = new HTTPIngressRuleValueArgs
                    {
                        Paths = new HTTPIngressPathArgs
                        {
                            Path     = "/",
                            PathType = "Prefix",
                            Backend  = new IngressBackendArgs
                            {
                                Service = new IngressServiceBackendArgs
                                {
                                    Name = "jaeger",
                                    Port = new ServiceBackendPortArgs { Number = 16686 }
                                }
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = monitoringDeps });

        // ── Ingress : Vault (vault.wizzz.com) ─────────────────────────────────────
        // Requis en prod pour que le provider pulumi-vault (configMode=provider) joigne
        // Vault depuis l'hôte. PAS de basic-auth nginx : Vault s'authentifie par TOKEN
        // (le provider envoie un token Vault — une couche basic-auth casserait l'API).
        // TLS terminé par nginx ; backend en HTTP (listener Vault tls_disable=1 en interne).
        // Créé seulement si Vault est déployé.
        if (args.VaultEnabled)
        {
            _ = new Ingress("vault-ingress", new IngressArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Namespace   = args.VaultNamespace,
                    Name        = "vault",
                    Annotations = new InputMap<string>
                    {
                        ["cert-manager.io/cluster-issuer"]               = issuerName,
                        ["nginx.ingress.kubernetes.io/ssl-redirect"]     = "true",
                        ["nginx.ingress.kubernetes.io/backend-protocol"] = "HTTP"
                    }
                },
                Spec = new IngressSpecArgs
                {
                    IngressClassName = "nginx",
                    Tls = new IngressTLSArgs
                    {
                        Hosts      = new InputList<string> { vaultDomain },
                        SecretName = "tls-vault"
                    },
                    Rules = new IngressRuleArgs
                    {
                        Host = vaultDomain,
                        Http = new HTTPIngressRuleValueArgs
                        {
                            Paths = new HTTPIngressPathArgs
                            {
                                Path     = "/",
                                PathType = "Prefix",
                                Backend  = new IngressBackendArgs
                                {
                                    Service = new IngressServiceBackendArgs
                                    {
                                        Name = "vault",
                                        Port = new ServiceBackendPortArgs { Number = 8200 }
                                    }
                                }
                            }
                        }
                    }
                }
            }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { issuer, nginx } });
        }

        RegisterOutputs();
    }
}
