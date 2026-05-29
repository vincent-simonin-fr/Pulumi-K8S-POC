using System.IO;
using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Rbac.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Rbac.V1;
using DaemonSet = Pulumi.Kubernetes.Apps.V1.DaemonSet;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class ObservabilityResourcesArgs
{
    public string Namespace { get; set; } = "monitoring";

    // Versions des images — configurables via Pulumi.dev.yaml
    public string OtelCollectorVersion { get; set; } = "0.153.0";
    public string JaegerVersion        { get; set; } = "1.76.0";
    public string PrometheusVersion    { get; set; } = "v3.11.3";
    public string GrafanaVersion       { get; set; } = "13.0.1-security-01";

    // NodePorts exposés sur l'hôte via Kind extraPortMappings (ignorés si IngressEnabled)
    public int GrafanaNodePort  { get; set; } = 30030;
    public int JaegerUiNodePort { get; set; } = 30686;

    /// <summary>
    /// Quand true : services Grafana et Jaeger en ClusterIP (nginx-ingress gère l'accès externe).
    /// Quand false : NodePort — accès direct via localhost (dev Kind).
    /// </summary>
    public bool IngressEnabled { get; set; } = false;

    /// <summary>
    /// Mot de passe admin Grafana — utilisé uniquement quand IngressEnabled = true.
    /// En dev (IngressEnabled = false) l'auth anonyme est activée, ce champ est ignoré.
    /// Stocker : pulumi config set --secret observability:grafanaAdminPassword &lt;password&gt;
    /// </summary>
    public string GrafanaAdminPassword { get; set; } = "";
}

public class ObservabilityResources : ComponentResource
{
    /// <summary>Endpoint OTLP gRPC du collecteur — à passer aux APIs via env var.</summary>
    public Output<string> OtelCollectorEndpoint { get; }

    public ObservabilityResources(string name, ObservabilityResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:ObservabilityResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Namespace monitoring ─────────────────────────────────────────────────
        var ns = new Namespace("monitoring-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = args.Namespace }
        }, resourceOpts);

        // Toutes les ressources ci-dessous dépendent du namespace
        var nsDep = new CustomResourceOptions { Parent = this, DependsOn = ns };

