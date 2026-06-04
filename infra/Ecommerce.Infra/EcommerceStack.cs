using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

namespace Ecommerce.Infra.Resources;

public class EcommerceStack : Stack
{
    public EcommerceStack()
    {
        // ── Config par namespace (format YAML : "namespace:key") ─────────────
        // new Config("orderApi").Get("image")  lit  "orderApi:image"  dans Pulumi.dev.yaml
        var orderApiCfg     = new Config("orderApi");
        var inventoryApiCfg = new Config("inventoryApi");
        var gatewayCfg      = new Config("gateway");
        var reservationCfg  = new Config("reservation");
        var replicasCfg     = new Config("replicas");
        var hpaCfg          = new Config("hpa");
        var resourcesCfg    = new Config("resources");
        var secretsCfg      = new Config("secrets");
        var obsCfg          = new Config("observability");
        var ingressCfg      = new Config("ingress");
        var presaleCfg      = new Config("presale");
        var kedaCfg          = new Config("keda");
        var cnpgCfg          = new Config("cnpg");
        var argocdCfg        = new Config("argocd");
        var metricsServerCfg = new Config("metricsServer");
        var gitopsCfg        = new Config("gitops");
        var rabbitmqCfg      = new Config("rabbitmq");
        var vaultCfg         = new Config("vault");

        var nodePort        = gatewayCfg.GetInt32("nodePort")     ?? 30080;
        var grafanaNodePort = obsCfg.GetInt32("grafanaNodePort")  ?? 30030;
        var jaegerNodePort  = obsCfg.GetInt32("jaegerNodePort")   ?? 30686;
        var ingressEnabled  = ingressCfg.GetBoolean("enabled")    ?? false;
        var domain          = ingressCfg.Get("domain")            ?? "wizzz.com";

        // ── Mode presale ──────────────────────────────────────────────────────
        // Quand presale:enabled = true, les minReplicas des HPA/ScaledObjects sont
        // surchargés pour que les pods soient déjà en place avant le pic de trafic.
        // Activation : pulumi config set presale:enabled true && pulumi up --yes
        // Désactivation : pulumi config set presale:enabled false && pulumi up --yes
        var presaleEnabled = presaleCfg.GetBoolean("enabled") ?? false;

        // Retourne le minReplicas effectif pour order-api et gateway (HPA natif).
        // Quand presale est actif, force la valeur presale pour pré-chauffer les pods.
        int HpaMin(string key, int fallback = 1) =>
            presaleEnabled
                ? (presaleCfg.GetInt32(key) ?? fallback)
                : (hpaCfg.GetInt32(key) ?? 1);

        // Retourne le minReplicaCount effectif pour inventory-api (ScaledObject KEDA).
        // La valeur nominale vient de keda:inventoryApiMin (section KEDA, pas HPA).
        int KedaMin(int fallback = 1) =>
            presaleEnabled
                ? (presaleCfg.GetInt32("inventoryApiMin") ?? fallback)
                : (kedaCfg.GetInt32("inventoryApiMin") ?? 1);

        // ── Metrics Server (kube-system — requis par HPA CPU et kubectl top) ────
        // Doit être déployé avant les HPA pour que les métriques CPU soient disponibles.
        // Sur Kind, --kubelet-insecure-tls est obligatoire (certs kubelets sans IP SANs).
        // En prod, passer metricsServer:kubeletInsecureTls false dans Pulumi.prod.yaml.
        _ = new MetricsServerResources("metrics-server", new MetricsServerResourcesArgs
        {
            Version            = metricsServerCfg.Get("version") ?? "3.12.2",
            KubeletInsecureTls = metricsServerCfg.GetBoolean("kubeletInsecureTls") ?? true
        });

        // ── Vault (coffre de secrets) — Phase 1 : serveur Helm ───────────────
        // Déployé scellé (SkipAwait). Init/unseal = Phase 2 ; VSO + moteur DB
        // dynamique = Phase 3. Gardé par vault:enabled pour pouvoir le désactiver.
        // Dev : standalone + storage fichier. Prod : HA Raft + auto-unseal KMS.
        // Capturés ici pour brancher les CRDs VSO APRÈS la création du namespace ecommerce.
        VaultSecretsOperatorResources? vso = null;
        VaultConfigResources? vaultConfig = null;

        if (vaultCfg.GetBoolean("enabled") ?? false)
        {
            var vault = new VaultResources("vault", new VaultResourcesArgs
            {
                Version      = vaultCfg.Get("version")             ?? "0.32.0",
                HaEnabled    = vaultCfg.GetBoolean("haEnabled")    ?? false,
                HaReplicas   = vaultCfg.GetInt32("haReplicas")     ?? 3,
                StorageClass = vaultCfg.Get("storageClass")        ?? "standard",
                StorageSize  = vaultCfg.Get("storageSize")         ?? "1Gi",
                SealConfig   = vaultCfg.Get("sealConfig")          ?? ""
            });

            // Vault Secrets Operator (livraison Vault → Secret K8s). DependsOn le
            // serveur Vault (le chart s'installe en parallèle, mais on garde l'ordre).
            vso = new VaultSecretsOperatorResources("vault-secrets-operator", new VaultSecretsOperatorResourcesArgs
            {
                Version = vaultCfg.Get("vsoVersion") ?? "1.4.0"
            }, new ComponentResourceOptions { DependsOn = { vault } });

            // Config Vault (Option A : Job in-cluster). Créée UNIQUEMENT si vault:rootToken
            // est renseigné → bootstrap : up (serveur) → init/unseal → config set --secret
            // vault:rootToken → up (ce Job configure Vault). Cf. docs/vault.md.
            if (!string.IsNullOrEmpty(vaultCfg.Get("rootToken")))
            {
                vaultConfig = new VaultConfigResources("vault-config", new VaultConfigResourcesArgs
                {
                    RootToken     = vaultCfg.GetSecret("rootToken") ?? (Input<string>)"",
                    VaultImageTag = "1.21.2"
                }, new ComponentResourceOptions { DependsOn = { vault } });
            }
        }

        // ── Observabilité ─────────────────────────────────────────────────────
        // OTel Collector + Jaeger (tracing) gérés ici. Prometheus + Grafana +
        // node-exporter + kube-state-metrics : fournis par le chart
        // kube-prometheus-stack (ci-dessous) via le Prometheus Operator.
        var observability = new ObservabilityResources("observability", new ObservabilityResourcesArgs
        {
            OtelCollectorVersion = obsCfg.Get("otelVersion")  ?? "0.153.0",
            JaegerVersion        = obsCfg.Get("jaegerVersion") ?? "1.76.0",
            JaegerUiNodePort     = jaegerNodePort,
            IngressEnabled       = ingressEnabled
        });

        // ── Métriques : kube-prometheus-stack (Operator + Grafana + exporters) ─
        // DependsOn observability : namespace monitoring + Jaeger (datasource Grafana).
        var kpStack = new KubePrometheusStackResources("kube-prometheus-stack", new KubePrometheusStackResourcesArgs
        {
            Namespace            = "monitoring",
            Version              = obsCfg.Get("kpStackVersion") ?? "86.1.0",
            GrafanaNodePort      = grafanaNodePort,
            IngressEnabled       = ingressEnabled,
            GrafanaAdminPassword = obsCfg.Get("grafanaAdminPassword") ?? "",
            JaegerUrl            = "http://jaeger.monitoring.svc.cluster.local:16686"
        }, new ComponentResourceOptions { DependsOn = { observability } });

        // ServiceMonitors : scrape déclaratif découvert par l'Operator (DependsOn le
        // chart pour que la CRD ServiceMonitor existe avant le kubectl apply).
        _ = new ServiceMonitorResources("service-monitors", new ServiceMonitorResourcesArgs
        {
            MonitoringNamespace = "monitoring"
        }, new ComponentResourceOptions { DependsOn = { kpStack } });

        // Dashboards : ConfigMaps labellisés grafana_dashboard=1, chargés par le
        // sidecar du Grafana du chart.
        _ = new GrafanaDashboardsResources("grafana-dashboards", new GrafanaDashboardsResourcesArgs
        {
            Namespace = "monitoring"
        }, new ComponentResourceOptions { DependsOn = { kpStack } });

        // ── Namespace ─────────────────────────────────────────────────────────
        var ns = new Namespace("ecommerce-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = "ecommerce" }
        });

