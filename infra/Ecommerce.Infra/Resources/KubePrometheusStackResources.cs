using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class KubePrometheusStackResourcesArgs
{
    public Input<string> Namespace { get; set; } = "monitoring";

    /// <summary>Version du chart prometheus-community/kube-prometheus-stack.</summary>
    public string Version { get; set; } = "86.1.0";

    /// <summary>NodePort Grafana (dev). 0 = ClusterIP (prod, derrière ingress).</summary>
    public int GrafanaNodePort { get; set; } = 30030;

    /// <summary>Quand true : Grafana en ClusterIP (ingress gère l'accès). False : NodePort dev.</summary>
    public bool IngressEnabled { get; set; } = false;

    /// <summary>Mot de passe admin Grafana (prod). Vide en dev → admin/prom-operator par défaut.</summary>
    public string GrafanaAdminPassword { get; set; } = "";

    /// <summary>Endpoint Jaeger (datasource tracing ajoutée à Grafana).</summary>
    public string JaegerUrl { get; set; } = "http://jaeger.monitoring.svc.cluster.local:16686";

    /// <summary>
    /// Active Alertmanager + le routage des alertes par sévérité. False (dev par défaut) =
    /// Alertmanager désactivé, aucune notification (les PrometheusRule restent visibles dans
    /// l'UI Prometheus si elles sont déployées).
    /// </summary>
    public bool AlertingEnabled { get; set; } = false;

    /// <summary>
    /// Webhook Slack (secret) pour les notifications. Vide → récepteurs « null » : Alertmanager
    /// tourne mais n'envoie rien vers l'extérieur (dev / validation locale).
    /// </summary>
    public string AlertmanagerSlackWebhook { get; set; } = "";

    /// <summary>Canal Slack cible des notifications.</summary>
    public string AlertmanagerSlackChannel { get; set; } = "#alerts";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  kube-prometheus-stack — LE chart de référence pour l'observabilité métriques.
///
///  REMPLACE (versions manuelles dans ObservabilityResources.cs) :
///    - Prometheus (Deployment + ConfigMap scrape statique)  → Prometheus OPERATOR
///    - Grafana    (Deployment + 3 ConfigMaps provisioning)  → Grafana du chart
///    - node-exporter (DaemonSet + RBAC)                     → inclus
///    - kube-state-metrics (Deployment + RBAC)               → inclus
///
///  CONSERVE séparément (pas dans ce chart) :
///    - OTel Collector (pipeline traces/métriques apps)      → ObservabilityResources
///    - Jaeger (tracing)                                     → ObservabilityResources
///      (ajouté ici comme DATASOURCE Grafana uniquement)
///
///  CHANGEMENT DE PARADIGME — le scrape :
///    Avant : un ConfigMap central 'prometheus-config' que TU édites + reload manuel.
///    Après : chaque composant déclare un ServiceMonitor (CRD) → l'Operator détecte
///            et recharge Prometheus AUTOMATIQUEMENT. Voir ServiceMonitorResources.cs.
///
///  serviceMonitorSelectorNilUsesHelmValues=false : CRUCIAL. Par défaut, l'Operator
///  ne sélectionne QUE les ServiceMonitors portant le label 'release'. On le désactive
///  pour qu'il découvre TOUS les ServiceMonitors du cluster (les nôtres inclus).
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class KubePrometheusStackResources : ComponentResource
{
    public KubePrometheusStackResources(string name, KubePrometheusStackResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:KubePrometheusStackResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // Grafana : NodePort en dev (accès localhost), ClusterIP en prod (ingress).
        var grafanaService = args.IngressEnabled
            ? new Dictionary<string, object> { ["type"] = "ClusterIP" }
            : new Dictionary<string, object>
              {
                  ["type"]     = "NodePort",
                  ["nodePort"] = args.GrafanaNodePort
              };

        var grafanaValues = new Dictionary<string, object>
        {
            ["service"] = grafanaService,
            // Dashboards : le sidecar charge tout ConfigMap labellisé grafana_dashboard=1.
            // Nos 6 dashboards (ServiceMonitorResources / dashboards ConfigMap) sont
            // ainsi découverts automatiquement, sans provisioning manuel.
            ["sidecar"] = new Dictionary<string, object>
            {
                ["dashboards"] = new Dictionary<string, object>
                {
                    ["enabled"]         = true,
                    ["label"]           = "grafana_dashboard",
                    ["searchNamespace"] = "ALL"   // cherche les dashboards dans tous les namespaces
                },
                // Datasource Prometheus auto-créée avec uid="prometheus" (défaut du chart,
                // explicité pour robustesse). CRUCIAL : nos 6 dashboards référencent
                // "uid": "prometheus" EN DUR → cet uid doit correspondre, sinon "no data".
                ["datasources"] = new Dictionary<string, object>
                {
                    ["defaultDatasourceEnabled"] = true,
                    ["isDefaultDatasource"]      = true,
                    ["uid"]                      = "prometheus"
                }
            },
            // Datasource Jaeger ajoutée (Prometheus est auto-configuré par le chart).
            ["additionalDataSources"] = new List<Dictionary<string, object>>
            {
                new()
                {
                    ["name"]   = "Jaeger",
                    ["type"]   = "jaeger",
                    ["uid"]    = "jaeger",
                    ["url"]    = args.JaegerUrl,
                    ["access"] = "proxy"
                }
            }
        };

        // Auth Grafana : mot de passe admin en prod, défaut en dev.
        if (!string.IsNullOrEmpty(args.GrafanaAdminPassword))
            grafanaValues["adminPassword"] = args.GrafanaAdminPassword;

        _ = new Release("kube-prometheus-stack", new ReleaseArgs
        {
            Name            = "kube-prometheus-stack",
            Chart           = "kube-prometheus-stack",
            Version         = args.Version,
            Namespace       = args.Namespace,
            CreateNamespace = false,
            RepositoryOpts  = new RepositoryOptsArgs
            {
                Repo = "https://prometheus-community.github.io/helm-charts"
            },
            // Le chart installe des CRDs (ServiceMonitor, PrometheusRule...) + attend
            // que l'Operator soit prêt. Timeout large : beaucoup de ressources.
            Timeout = 600,
            Values  = new InputMap<object>
            {
                // ── Prometheus Operator core ──────────────────────────────────
                ["prometheus"] = new Dictionary<string, object>
                {
                    ["prometheusSpec"] = new Dictionary<string, object>
                    {
                        // CRUCIAL : découvre TOUS les ServiceMonitors, pas seulement
                        // ceux portant le label de cette release Helm.
                        ["serviceMonitorSelectorNilUsesHelmValues"] = false,
                        ["podMonitorSelectorNilUsesHelmValues"]     = false,
                        ["ruleSelectorNilUsesHelmValues"]           = false,
                        // Rétention alignée sur l'ancienne config.
                        ["retention"] = "7d",
                        // Ressources conservatrices (Kind). Ajuster en prod.
                        ["resources"] = new Dictionary<string, object>
                        {
                            ["requests"] = new Dictionary<string, object> { ["cpu"] = "100m", ["memory"] = "512Mi" },
                            ["limits"]   = new Dictionary<string, object> { ["cpu"] = "1000m", ["memory"] = "1Gi" }
                        }
                    }
                },

                // ── Grafana (fourni par le chart) ─────────────────────────────
                ["grafana"] = grafanaValues,

                // ── Alertmanager : activé en prod (récepteur Slack), off en dev ─
                // Voir BuildAlertmanagerValues : routage par sévérité (critical/warning).
                ["alertmanager"] = BuildAlertmanagerValues(args),

                // ── node-exporter + kube-state-metrics : inclus, activés ───────
                // (remplacent les versions manuelles d'ObservabilityResources).
                ["nodeExporter"]       = new Dictionary<string, object> { ["enabled"] = true },
                ["kubeStateMetrics"]   = new Dictionary<string, object> { ["enabled"] = true },

                // ── Composants control-plane : désactivés sur Kind ─────────────
                // Kind ne les expose pas (kube-scheduler, controller-manager, etcd,
                // kube-proxy) → les scrapes échoueraient. On les coupe pour éviter
                // des targets "down" rouges en permanence.
                ["kubeScheduler"]         = new Dictionary<string, object> { ["enabled"] = false },
                ["kubeControllerManager"] = new Dictionary<string, object> { ["enabled"] = false },
                ["kubeEtcd"]              = new Dictionary<string, object> { ["enabled"] = false },
                ["kubeProxy"]             = new Dictionary<string, object> { ["enabled"] = false }
            }
        }, baseOpts);

        RegisterOutputs();
    }

    /// <summary>
    /// Construit les values Helm de la section <c>alertmanager</c>.
    ///   - Alerting OFF (dev)  : <c>{ enabled: false }</c> — aucun Alertmanager, aucune notif.
    ///   - Alerting ON         : Alertmanager + config routée par <c>severity</c>
    ///                           (critical / warning → récepteur Slack).
    ///   - Sans webhook Slack  : récepteurs « null » (Alertmanager tourne, n'envoie rien
    ///                           dehors) → permet de valider les alertes en local sans fuite.
    ///
    /// Le webhook provient d'un secret Pulumi (jamais committé en clair) ; il n'apparaît que
    /// dans le Secret Alertmanager in-cluster, comme tout récepteur.
    /// </summary>
    private static Dictionary<string, object> BuildAlertmanagerValues(KubePrometheusStackResourcesArgs args)
    {
        if (!args.AlertingEnabled)
            return new Dictionary<string, object> { ["enabled"] = false };

        var criticalReceiver = new Dictionary<string, object> { ["name"] = "critical" };
        var warningReceiver  = new Dictionary<string, object> { ["name"] = "warning" };

        if (!string.IsNullOrEmpty(args.AlertmanagerSlackWebhook))
        {
            Dictionary<string, object> Slack() => new()
            {
                ["api_url"]       = args.AlertmanagerSlackWebhook,
                ["channel"]       = args.AlertmanagerSlackChannel,
                ["send_resolved"] = true,
                ["title"]         = "[{{ .CommonLabels.severity | toUpper }}] {{ .CommonLabels.alertname }}",
                ["text"]          = "{{ range .Alerts }}*{{ .Annotations.summary }}*\n{{ .Annotations.description }}\n{{ end }}"
            };
            criticalReceiver["slack_configs"] = new List<Dictionary<string, object>> { Slack() };
            warningReceiver["slack_configs"]  = new List<Dictionary<string, object>> { Slack() };
        }

        return new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["config"]  = new Dictionary<string, object>
            {
                ["global"] = new Dictionary<string, object> { ["resolve_timeout"] = "5m" },
                ["route"]  = new Dictionary<string, object>
                {
                    ["group_by"]        = new List<string> { "alertname", "namespace" },
                    ["group_wait"]      = "30s",
                    ["group_interval"]  = "5m",
                    ["repeat_interval"] = "4h",
                    ["receiver"]        = "default",
                    ["routes"] = new List<Dictionary<string, object>>
                    {
                        new() { ["matchers"] = new List<string> { "severity = \"critical\"" }, ["receiver"] = "critical" },
                        new() { ["matchers"] = new List<string> { "severity = \"warning\"" },  ["receiver"] = "warning"  }
                    }
                },
                ["receivers"] = new List<Dictionary<string, object>>
                {
                    new() { ["name"] = "default" },   // catch-all silencieux (alertes sans severity routée)
                    criticalReceiver,
                    warningReceiver
                }
            }
        };
    }
}
