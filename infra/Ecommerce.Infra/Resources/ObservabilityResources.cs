using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
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

    // NodePorts exposés sur l'hôte via Kind extraPortMappings
    public int GrafanaNodePort  { get; set; } = 30030;
    public int JaegerUiNodePort { get; set; } = 30686;
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

        // Service Jaeger : NodePort pour l'UI (30686), accès interne pour OTLP (4317).
        // K8s assigne un nodePort aléatoire pour le port 4317 — pas utilisé directement.
        _ = new Service("jaeger-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "jaeger" },
            Spec = new ServiceSpecArgs
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
                                "--storage.tsdb.retention.time=7d"
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
                            Env = new List<EnvVarArgs>
                            {
                                // Accès direct sans authentification — dev local uniquement
                                new() { Name = "GF_AUTH_ANONYMOUS_ENABLED",  Value = "true"  },
                                new() { Name = "GF_AUTH_ANONYMOUS_ORG_ROLE", Value = "Admin" },
                                new() { Name = "GF_AUTH_DISABLE_LOGIN_FORM", Value = "true"  }
                            },
                            Ports        = new ContainerPortArgs { Name = "http", ContainerPortValue = 3000 },
                            VolumeMounts = new VolumeMountArgs
                            {
                                Name      = "datasources",
                                MountPath = "/etc/grafana/provisioning/datasources"
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "64Mi"  },
                                Limits   = new InputMap<string> { ["cpu"] = "200m", ["memory"] = "256Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/api/health", Port = 3000 },
                                InitialDelaySeconds = 10,
                                PeriodSeconds       = 5
                            }
                        },
                        Volumes = new VolumeArgs
                        {
                            Name      = "datasources",
                            ConfigMap = new ConfigMapVolumeSourceArgs { Name = "grafana-datasources" }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = grafanaDatasourcesMap });

        _ = new Service("grafana-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "grafana" },
            Spec = new ServiceSpecArgs
            {
                Type     = "NodePort",
                Selector = new InputMap<string> { ["app"] = "grafana" },
                Ports    = new ServicePortArgs
                {
                    Name       = "http",
                    Port       = 3000,
                    TargetPort = 3000,
                    NodePort   = args.GrafanaNodePort
                }
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