        var namespaceName = ns.Metadata.Apply(m => m.Name);

        // ── Livraison VSO (CRDs) ──────────────────────────────────────────────
        // SA vault-auth + VaultConnection/VaultAuth/VaultDynamicSecret dans ecommerce
        // → Secret K8s 'order-db-dynamic' rotaté. Nécessite VSO + Vault configuré +
        // le namespace ecommerce. Créé seulement si la config Vault est active.
        // Phase 3e : order-api consomme des creds PostgreSQL DYNAMIQUES (Vault/VSO)
        // au lieu du secret statique. Opt-in (order-api dépendra alors du bootstrap Vault).
        VaultSecretsResources? vaultSecrets = null;
        if (vso != null && vaultConfig != null)
        {
            vaultSecrets = new VaultSecretsResources("vault-secrets", new VaultSecretsResourcesArgs
            {
                Namespace = "ecommerce"
            }, new ComponentResourceOptions { DependsOn = { vso, vaultConfig, ns } });
        }

        // Dès que le pipeline VSO existe (Vault activé + bootstrappé), order-api et
        // inventory-api consomment des creds PostgreSQL DYNAMIQUES. C'est la méthode :
        // pas de flag par service. Tant que Vault n'est pas bootstrappé (vaultSecrets null),
        // les apps restent sur le secret statique → aucun blocage au 1er déploiement.
        var useDynamicCreds = vaultSecrets != null;