        // ── OpenTelemetry Collector ──────────────────────────────────────────────
        // Reçoit OTLP (gRPC :4317, HTTP :4318) depuis les APIs.
        // Route : traces → Jaeger (OTLP gRPC), métriques → endpoint Prometheus (:8889).
        var otelConfigMap = new ConfigMap("otel-collector-config", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "otel-collector-config" },
            Data = new InputMap<string>
            {
                ["config.yaml"] =
@"extensions:
  health_check:
    endpoint: 0.0.0.0:13133

receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  batch:
    timeout: 5s

exporters:
  otlp/jaeger:
    endpoint: jaeger.monitoring.svc.cluster.local:4317
    tls:
      insecure: true
  prometheus:
    endpoint: 0.0.0.0:8889
    namespace: ecommerce
    resource_to_telemetry_conversion:
      enabled: true

service:
  extensions: [health_check]
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [otlp/jaeger]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [prometheus]
"
            }
        }, nsDep);

        _ = new Deployment("otel-collector-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "otel-collector" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "otel-collector" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "otel-collector" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "otel-collector",
                            Image           = $"otel/opentelemetry-collector-contrib:{args.OtelCollectorVersion}",
                            ImagePullPolicy = "IfNotPresent",
                            Args            = new[] { "--config=/etc/otel/config.yaml" },
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { Name = "otlp-grpc",    ContainerPortValue = 4317  },
                                new() { Name = "otlp-http",    ContainerPortValue = 4318  },
                                new() { Name = "prom-metrics", ContainerPortValue = 8889  },
                                new() { Name = "health",       ContainerPortValue = 13133 }
                            },
                            VolumeMounts = new VolumeMountArgs
                            {
                                Name      = "config",
                                MountPath = "/etc/otel"
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "64Mi"  },
                                Limits   = new InputMap<string> { ["cpu"] = "200m", ["memory"] = "256Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/", Port = 13133 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        },
                        Volumes = new VolumeArgs
                        {
                            Name      = "config",
                            ConfigMap = new ConfigMapVolumeSourceArgs { Name = "otel-collector-config" }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = otelConfigMap });

        _ = new Service("otel-collector-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "otel-collector" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "otel-collector" },
                Ports = new List<ServicePortArgs>
                {
                    new() { Name = "otlp-grpc",    Port = 4317,  TargetPort = 4317  },
                    new() { Name = "otlp-http",    Port = 4318,  TargetPort = 4318  },
                    new() { Name = "prom-metrics", Port = 8889,  TargetPort = 8889  }
                }
            }
        }, nsDep);

        // ── Jaeger all-in-one ────────────────────────────────────────────────────
        // Stockage en mémoire (adapté au dev local).
        // Port 4317 : reçoit les traces OTLP depuis le collecteur.
        // Port 16686 : UI web (NodePort 30686 → accessible sur http://localhost:30686).
        _ = new Deployment("jaeger-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "jaeger" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "jaeger" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "jaeger" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "jaeger",
                            Image           = $"jaegertracing/all-in-one:{args.JaegerVersion}",
                            ImagePullPolicy = "IfNotPresent",
                            Env = new List<EnvVarArgs>
                            {
                                // Active le récepteur OTLP natif de Jaeger
                                new() { Name = "COLLECTOR_OTLP_ENABLED", Value = "true" },
                                // Limite les traces conservées en mémoire
                                new() { Name = "MEMORY_MAX_TRACES",      Value = "50000" }
                            },
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { Name = "otlp-grpc", ContainerPortValue = 4317  },
                                new() { Name = "ui",        ContainerPortValue = 16686 }
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "128Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "500m", ["memory"] = "512Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/", Port = 16686 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        }
                    }
                }
            }
        }, nsDep);

        // Service Jaeger : port 4317 (OTLP interne, toujours ClusterIP).
        // En dev : UI exposée en NodePort. En prod : UI exposée via nginx-ingress (ClusterIP).
        _ = new Service("jaeger-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "jaeger" },
            Spec = args.IngressEnabled
                ? new ServiceSpecArgs
                  {
                      Type     = "ClusterIP",
                      Selector = new InputMap<string> { ["app"] = "jaeger" },
                      Ports = new List<ServicePortArgs>
                      {
                          new() { Name = "otlp-grpc", Port = 4317,  TargetPort = 4317  },
                          new() { Name = "ui",        Port = 16686, TargetPort = 16686 }
                      }
                  }
                : new ServiceSpecArgs
                  {
                      Type     = "NodePort",
                      Selector = new InputMap<string> { ["app"] = "jaeger" },
                      Ports = new List<ServicePortArgs>
                      {
                          new() { Name = "otlp-grpc", Port = 4317,  TargetPort = 4317  },
                          new() { Name = "ui",        Port = 16686, TargetPort = 16686, NodePort = args.JaegerUiNodePort }
                      }
                  }
        }, nsDep);

        // ── Prometheus ───────────────────────────────────────────────────────────
        // Scrape l'endpoint prometheus du collecteur toutes les 15 secondes.
        // Rétention : 7 jours (adapté au dev local).
        var promConfigMap = new ConfigMap("prometheus-config", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "prometheus-config" },
            Data = new InputMap<string>
            {
                ["prometheus.yml"] =
@"global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: otel-collector
    static_configs:
      - targets: ['otel-collector.monitoring.svc.cluster.local:8889']
  - job_name: postgres-order
    static_configs:
      - targets: ['postgres-exporter-order.ecommerce.svc.cluster.local:9187']
  - job_name: postgres-inventory
    static_configs:
      - targets: ['postgres-exporter-inventory.ecommerce.svc.cluster.local:9187']
  - job_name: kube-state-metrics
    static_configs:
      - targets: ['kube-state-metrics.monitoring.svc.cluster.local:8080']
  - job_name: node-exporter
    static_configs:
      - targets: ['node-exporter.monitoring.svc.cluster.local:9100']
"
            }
        }, nsDep);

        _ = new Deployment("prometheus-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "prometheus" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "prometheus" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "prometheus" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "prometheus",
                            Image           = $"prom/prometheus:{args.PrometheusVersion}",
                            ImagePullPolicy = "IfNotPresent",
                            Args = new[]
                            {
                                "--config.file=/etc/prometheus/prometheus.yml",
                                "--storage.tsdb.retention.time=7d",
                                // Active le receiver remote write — utilisé par k6 pour pousser
                                // ses métriques de charge directement dans Prometheus.
                                // Les métriques k6 (k6_http_req_duration_*, k6_vus, ...)
                                // sont alors corrélables avec les métriques applicatives dans Grafana.
                                "--web.enable-remote-write-receiver"
                            },
                            Ports        = new ContainerPortArgs { Name = "http", ContainerPortValue = 9090 },
                            VolumeMounts = new VolumeMountArgs { Name = "config", MountPath = "/etc/prometheus" },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "128Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "200m", ["memory"] = "512Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/-/ready", Port = 9090 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        },
                        Volumes = new VolumeArgs
                        {
                            Name      = "config",
                            ConfigMap = new ConfigMapVolumeSourceArgs { Name = "prometheus-config" }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = promConfigMap });

        _ = new Service("prometheus-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "prometheus" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "prometheus" },
                Ports    = new ServicePortArgs { Name = "http", Port = 9090, TargetPort = 9090 }
            }
        }, nsDep);

        // ── Grafana ──────────────────────────────────────────────────────────────
        // Datasources provisionnées automatiquement via ConfigMap.
        // Auth anonyme activée (pas de login en dev local).
        // NodePort 30030 → accessible sur http://localhost:30030.
        var grafanaDatasourcesMap = new ConfigMap("grafana-datasources", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "grafana-datasources" },
            Data = new InputMap<string>
            {
                ["datasources.yaml"] =
@"apiVersion: 1
datasources:
  - name: Prometheus
    type: prometheus
    access: proxy
    url: http://prometheus.monitoring.svc.cluster.local:9090
    isDefault: true
    editable: false

  - name: Jaeger
    type: jaeger
    access: proxy
    url: http://jaeger.monitoring.svc.cluster.local:16686
    editable: false
"
            }
        }, nsDep);

        // ── Grafana dashboard provisioning ───────────────────────────────────────
        // Les fichiers JSON sont lus au moment de pulumi up (répertoire courant = infra/Ecommerce.Infra).
        // Les mêmes fichiers sont montés en volume dans docker-compose via bind mount.
        var dashboardProviderMap = new ConfigMap("grafana-dashboard-provider", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "grafana-dashboard-provider" },
            Data     = new InputMap<string>
            {
                ["provider.yaml"] = File.ReadAllText("../../docker/observability/dashboards/provider.yaml")
            }
        }, nsDep);

        var dashboardsMap = new ConfigMap("grafana-dashboards", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "grafana-dashboards" },
            Data     = new InputMap<string>
            {
                ["services.json"]   = File.ReadAllText("../../docker/observability/dashboards/services.json"),
                ["database.json"]   = File.ReadAllText("../../docker/observability/dashboards/database.json"),
                ["runtime.json"]    = File.ReadAllText("../../docker/observability/dashboards/runtime.json"),
                ["kubernetes.json"] = File.ReadAllText("../../docker/observability/dashboards/kubernetes.json")
            }
        }, nsDep);

        _ = new Deployment("grafana-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "grafana" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "grafana" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "grafana" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "grafana",
                            Image           = $"grafana/grafana:{args.GrafanaVersion}",
                            ImagePullPolicy = "IfNotPresent",
                            // Dev : auth anonyme (pas de login). Prod : login natif Grafana.
                            Env = args.IngressEnabled
                                ? new List<EnvVarArgs>
                                  {
                                      new() { Name = "GF_AUTH_ANONYMOUS_ENABLED",  Value = "false" },
                                      new() { Name = "GF_SECURITY_ADMIN_USER",     Value = "admin" },
                                      new() { Name = "GF_SECURITY_ADMIN_PASSWORD", Value = args.GrafanaAdminPassword }
                                  }
                                : new List<EnvVarArgs>
                                  {
                                      new() { Name = "GF_AUTH_ANONYMOUS_ENABLED",  Value = "true"  },
                                      new() { Name = "GF_AUTH_ANONYMOUS_ORG_ROLE", Value = "Admin" },
                                      new() { Name = "GF_AUTH_DISABLE_LOGIN_FORM", Value = "true"  }
                                  },
                            Ports        = new ContainerPortArgs { Name = "http", ContainerPortValue = 3000 },
                            VolumeMounts = new List<VolumeMountArgs>
                            {
                                new() { Name = "datasources",        MountPath = "/etc/grafana/provisioning/datasources" },
                                new() { Name = "dashboard-provider", MountPath = "/etc/grafana/provisioning/dashboards"  },
                                new() { Name = "dashboards",         MountPath = "/var/lib/grafana/dashboards"           }
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "128Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "200m", ["memory"] = "512Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/api/health", Port = 3000 },
                                InitialDelaySeconds = 10,
                                PeriodSeconds       = 5
                            }
                        },
                        Volumes = new List<VolumeArgs>
                        {
                            new() { Name = "datasources",        ConfigMap = new ConfigMapVolumeSourceArgs { Name = "grafana-datasources" }        },
                            new() { Name = "dashboard-provider", ConfigMap = new ConfigMapVolumeSourceArgs { Name = "grafana-dashboard-provider" }  },
                            new() { Name = "dashboards",         ConfigMap = new ConfigMapVolumeSourceArgs { Name = "grafana-dashboards" }          }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = new[] { grafanaDatasourcesMap, dashboardProviderMap, dashboardsMap } });

        _ = new Service("grafana-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "grafana" },
            Spec = args.IngressEnabled
                ? new ServiceSpecArgs
                  {
                      Type     = "ClusterIP",
                      Selector = new InputMap<string> { ["app"] = "grafana" },
                      Ports    = new ServicePortArgs { Name = "http", Port = 3000, TargetPort = 3000 }
                  }
                : new ServiceSpecArgs
                  {
                      Type     = "NodePort",
                      Selector = new InputMap<string> { ["app"] = "grafana" },
                      Ports    = new ServicePortArgs { Name = "http", Port = 3000, TargetPort = 3000, NodePort = args.GrafanaNodePort }
                  }
        }, nsDep);

        // ── kube-state-metrics ───────────────────────────────────────────────────
        // Expose l'état des objets K8s (pods, deployments, HPA, ...) en métriques Prometheus.
        // ClusterRole requis car les objets sont cluster-scoped.
        var ksmSa = new ServiceAccount("ksm-sa", new ServiceAccountArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "kube-state-metrics" }
        }, nsDep);

        var ksmCr = new ClusterRole("ksm-cr", new ClusterRoleArgs
        {
            Metadata = new ObjectMetaArgs { Name = "kube-state-metrics" },
            Rules    = new List<PolicyRuleArgs>
            {
                new()
                {
                    ApiGroups = new[] { "" },
                    Resources = new[] { "pods", "nodes", "services", "endpoints", "persistentvolumeclaims", "namespaces", "replicationcontrollers" },
                    Verbs     = new[] { "list", "watch" }
                },
                new()
                {
                    ApiGroups = new[] { "apps" },
                    Resources = new[] { "deployments", "replicasets", "statefulsets", "daemonsets" },
                    Verbs     = new[] { "list", "watch" }
                },
                new()
                {
                    ApiGroups = new[] { "autoscaling" },
                    Resources = new[] { "horizontalpodautoscalers" },
                    Verbs     = new[] { "list", "watch" }
                },
                new()
                {
                    ApiGroups = new[] { "batch" },
                    Resources = new[] { "jobs", "cronjobs" },
                    Verbs     = new[] { "list", "watch" }
                }
            }
        }, resourceOpts);

        _ = new ClusterRoleBinding("ksm-crb", new ClusterRoleBindingArgs
        {
            Metadata = new ObjectMetaArgs { Name = "kube-state-metrics" },
            RoleRef  = new RoleRefArgs
            {
                ApiGroup = "rbac.authorization.k8s.io",
                Kind     = "ClusterRole",
                Name     = "kube-state-metrics"
            },
            Subjects = new SubjectArgs
            {
                Kind      = "ServiceAccount",
                Name      = "kube-state-metrics",
                Namespace = args.Namespace
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { ksmCr, ksmSa } });

        _ = new Deployment("ksm-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "kube-state-metrics" },
            Spec     = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "kube-state-metrics" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "kube-state-metrics" }
                    },
                    Spec = new PodSpecArgs
                    {
                        ServiceAccountName = "kube-state-metrics",
                        Containers         = new ContainerArgs
                        {
                            Name            = "kube-state-metrics",
                            Image           = "registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0",
                            ImagePullPolicy = "IfNotPresent",
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { Name = "metrics",   ContainerPortValue = 8080 },
                                new() { Name = "telemetry", ContainerPortValue = 8081 }
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "10m",  ["memory"] = "64Mi"  },
                                Limits   = new InputMap<string> { ["cpu"] = "100m", ["memory"] = "128Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/healthz", Port = 8080 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = ksmSa });

        _ = new Service("ksm-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "kube-state-metrics" },
            Spec     = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "kube-state-metrics" },
                Ports    = new List<ServicePortArgs>
                {
                    new() { Name = "metrics",   Port = 8080, TargetPort = 8080 },
                    new() { Name = "telemetry", Port = 8081, TargetPort = 8081 }
                }
            }
        }, nsDep);

        // ── node-exporter ────────────────────────────────────────────────────────
        // DaemonSet : tourne sur chaque noeud Kind.
        // hostPID + hostNetwork + montage /proc, /sys, / → métriques CPU, RAM, disque, réseau.
        var neSa = new ServiceAccount("ne-sa", new ServiceAccountArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "node-exporter" }
        }, nsDep);

        var neCr = new ClusterRole("ne-cr", new ClusterRoleArgs
        {
            Metadata = new ObjectMetaArgs { Name = "node-exporter" },
            Rules    = new List<PolicyRuleArgs>
            {
                new()
                {
                    ApiGroups = new[] { "" },
                    Resources = new[] { "nodes" },
                    Verbs     = new[] { "list", "watch" }
                }
            }
        }, resourceOpts);

        _ = new ClusterRoleBinding("ne-crb", new ClusterRoleBindingArgs
        {
            Metadata = new ObjectMetaArgs { Name = "node-exporter" },
            RoleRef  = new RoleRefArgs
            {
                ApiGroup = "rbac.authorization.k8s.io",
                Kind     = "ClusterRole",
                Name     = "node-exporter"
            },
            Subjects = new SubjectArgs
            {
                Kind      = "ServiceAccount",
                Name      = "node-exporter",
                Namespace = args.Namespace
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = new Resource[] { neCr, neSa } });

        _ = new DaemonSet("node-exporter-ds", new DaemonSetArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "node-exporter" },
            Spec     = new DaemonSetSpecArgs
            {
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "node-exporter" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "node-exporter" }
                    },
                    Spec = new PodSpecArgs
                    {
                        ServiceAccountName = "node-exporter",
                        HostPID            = true,
                        HostNetwork        = true,
                        Tolerations = new TolerationArgs
                        {
                            Operator = "Exists"  // tolere tous les taints (noeud control-plane de Kind)
                        },
                        Containers = new ContainerArgs
                        {
                            Name            = "node-exporter",
                            Image           = "quay.io/prometheus/node-exporter:v1.9.1",
                            ImagePullPolicy = "IfNotPresent",
                            Args = new[]
                            {
                                "--path.procfs=/host/proc",
                                "--path.sysfs=/host/sys",
                                "--path.rootfs=/host/root",
                                "--collector.filesystem.mount-points-exclude=^/(sys|proc|dev|host|etc)($|/)"
                            },
                            Ports = new ContainerPortArgs { Name = "metrics", ContainerPortValue = 9100 },
                            VolumeMounts = new List<VolumeMountArgs>
                            {
                                new() { Name = "proc",   MountPath = "/host/proc", ReadOnly = true },
                                new() { Name = "sys",    MountPath = "/host/sys",  ReadOnly = true },
                                new() { Name = "rootfs", MountPath = "/host/root", ReadOnly = true }
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "10m",  ["memory"] = "32Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "100m", ["memory"] = "64Mi" }
                            }
                        },
                        Volumes = new List<VolumeArgs>
                        {
                            new() { Name = "proc",   HostPath = new HostPathVolumeSourceArgs { Path = "/proc" } },
                            new() { Name = "sys",    HostPath = new HostPathVolumeSourceArgs { Path = "/sys"  } },
                            new() { Name = "rootfs", HostPath = new HostPathVolumeSourceArgs { Path = "/"     } }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = neSa });

        _ = new Service("node-exporter-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "node-exporter" },
            Spec     = new ServiceSpecArgs
            {
                // ClusterIP (pas headless) pour que le static_config Prometheus fonctionne
                // sur Kind single-node — le ClusterIP pointe vers le seul pod node-exporter.
                Selector = new InputMap<string> { ["app"] = "node-exporter" },
                Ports    = new ServicePortArgs { Name = "metrics", Port = 9100, TargetPort = 9100 }
            }
        }, nsDep);

        OtelCollectorEndpoint = Output.Create(
            $"http://otel-collector.{args.Namespace}.svc.cluster.local:4317");

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["otelCollectorEndpoint"] = OtelCollectorEndpoint
        });
    }
}
