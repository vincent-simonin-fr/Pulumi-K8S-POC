using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tooling.ProcessTasks;

/// <summary>
/// Automatisation des orchestrations multi-étapes du projet.
///
/// Périmètre volontairement restreint aux enchaînements fastidieux à faire à la
/// main. Les commandes pulumi (up, preview, destroy, config) et kubectl restent
/// lancées manuellement — l'API Pulumi/kubectl est conservée au quotidien.
///
/// Lancement :
///   dotnet nuke <Target>            (ex: dotnet nuke Launch)
///   dotnet nuke --help              (liste des targets + paramètres)
///
/// Targets exposées :
///   Launch          Bootstrap complet : RecreateCluster + PreloadImages + BuildImages + pulumi up
///   RecreateCluster Supprime et recrée le cluster Kind
///   PreloadImages   Pull (podman) + load (kind) des images infra/observabilité/KEDA/metrics-server
///   BuildImages     Build/tag/load des 3 apps (SemVer + SHA par service) + pulumi config set
///   Publish         BuildImages + pulumi up (render) + commit + push (workflow GitOps complet)
///   GitopsOn/Off    Bascule gitops:enabled
///   PresaleStart/Stop  Pré-scaling avant flash sale (KEDA + HPA)
///
/// Le cluster Kind tourne sous Podman : KIND_EXPERIMENTAL_PROVIDER=podman est
/// positionné automatiquement pour toutes les commandes kind.
/// </summary>
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Launch);

    // ── Constantes projet ─────────────────────────────────────────────────────
    const string Namespace   = "ecommerce";

    /// <summary>
    /// Nom du cluster Kind ciblé. Défaut : ecommerce (cluster dev).
    /// Pour un test HA parallèle SANS détruire le cluster dev :
    ///   --cluster-name ecommerce-ha   (combiné à --kind-config kind-config-multinode.yaml)
    /// </summary>
    [Parameter("Nom du cluster Kind (défaut : ecommerce ; HA parallèle : ecommerce-ha).")]
    readonly string ClusterName = "ecommerce";

    /// <summary>
    /// Saute RecreateCluster (delete + create du cluster Kind) pour reprendre un
    /// Launch interrompu directement au préchargement des images.
    /// Usage : dotnet nuke Launch --skip-cluster-recreate
    /// Utile si le cluster existe déjà et que seul PreloadImages/BuildImages a échoué.
    /// </summary>
    [Parameter("Reprend sans recréer le cluster Kind (saute RecreateCluster).")]
    readonly bool SkipClusterRecreate;

    // Répertoire d'exécution Pulumi (relatif à la racine du repo).
    AbsolutePath PulumiDir   => RootDirectory / "infra" / "Ecommerce.Infra";
    AbsolutePath VersionFile => RootDirectory / "VERSION";

    /// <summary>
    /// Fichier de config Kind. Défaut : kind-config.yaml (mono-nœud dev).
    /// Pour tester la HA : --kind-config kind-config-multinode.yaml
    /// </summary>
    // Name explicite : sans lui, Nuke exposerait --kind-config-file (kebab de
    // KindConfigFile). On force --kind-config pour coller à la doc / aux habitudes.
    [Parameter("Fichier de config Kind (défaut : kind-config.yaml ; HA : kind-config-multinode.yaml).", Name = "kind-config")]
    readonly string KindConfigFile = "kind-config.yaml";

    AbsolutePath KindConfig  => RootDirectory / KindConfigFile;

    // ── Helpers d'exécution ───────────────────────────────────────────────────
    // Toutes les commandes kind reçoivent KIND_EXPERIMENTAL_PROVIDER=podman.
    static readonly IReadOnlyDictionary<string, string> KindEnv =
        new Dictionary<string, string>(EnvironmentInfo.Variables) { ["KIND_EXPERIMENTAL_PROVIDER"] = "podman" };

    // Les outils externes (kind, podman, kubectl, pulumi) écrivent leur progression
    // sur stderr → Nuke la taguerait [ERR] par défaut (fausses alertes rouges :
    // "Creating cluster", "Copying blob", "loading..."). On route les DEUX flux
    // (Std + Err) en Information : la sortie reste visible mais neutre. Les vrais
    // échecs restent détectés par le code de sortie (AssertZeroExitCode), jamais
    // par le niveau de log.
    static void ToolLogger(OutputType type, string line) => Serilog.Log.Information(line);

    // Exécute une commande externe et échoue le build si exit code != 0.
    static void Run(string tool, string args, AbsolutePath? workingDir = null,
                    IReadOnlyDictionary<string, string>? env = null)
    {
        var process = StartProcess(tool, args,
            workingDirectory: workingDir,
            environmentVariables: env,
            logger: ToolLogger);
        process.AssertZeroExitCode();
    }

    // Variante kind (injecte l'env Podman).
    static void Kind(string args) => Run("kind", args, env: KindEnv);

    // ── Pulumi (gestion de la passphrase) ─────────────────────────────────────
    // Le stack utilise un secrets provider par passphrase → pulumi réclame
    // PULUMI_CONFIG_PASSPHRASE. Lancé en sous-process, le prompt interactif ne
    // fonctionne pas : on injecte la passphrase via l'environnement pour rendre
    // tous les appels pulumi NON interactifs.
    //
    // Source de la passphrase (par priorité) :
    //   1. paramètre Nuke --pulumi-passphrase (marqué [Secret])
    //   2. variable PULUMI_CONFIG_PASSPHRASE déjà présente dans le shell
    // Si aucune des deux : pulumi échoue avec un message clair ("passphrase must be set").
    [Parameter("Passphrase du secrets provider Pulumi. À défaut, lue depuis PULUMI_CONFIG_PASSPHRASE.")]
    [Secret]
    readonly string? PulumiPassphrase;

    // Environnement enrichi pour les commandes pulumi : hérite du shell + force
    // PULUMI_CONFIG_PASSPHRASE si le paramètre Nuke est fourni.
    IReadOnlyDictionary<string, string> PulumiEnv()
    {
        var env = new Dictionary<string, string>(EnvironmentInfo.Variables);
        if (!string.IsNullOrEmpty(PulumiPassphrase))
            env["PULUMI_CONFIG_PASSPHRASE"] = PulumiPassphrase;
        return env;
    }

    // Helper centralisé pour TOUTE commande pulumi (up, config set...).
    // Garantit que la passphrase est transmise → exécution non interactive.
    void Pulumi(string args) => Run("pulumi", args, PulumiDir, PulumiEnv());

    // pulumi up interne — appelé par Launch et Publish (pas exposé comme target :
    // le `pulumi up` du quotidien se lance manuellement, on conserve la compétence).
    void PulumiUp() => Pulumi("up --yes");

    // ── Bootstrap complet (ex k8s_complete_launch.cmd) ────────────────────────
    Target Launch => _ => _
        .Description("Bootstrap complet : recrée le cluster Kind, précharge les images, build les apps, pulumi up.")
        .DependsOn(RecreateCluster, PreloadImages, BuildImages)
        .Executes(() =>
        {
            PulumiUp();
            Serilog.Log.Information("══════════════════════════════════════════════");
            Serilog.Log.Information(" Déploiement terminé !");
            Serilog.Log.Information(" Gateway    -> http://localhost:30080");
            Serilog.Log.Information(" Grafana    -> http://localhost:30030");
            Serilog.Log.Information(" Jaeger     -> http://localhost:30686");
            Serilog.Log.Information(" Argo CD    -> http://localhost:8080  (kubectl port-forward -n argocd svc/argocd-server 8080:80)");
            Serilog.Log.Information(" Vault      -> http://localhost:30820 (NodePort)");
            Serilog.Log.Information(" MinIO      -> http://localhost:9001  (kubectl port-forward -n minio svc/minio-console 9001:9001)");
            Serilog.Log.Information(" Prometheus -> http://localhost:9090  (kubectl port-forward -n monitoring svc/kube-prometheus-stack-prometheus 9090:9090)");
            Serilog.Log.Information(" RabbitMQ   -> http://localhost:15672 (kubectl port-forward -n ecommerce svc/rabbitmq 15672:15672)");
            Serilog.Log.Information("══════════════════════════════════════════════");
        });

    Target RecreateCluster => _ => _
        .Description("Supprime et recrée le cluster Kind ecommerce.")
        // --skip-cluster-recreate : saute cette étape (cluster déjà en place) et laisse
        // PreloadImages/BuildImages/pulumi up s'exécuter → reprise d'un Launch interrompu.
        .OnlyWhenDynamic(() => !SkipClusterRecreate)
        .Executes(() =>
        {
            Assert.FileExists(KindConfig, $"kind-config.yaml introuvable : {KindConfig}");
            // kind delete ne doit pas faire échouer si le cluster n'existe pas.
            StartProcess("kind", $"delete cluster --name {ClusterName}",
                    environmentVariables: KindEnv, logger: ToolLogger)
                .WaitForExit();
            Kind($"create cluster --name {ClusterName} --config \"{KindConfig}\"");
            Run("kubectl", $"config use-context kind-{ClusterName}");
        });

    // ── Préchargement des images infra/observabilité/KEDA/metrics-server ──────
    // Évite les timeouts de pull pendant les Helm install (CNPG, KEDA) sur connexion lente.
    static readonly string[] PreloadImageList =
    {
        // Infra
        "postgres:16-alpine",
        "rabbitmq:4.3.1-management-alpine",
        "redis:7-alpine",
        // CNPG (chart 0.23.2 = opérateur 1.25.1) + PostgreSQL bookworm + PgBouncer
        "ghcr.io/cloudnative-pg/cloudnative-pg:1.25.1",
        "ghcr.io/cloudnative-pg/postgresql:16.6-bookworm",
        "ghcr.io/cloudnative-pg/pgbouncer:1.23.0",
        // Observabilité — tracing (géré par Pulumi : ObservabilityResources)
        "otel/opentelemetry-collector-contrib:0.153.0",
        "jaegertracing/all-in-one:1.76.0",
        // Observabilité — métriques via kube-prometheus-stack (chart 86.1.0).
        // ⚠️ Ces tags suivent la version du chart (observability:kpStackVersion) :
        //     les réaligner si le chart est bumpé, sinon pull live = très lent
        //     (Prometheus distroless ≈ 155 Mo). Source de vérité :
        //     kubectl get pods -n monitoring -o jsonpath='{..image}'
        "quay.io/prometheus/prometheus:v3.12.0-distroless",
        "quay.io/prometheus-operator/prometheus-operator:v0.91.0",
        "quay.io/prometheus-operator/prometheus-config-reloader:v0.91.0",
        "quay.io/prometheus/node-exporter:v1.11.1-distroless",
        // Alertmanager — déployé seulement si alerting:enabled=true (défaut prod), mais
        // préchargé pour éviter un pull live lors de l'activation. Tag = défaut de
        // l'operator v0.91.0.
        "quay.io/prometheus/alertmanager:v0.28.1",
        "registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.19.0",
        "quay.io/kiwigrid/k8s-sidecar:2.7.3",
        "grafana/grafana:13.0.1-security-01",
        // postgres_exporter — géré par Pulumi (DatabaseResources), hors chart
        "prometheuscommunity/postgres-exporter:v0.16.0",
        // KEDA 2.17.0 (3 composants du chart)
        "ghcr.io/kedacore/keda:2.17.0",
        "ghcr.io/kedacore/keda-metrics-apiserver:2.17.0",
        "ghcr.io/kedacore/keda-admission-webhooks:2.17.0",
        // Metrics Server (chart 3.12.2 = app v0.7.2)
        "registry.k8s.io/metrics-server/metrics-server:v0.7.2",
        // Vault (chart 0.32.0 = Vault 1.21.2). Injector désactivé → image serveur seule.
        "hashicorp/vault:1.21.2",
        // Vault Secrets Operator (chart 1.4.0).
        "hashicorp/vault-secrets-operator:1.4.0",
        // MinIO (object storage S3 pour les backups CNPG — chart 5.4.0). Le Job de
        // création de bucket tire minio/mc en live (petite image).
        "quay.io/minio/minio:RELEASE.2024-12-18T13-15-44Z",
    };

    // Images du mode RabbitMQ cluster (rabbitmq:cluster=true uniquement).
    // ⚠️ NON préchargées par défaut : le mode cluster est prod (multi-nœuds), pas
    // dev Kind. L'opérateur officiel (rabbitmqoperator/cluster-operator:latest) est
    // appliqué via manifeste depuis github.com/rabbitmq/cluster-operator.
    // Pour précharger en vue d'un test cluster local, ajouter à PreloadImageList :
    //   "rabbitmqoperator/cluster-operator:latest"
    //   "rabbitmq:4.3.1-management-alpine"   (déjà préchargée ci-dessus)
    // Note : "latest" n'est pas reproductible — épingler une version pour un test stable.

    Target PreloadImages => _ => _
        .Description("Pull (podman) + load (kind) des images infra/observabilité/KEDA/metrics-server.")
        // 'kind load' exige que le cluster existe → toujours après RecreateCluster.
        .After(RecreateCluster)
        .Executes(() =>
        {
            foreach (var image in PreloadImageList)
            {
                Serilog.Log.Information("Préchargement {Image}", image);
                Run("podman", $"pull {image}");
                Kind($"load docker-image {image} --name {ClusterName}");
            }
        });
}