        // ── Secrets (ESO + ClusterSecretStore + ExternalSecrets) ─────────────
        //  Doit être créé AVANT les pods qui consomment les secrets.
        //  Les valeurs sont lues depuis `pulumi config set --secret secrets:xxx`
        //  (voir commentaires dans Pulumi.dev.yaml).
        var secretsResources = new SecretsResources("secrets", new SecretsResourcesArgs
        {
            Namespace           = namespaceName,
            OrderDbUser         = secretsCfg.Get("orderDbUser")         ?? "postgres",
            OrderDbPassword     = secretsCfg.Get("orderDbPassword")     ?? "postgres",
            OrderDbName         = secretsCfg.Get("orderDbName")         ?? "order_db",
            InventoryDbUser     = secretsCfg.Get("inventoryDbUser")     ?? "postgres",
            InventoryDbPassword = secretsCfg.Get("inventoryDbPassword") ?? "postgres",
            InventoryDbName     = secretsCfg.Get("inventoryDbName")     ?? "inventory_db",
            RabbitMqUser        = secretsCfg.Get("rabbitmqUser")        ?? "guest",
            RabbitMqPassword    = secretsCfg.Get("rabbitmqPassword")    ?? "guest",
            // Connection strings pointent vers les Poolers PgBouncer (pas directement vers CNPG -rw).
            // Les init containers utilisent -rw séparément (voir OrderServiceResources + InventoryServiceResources).
            OrderDbHost         = "order-db-pooler",
            InventoryDbHost     = "inventory-db-pooler"
        });

        var secretsDep = new ComponentResourceOptions { DependsOn = { secretsResources } };

        // ── CNPG Operator (avant les bases de données) ────────────────────────
        // Installe cloudnative-pg via Helm (namespace cnpg-system, WaitForJobs=true).
        // DatabaseResources dépend de CnpgResources pour que les CRDs (Cluster, Pooler)
        // soient enregistrées dans l'API K8s avant que kubectl apply ne les utilise.
        // Voir CnpgResources.cs pour le détail du workaround GVK cache.
        var cnpgResources = new CnpgResources("cnpg", new CnpgResourcesArgs
        {
            Version = cnpgCfg.Get("version") ?? "0.23.2"
        });

        // DependsOn combiné : secrets (pour postgres_exporter) + CNPG (pour CRDs).
        var cnpgSecretsDep = new ComponentResourceOptions
        {
            DependsOn = { secretsResources, cnpgResources }
        };

