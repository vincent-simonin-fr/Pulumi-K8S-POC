using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Networking.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Networking.V1;
using Pulumi.Kubernetes.Types.Inputs.Yaml.V2;
using Pulumi.Kubernetes.Yaml.V2;

namespace Ecommerce.Infra.Resources;

public class IngressResourcesArgs
{
    /// <summary>
    /// Domaine racine — wizzz.com donne :
    ///   wizzz.com          → gateway (API publique)
    ///   grafana.wizzz.com  → Grafana  (basic-auth)
    ///   jaeger.wizzz.com   → Jaeger   (basic-auth)
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
}

public class IngressResources : ComponentResource
{
    public IngressResources(string name, IngressResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:IngressResources", name, opts)
    {
        var domain        = args.Domain;
        var grafanaDomain = $"grafana.{domain}";
        var jaegerDomain  = $"jaeger.{domain}";

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
        // Service type LoadBalancer → IP publique provisionnée par le cloud provider.
        // 2 réplicas pour la haute disponibilité.
        // force-ssl-redirect : toute requête HTTP est redirigée vers HTTPS.
        var nginx = new Release("nginx-ingress", new ReleaseArgs
        {
            Chart           = "ingress-nginx",
            Version         = args.NginxVersion,
            Namespace       = "ingress",
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs { Repo = "https://kubernetes.github.io/ingress-nginx" },
            WaitForJobs     = true,
            Values          = new InputMap<object>
            {
                ["controller"] = new Dictionary<string, object>
                {
                    ["replicaCount"] = 2,
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
                }
            }
        }, baseOpts);

        // ── ClusterIssuer Let's Encrypt ───────────────────────────────────────────
        // HTTP-01 challenge : nginx-ingress expose temporairement
        //   /.well-known/acme-challenge/<token>
        // Let's Encrypt appelle ce endpoint depuis Internet pour valider la propriété du domaine.
        // ⚠️  Le domaine doit être résolvable publiquement et pointer vers l'IP du LoadBalancer.
        var issuer = new ConfigGroup("letsencrypt-issuer", new ConfigGroupArgs
        {
            // V2 : Yaml est un Input<string> (document unique, pas une liste)
            Yaml = $@"apiVersion: cert-manager.io/v1
                        kind: ClusterIssuer
                        metadata:
                          name: letsencrypt-prod
                        spec:
                          acme:
                            server: https://acme-v02.api.letsencrypt.org/directory
                            email: {args.AcmeEmail}
                            privateKeySecretRef:
                              name: letsencrypt-prod-key
                            solvers:
                            - http01:
                                ingress:
                                  class: nginx"
        }, new ComponentResourceOptions { Parent = this, DependsOn = new Resource[] { certManager } });

        // ── Secret basic-auth monitoring ──────────────────────────────────────────
        // Protège Grafana et Jaeger au niveau nginx (couche externe).
        // Le Secret doit être dans le même namespace que les Ingress qui l'utilisent.
        var monitoringAuth = new Secret("monitoring-basic-auth", new SecretArgs
        {
            Metadata   = new ObjectMetaArgs { Namespace = args.MonitoringNamespace, Name = "monitoring-basic-auth" },
            Type       = "Opaque",
            StringData = new InputMap<string> { ["auth"] = args.MonitoringBasicAuthHtpasswd }
        }, baseOpts);

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
                    ["cert-manager.io/cluster-issuer"]           = "letsencrypt-prod",
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
                Annotations = new InputMap<string>
                {
                    ["cert-manager.io/cluster-issuer"]              = "letsencrypt-prod",
                    ["nginx.ingress.kubernetes.io/ssl-redirect"]    = "true",
                    ["nginx.ingress.kubernetes.io/auth-type"]       = "basic",
                    ["nginx.ingress.kubernetes.io/auth-secret"]     = "monitoring-basic-auth",
                    ["nginx.ingress.kubernetes.io/auth-realm"]      = "Monitoring"
                }
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
                                    Name = "grafana",
                                    Port = new ServiceBackendPortArgs { Number = 3000 }
                                }
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { issuer, nginx, monitoringAuth } });

        // ── Ingress : Jaeger (tracing — jaeger.wizzz.com) ────────────────────────
        _ = new Ingress("jaeger-ingress", new IngressArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace   = args.MonitoringNamespace,
                Name        = "jaeger",
                Annotations = new InputMap<string>
                {
                    ["cert-manager.io/cluster-issuer"]              = "letsencrypt-prod",
                    ["nginx.ingress.kubernetes.io/ssl-redirect"]    = "true",
                    ["nginx.ingress.kubernetes.io/auth-type"]       = "basic",
                    ["nginx.ingress.kubernetes.io/auth-secret"]     = "monitoring-basic-auth",
                    ["nginx.ingress.kubernetes.io/auth-realm"]      = "Monitoring"
                }
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
        }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { issuer, nginx, monitoringAuth } });

        RegisterOutputs();
    }
}
