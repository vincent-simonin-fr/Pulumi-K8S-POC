using Pulumi;
using Pulumi.Command.Local;

namespace Ecommerce.Infra.Resources;

public class GitopsResourcesArgs
{
    /// <summary>Namespace cible où ArgoCD déploie les apps (Deployments, Services, HPA).</summary>
    public Input<string> Namespace { get; set; } = "ecommerce";

    /// <summary>URL du repo Git contenant les manifests rendus (ex : https://github.com/user/repo).</summary>
    public Input<string> RepoUrl { get; set; } = default!;

    /// <summary>Branche ou tag Git suivi par ArgoCD.</summary>
    public Input<string> TargetRevision { get; set; } = "main";

    /// <summary>Chemin des manifests dans le repo (ex : gitops/apps).</summary>
    public Input<string> Path { get; set; } = "gitops/apps";

    /// <summary>Namespace d'ArgoCD (où vit la ressource Application).</summary>
    public string ArgocdNamespace { get; set; } = "argocd";
}

/// <summary>
/// Crée l'Application ArgoCD qui matérialise la démarche GitOps des applications.
///
/// Architecture :
///   gitops/apps/ (Git)  ──surveillé par──►  Application ArgoCD  ──sync──►  cluster
///
///   Les 3 apps (order-api, inventory-api, gateway) sont rendues en YAML par Pulumi
///   (Provider RenderYamlToDirectory dans EcommerceStack) puis poussées dans Git.
///   ArgoCD réconcilie en continu l'état Git ↔ cluster.
///
/// syncPolicy.automated :
///   prune    = true  → supprime du cluster les ressources retirées de Git
///   selfHeal = true  → corrige toute dérive manuelle (kubectl edit) vers l'état Git
///
/// ignoreDifferences sur /spec/replicas :
///   Les Deployments sont scalés dynamiquement par HPA (order-api, gateway) et KEDA
///   (inventory-api). Sans cette exclusion, ArgoCD verrait le nombre de replicas réel
///   diverger du YAML (replicas: 1) et le réinitialiserait en boucle, annulant le scaling.
///
/// Workaround GVK cache Pulumi (identique à CNPG / KEDA) :
///   La CRD Application (argoproj.io/v1alpha1) est installée par le chart ArgoCD pendant
///   ce même pulumi up → absente du cache GVK du provider Kubernetes.
///   kubectl apply via Pulumi.Command interroge directement l'API server et la connaît.
/// </summary>
public class GitopsResources : ComponentResource
{
    public GitopsResources(string name, GitopsResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:GitopsResources", name, opts)
    {
        var yaml = Output.Format($@"apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: ecommerce-apps
  namespace: {args.ArgocdNamespace}
  finalizers:
    - resources-finalizer.argocd.argoproj.io
spec:
  project: default
  source:
    repoURL: {args.RepoUrl}
    targetRevision: {args.TargetRevision}
    path: {args.Path}
    directory:
      recurse: true
  destination:
    server: https://kubernetes.default.svc
    namespace: {args.Namespace}
  syncPolicy:
    automated:
      prune: true
      selfHeal: true
    syncOptions:
      - CreateNamespace=false
  ignoreDifferences:
    - group: apps
      kind: Deployment
      jsonPointers:
        - /spec/replicas");

        // Create = Update : server-side apply idempotent.
        // Delete : retire l'Application (les apps déployées restent — orphan policy par défaut).
        _ = new Command("ecommerce-apps-application", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = yaml
        }, new CustomResourceOptions { Parent = this });

        RegisterOutputs();
    }
}