        // ── Infrastructure (PostgreSQL CNPG + RabbitMQ + Redis) ──────────────
        var dbResources = new DatabaseResources("databases", new DatabaseResourcesArgs
        {
            Namespace           = namespaceName,
            OrderDbPassword     = secretsCfg.Get("orderDbPassword")     ?? "postgres",
            InventoryDbPassword = secretsCfg.Get("inventoryDbPassword") ?? "postgres",
            OrderInstances      = cnpgCfg.GetInt32("orderInstances")     ?? 1,
            InventoryInstances  = cnpgCfg.GetInt32("inventoryInstances") ?? 1,
            PoolerInstances     = cnpgCfg.GetInt32("poolerInstances")    ?? 1,
            // Dev : standard (local-path). Prod multi-nœuds : stockage RÉSEAU obligatoire.
            StorageClass        = cnpgCfg.Get("storageClass")            ?? "standard",
            StorageSize         = cnpgCfg.Get("storageSize")             ?? "1Gi",
        }, cnpgSecretsDep);

        // ── RabbitMQ — Deployment (dev) ou Cluster Operator (prod HA) ─────────
        // rabbitmq:cluster = true → installe l'opérateur RabbitMQ + déploie un
        // RabbitmqCluster quorum N nœuds (HA). false → Deployment simple 1 réplica.
        var rabbitmqCluster = rabbitmqCfg.GetBoolean("cluster") ?? false;

        // L'opérateur n'est installé QUE si le mode cluster est activé (dev = pas
        // d'opérateur, économie de ressources). MessagingResources DependsOn l'opérateur
        // en mode cluster pour que la CRD RabbitmqCluster soit enregistrée (cache GVK).
        ComponentResource? rabbitmqOperator = null;
        if (rabbitmqCluster)
        {
            rabbitmqOperator = new RabbitmqOperatorResources("rabbitmq-operator", new RabbitmqOperatorResourcesArgs
            {
                // Vide par défaut → image du manifeste officiel (ghcr.io/rabbitmq/...).
                // Override seulement pour épingler une version officielle précise.
                OperatorImage = rabbitmqCfg.Get("operatorImage") ?? "",
                ManifestUrl   = rabbitmqCfg.Get("operatorManifest")
                                ?? "https://github.com/rabbitmq/cluster-operator/releases/latest/download/cluster-operator.yml"
            });
        }

        // DependsOn : secrets ESO (toujours) + opérateur RabbitMQ (mode cluster).
        var mqDep = new ComponentResourceOptions { DependsOn = { secretsResources } };
        if (rabbitmqOperator is not null)
            mqDep.DependsOn.Add(rabbitmqOperator);

        var mqResources = new MessagingResources("messaging", new MessagingResourcesArgs
        {
            Namespace        = namespaceName,
            UseCluster       = rabbitmqCluster,
            Replicas         = rabbitmqCfg.GetInt32("replicas")     ?? 3,
            RabbitMqUser     = secretsCfg.Get("rabbitmqUser")        ?? "guest",
            RabbitMqPassword = secretsCfg.Get("rabbitmqPassword")    ?? "guest",
            StorageClass     = rabbitmqCfg.Get("storageClass")       ?? "standard",
            StorageSize      = rabbitmqCfg.Get("storageSize")        ?? "5Gi"
        }, mqDep);

        var cacheResources = new CacheResources("cache", new CacheResourcesArgs
        {
            Namespace = namespaceName
        });

        // ── KEDA — Kubernetes Event-Driven Autoscaling (inventory-api) ────────
        // KEDA scale inventory-api en fonction de la profondeur de la queue
        // RabbitMQ (ProductAddedToCartEvent). Réaction ~5 s vs ~75 s pour HPA CPU.
        //
        // Workflow presale :
        //   pulumi config set presale:enabled true && pulumi up --yes
        //   → minReplicaCount du ScaledObject passe à presale:inventoryApiMin (3)
        //   → les pods sont pré-chauffés AVANT le pic, sans cold-start
        //
        // Urgence (sans pulumi up) :
        //   dotnet nuke PresaleStart / PresaleStop
        //   → patch direct du ScaledObject via kubectl
        _ = new KedaResources("keda", new KedaResourcesArgs
        {
            Namespace       = namespaceName,
            RabbitMqUser    = secretsCfg.Get("rabbitmqUser")     ?? "guest",
            RabbitMqPassword= secretsCfg.Get("rabbitmqPassword") ?? "guest",
            QueueName       = kedaCfg.Get("queueName")           ?? "product-added-to-cart",
            QueueLength     = kedaCfg.GetInt32("queueLength")    ?? 5,
            MinReplicas     = KedaMin(fallback: 3),
            MaxReplicas     = kedaCfg.GetInt32("inventoryApiMax") ?? 8,
            PollingInterval = kedaCfg.GetInt32("pollingInterval") ?? 5,
            CooldownPeriod  = kedaCfg.GetInt32("cooldownPeriod")  ?? 60,
            ScaleDownWindow = kedaCfg.GetInt32("scaleDownWindow") ?? 240,
            KedaVersion     = kedaCfg.Get("version")              ?? "2.17.0"
        });

