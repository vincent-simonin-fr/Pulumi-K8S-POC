using Pulumi;
using Pulumi.Command.Local;

namespace Ecommerce.Infra.Resources;

public class ServiceMonitorResourcesArgs
{
    /// <summary>Namespace où vit Prometheus (les ServiceMonitors peuvent être ailleurs).</summary>
    public string MonitoringNamespace { get; set; } = "monitoring";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  ServiceMonitors — la traduction des anciens scrape_configs statiques vers le
///  modèle déclaratif du Prometheus Operator.
///
///  AVANT (ObservabilityResources, ConfigMap prometheus-config) :
///    scrape_configs:
///      - job_name: cnpg-order
///        static_configs: [targets: ['order-db-metrics...:9187']]
///        metric_relabel_configs: [target_label: cluster, replacement: order-db]
///    → édition manuelle d'un fichier central + reload Prometheus
///
///  APRÈS (ce fichier) :
///    Un ServiceMonitor par cible. L'Operator les découvre (serviceMonitorSelector
///    NilUsesHelmValues=false) et génère la config Prometheus automatiquement.
///
///  Appliqués via kubectl (Pulumi.Command) car la CRD ServiceMonitor
///  (monitoring.coreos.com/v1) est installée par kube-prometheus-stack pendant ce
///  même pulumi up → absente du cache GVK du provider (même contrainte que CNPG/KEDA).
///
///  Sélecteur : chaque ServiceMonitor cible un Service par son label + son port nommé.
///  ⚠️ Les Services doivent avoir un port NOMMÉ (endpoints.port = nom, pas numéro).
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class ServiceMonitorResources : ComponentResource
{
    public ServiceMonitorResources(string name, ServiceMonitorResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:ServiceMonitorResources", name, opts)
    {
        // YAML multi-document : tous les ServiceMonitors en un seul kubectl apply.
        //
        // Conventions Operator :
        //   - selector.matchLabels : cible le Service par ses labels
        //   - endpoints.port       : NOM du port du Service (pas le numéro)
        //   - namespaceSelector    : où chercher les Services (cross-namespace)
        //   - relabelings / metricRelabelings : équivalent des *_relabel_configs
        // Labels/ports VÉRIFIÉS sur le cluster réel (voir validation) :
        //   otel-collector       label monitoring=ecommerce      port prom-metrics
        //   postgres-exporter-*  label monitoring=ecommerce      port metrics
        //   {cluster}-metrics    label cnpg.io/cluster=<cluster> port metrics
        //   rabbitmq-metrics     label monitoring=ecommerce      port prometheus
        //   argocd-*-metrics     label app.kubernetes.io/part-of=argocd  port http-metrics
        var yaml = @"
# ── OTel Collector (métriques applicatives exportées en Prometheus) ──────────
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: otel-collector
  namespace: monitoring
spec:
  namespaceSelector:
    matchNames: [monitoring]
  selector:
    matchLabels:
      monitoring: ecommerce
  endpoints:
    - port: prom-metrics
---
# ── postgres-exporters (order + inventory en un seul SM via label commun) ────
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: postgres-exporters
  namespace: monitoring
spec:
  namespaceSelector:
    matchNames: [ecommerce]
  selector:
    matchLabels:
      monitoring: ecommerce
  # matchExpressions affine : seulement les services postgres-exporter (port metrics 9187).
  # (otel a aussi monitoring=ecommerce mais est dans monitoring, pas ecommerce → exclu
  #  par le namespaceSelector. Les 2 postgres + les 2 cnpg-metrics sont dans ecommerce.)
  endpoints:
    - port: metrics
---
# ── CNPG order-db (relabel 'cluster=order-db') ───────────────────────────────
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: cnpg-order
  namespace: monitoring
spec:
  namespaceSelector:
    matchNames: [ecommerce]
  selector:
    matchLabels:
      cnpg.io/cluster: order-db
  endpoints:
    - port: metrics
      metricRelabelings:
        - targetLabel: cluster
          replacement: order-db
---
# ── CNPG inventory-db (relabel 'cluster=inventory-db') ───────────────────────
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: cnpg-inventory
  namespace: monitoring
spec:
  namespaceSelector:
    matchNames: [ecommerce]
  selector:
    matchLabels:
      cnpg.io/cluster: inventory-db
  endpoints:
    - port: metrics
      metricRelabelings:
        - targetLabel: cluster
          replacement: inventory-db
---
# ── RabbitMQ (3 nœuds — remplace le dns_sd headless ; scrape les 3 endpoints) ─
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: rabbitmq
  namespace: monitoring
spec:
  namespaceSelector:
    matchNames: [ecommerce]
  selector:
    matchLabels:
      monitoring: ecommerce
  endpoints:
    - port: prometheus
---
# ── ArgoCD (4 composants : label part-of=argocd, port http-metrics commun) ───
apiVersion: monitoring.coreos.com/v1
kind: ServiceMonitor
metadata:
  name: argocd
  namespace: monitoring
spec:
  namespaceSelector:
    matchNames: [argocd]
  selector:
    matchLabels:
      app.kubernetes.io/part-of: argocd
  endpoints:
    - port: http-metrics
";

        _ = new Command("service-monitors-apply", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = yaml
        }, new CustomResourceOptions { Parent = this });

        RegisterOutputs();
    }
}