        // ── GitOps (ArgoCD) ───────────────────────────────────────────────────
        // Quand gitops:enabled = true, les 3 apps (order-api, inventory-api, gateway)
        // ne sont PLUS appliquées au cluster par Pulumi : elles sont rendues en YAML
        // dans gitops/apps/ via un Provider dédié (RenderYamlToDirectory).
        // ArgoCD surveille ensuite ce dossier dans Git et déploie les apps.
        //
        // Pulumi conserve la gestion directe de toute l'infra (CNPG, KEDA, secrets,
        // observabilité, ArgoCD, metrics-server) — pattern "infra par IaC, apps par GitOps".
        //
        // ⚠️ Transition : activer ce flag fait que Pulumi RETIRE les 3 apps du cluster
        // (changement de provider = replace). Workflow complet :
        //   1. pulumi config set gitops:enabled true
        //   2. pulumi config set gitops:repoUrl https://github.com/<user>/<repo>
        //   3. pulumi up --yes            → rend les YAML + crée l'Application ArgoCD
        //   4. git add manifests && git commit && git push
        //   5. ArgoCD synchronise → (re)déploie les apps depuis Git
        var gitopsEnabled = gitopsCfg.GetBoolean("enabled") ?? false;

        // Provider de rendu : écrit les manifests au lieu de les appliquer au cluster.
        // Chemin relatif au répertoire d'exécution Pulumi (infra/Ecommerce.Infra)
        // → ../../gitops/apps = <racine repo>/gitops/apps.
        var manifestsProvider = gitopsEnabled
            ? new Pulumi.Kubernetes.Provider("manifests-render", new Pulumi.Kubernetes.ProviderArgs
              {
                  RenderYamlToDirectory = gitopsCfg.Get("outputDir") ?? "../../gitops/apps"
              })
            : null;

        // Options passées aux 3 ComponentResources applicatives.
        // Avec le render provider : les ressources enfants (Deployment, Service, HPA,
        // ConfigMap) héritent du provider via Parent et sont rendues en YAML.
        // Sans (mode normal) : options vides → provider par défaut → applique au cluster.
        ComponentResourceOptions AppOpts() =>
            manifestsProvider is null
                ? new ComponentResourceOptions()
                : new ComponentResourceOptions { Providers = { manifestsProvider } };

        // ── Services applicatifs ──────────────────────────────────────────────
        // order-api dépend de VSO (Secret dynamique) quand orderDynamicCreds est actif.
        var orderOpts = AppOpts();
        if (useDynamicCreds)
            orderOpts.DependsOn.Add(vaultSecrets!);

        var orderApi = new OrderServiceResources("order-service", new ServiceResourcesArgs
        {
            Namespace      = namespaceName,
            Image          = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev",
            // ConnectionStrings__OrderDb : dynamique (Vault/VSO) si le pipeline existe, sinon statique.
            DbCredentialsSecretName = useDynamicCreds ? "order-db-dynamic" : SecretsResources.OrderDbSecretName,
            // Init container : attend que le primary CNPG soit Ready (service -rw créé par CNPG).
            // La connection string ASP.NET Core passe par le Pooler (secrets → order-db-pooler).
            OrderDbHost    = dbResources.OrderDbRwServiceName,
            RabbitMqHost   = mqResources.RabbitMqServiceName,
            OtelEndpoint   = observability.OtelCollectorEndpoint,
            Replicas       = replicasCfg.GetInt32("orderApi") ?? 1,
            CpuRequest     = resourcesCfg.Get("orderApiCpuRequest")    ?? "100m",
            CpuLimit       = resourcesCfg.Get("orderApiCpuLimit")      ?? "500m",
            MemoryRequest  = resourcesCfg.Get("orderApiMemoryRequest") ?? "128Mi",
            MemoryLimit    = resourcesCfg.Get("orderApiMemoryLimit")   ?? "256Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("orderApiEnabled") ?? false,
                MinReplicas   = HpaMin("orderApiMin", fallback: 3),
                MaxReplicas   = hpaCfg.GetInt32("orderApiMax") ?? 4,
                CpuPercent    = hpaCfg.GetInt32("orderApiCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("orderApiMemory")
            }
        }, orderOpts);

        // inventory-api dépend de VSO (Secret dynamique) quand inventoryDynamicCreds est actif.
        var invOpts = AppOpts();
        if (useDynamicCreds)
            invOpts.DependsOn.Add(vaultSecrets!);

        var inventoryApi = new InventoryServiceResources("inventory-service", new InventoryServiceResourcesArgs
        {
            Namespace             = namespaceName,
            Image                 = inventoryApiCfg.Get("image") ?? "localhost/ecommerce/inventory-api:dev",
            // ConnectionStrings__InventoryDb : dynamique (Vault/VSO) si le pipeline existe, sinon statique.
            DbCredentialsSecretName = useDynamicCreds ? "inventory-db-dynamic" : SecretsResources.InventoryDbSecretName,
            // Init container : attend que le primary CNPG soit Ready (service -rw créé par CNPG).
            InventoryDbHost       = dbResources.InventoryDbRwServiceName,
            RabbitMqHost          = mqResources.RabbitMqServiceName,
            OtelEndpoint          = observability.OtelCollectorEndpoint,
            RedisConnectionString = cacheResources.RedisConnectionString,
            ReservationTtlMinutes = reservationCfg.GetInt32("ttlMinutes") ?? 10,
            CheckIntervalSeconds  = reservationCfg.GetInt32("checkIntervalSeconds") ?? 30,
            Replicas              = replicasCfg.GetInt32("inventoryApi") ?? 1,
            CpuRequest            = resourcesCfg.Get("inventoryApiCpuRequest")    ?? "100m",
            CpuLimit              = resourcesCfg.Get("inventoryApiCpuLimit")      ?? "500m",
            MemoryRequest         = resourcesCfg.Get("inventoryApiMemoryRequest") ?? "128Mi",
            MemoryLimit           = resourcesCfg.Get("inventoryApiMemoryLimit")   ?? "256Mi",
            // Hpa : non passé — le scaling est géré par KEDA (ScaledObject ci-dessus).
        }, invOpts);

        var gateway = new GatewayResources("gateway", new GatewayResourcesArgs
        {
            Namespace        = namespaceName,
            Image            = gatewayCfg.Get("image") ?? "localhost/ecommerce/gateway:dev",
            NodePort         = nodePort,
            IngressEnabled   = ingressEnabled,
            OrderApiHost     = orderApi.ServiceName,
            InventoryApiHost = inventoryApi.ServiceName,
            OtelEndpoint     = observability.OtelCollectorEndpoint,
            Replicas         = replicasCfg.GetInt32("gateway") ?? 1,
            CpuRequest       = resourcesCfg.Get("gatewayCpuRequest")    ?? "50m",
            CpuLimit         = resourcesCfg.Get("gatewayCpuLimit")      ?? "250m",
            MemoryRequest    = resourcesCfg.Get("gatewayMemoryRequest") ?? "64Mi",
            MemoryLimit      = resourcesCfg.Get("gatewayMemoryLimit")   ?? "128Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("gatewayEnabled") ?? false,
                MinReplicas   = HpaMin("gatewayMin", fallback: 2),
                MaxReplicas   = hpaCfg.GetInt32("gatewayMax") ?? 3,
                CpuPercent    = hpaCfg.GetInt32("gatewayCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("gatewayMemory")
            }
        }, AppOpts());

        // ── Argo CD (GitOps CD) ───────────────────────────────────────────────
        // Déployé dans le namespace "argocd", indépendant de la stack ecommerce.
        // Accès dev : kubectl port-forward -n argocd svc/argocd-server 8080:80
        //             → http://localhost:8080
        // Accès prod : https://argocd.{domain} (ingress nginx + cert-manager)
        // CLI       : argocd login localhost:8080 --username admin --insecure
        var argocd = new ArgocdResources("argocd", new ArgocdResourcesArgs
        {
            Version                = argocdCfg.Get("version")                 ?? "7.8.3",
            Domain                 = domain,
            IngressEnabled         = ingressEnabled,
            AdminPasswordBcrypt    = argocdCfg.Get("adminPasswordHash")        ?? "",
            ServerReplicas         = argocdCfg.GetInt32("serverReplicas")         ?? 1,
            RepoServerReplicas     = argocdCfg.GetInt32("repoServerReplicas")     ?? 1,
            ApplicationSetReplicas = argocdCfg.GetInt32("applicationSetReplicas") ?? 1
        });

        // ── Application ArgoCD (GitOps des apps) ──────────────────────────────
        // Créée uniquement si gitops:enabled = true ET gitops:repoUrl renseigné.
        // L'Application surveille gitops/apps/ dans le repo Git et synchronise
        // order-api, inventory-api, gateway (rendus en YAML par le manifestsProvider).
        // DependsOn argocd : l'opérateur ArgoCD et ses CRDs doivent exister d'abord.
        var gitopsRepoUrl = gitopsCfg.Get("repoUrl");
        if (gitopsEnabled && !string.IsNullOrWhiteSpace(gitopsRepoUrl))
        {
            _ = new GitopsResources("gitops", new GitopsResourcesArgs
            {
                Namespace        = "ecommerce",
                RepoUrl          = gitopsRepoUrl!,
                TargetRevision   = gitopsCfg.Get("targetRevision") ?? "main",
                Path             = gitopsCfg.Get("path") ?? "gitops/apps"
            }, new ComponentResourceOptions { DependsOn = { argocd } });
        }

        // ── Ingress (prod uniquement) ─────────────────────────────────────────
        if (ingressEnabled)
        {
            _ = new IngressResources("ingress", new IngressResourcesArgs
            {
                Domain                       = domain,
                AcmeEmail                    = ingressCfg.Get("acmeEmail")                    ?? "ops@wizzz.com",
                MonitoringBasicAuthHtpasswd  = ingressCfg.Get("monitoringBasicAuthHtpasswd") ?? "",
                CertManagerVersion           = ingressCfg.Get("certManagerVersion")           ?? "v1.16.2",
                NginxVersion                 = ingressCfg.Get("nginxVersion")                 ?? "4.11.3"
            });
        }

        // ── Outputs ───────────────────────────────────────────────────────────
        if (ingressEnabled)
        {
            GatewayUrl            = Output.Create($"https://{domain}");
            OrderApiHealthUrl     = Output.Create($"https://{domain}/health");
            InventoryApiHealthUrl = Output.Create($"https://{domain}/health");
            GrafanaUrl            = Output.Create($"https://grafana.{domain}");
            JaegerUrl             = Output.Create($"https://jaeger.{domain}");
            ArgocdUrl             = Output.Create($"https://argocd.{domain}");
        }
        else
        {
            GatewayUrl            = Output.Create($"http://localhost:{nodePort}");
            OrderApiHealthUrl     = Output.Create($"http://localhost:{nodePort}/health/orders");
            InventoryApiHealthUrl = Output.Create($"http://localhost:{nodePort}/health/inventory");
            GrafanaUrl            = Output.Create($"http://localhost:{grafanaNodePort}");
            JaegerUrl             = Output.Create($"http://localhost:{jaegerNodePort}");
            ArgocdUrl             = Output.Create("http://localhost:8080 (kubectl port-forward -n argocd svc/argocd-server 8080:80)");
        }
    }

    [Output] public Output<string> GatewayUrl            { get; set; }
    [Output] public Output<string> OrderApiHealthUrl     { get; set; }
    [Output] public Output<string> InventoryApiHealthUrl { get; set; }
    [Output] public Output<string> GrafanaUrl            { get; set; }
    [Output] public Output<string> JaegerUrl             { get; set; }
    [Output] public Output<string> ArgocdUrl             { get; set; }
}
