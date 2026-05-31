@echo off
setlocal

:: Recreer le cluster Kind + deployer via Pulumi.
:: Lancer depuis la RACINE du projet :
::   scripts\k8s_complete_launch.cmd

if not exist "kind-config.yaml" (
    echo ERREUR : lancer ce script depuis la racine du projet.
    exit /b 1
)

set KIND_EXPERIMENTAL_PROVIDER=podman

:: ------------------------------------------------------------------
echo.
echo [1/5] Recreation du cluster Kind...
:: ------------------------------------------------------------------
kind delete cluster --name ecommerce
kind create cluster --name ecommerce --config kind-config.yaml
if errorlevel 1 ( echo ERREUR : kind create a echoue & exit /b 1 )
kubectl config use-context kind-ecommerce

:: ------------------------------------------------------------------
echo.
echo [2/5] Images infra (postgres, rabbitmq, redis, cnpg)...
:: ------------------------------------------------------------------
:: postgres:16-alpine : utilise par les init containers wait-for-dependencies (psql).
:: Le cluster PostgreSQL lui-meme tourne sous ghcr.io/cloudnative-pg/postgresql (ci-dessous).
podman pull postgres:16-alpine
kind load docker-image postgres:16-alpine --name ecommerce
podman pull rabbitmq:4.3.1-management-alpine
kind load docker-image rabbitmq:4.3.1-management-alpine --name ecommerce
podman pull redis:7-alpine
kind load docker-image redis:7-alpine --name ecommerce

:: Images CNPG : operateur + image PostgreSQL 16 bookworm.
:: Pre-charger pour eviter les timeouts lors du Helm install et de l'initdb CNPG.
:: L'image bookworm (non alpine) est la seule supportee par CNPG.
:: chart 0.23.2 = operateur 1.25.1
podman pull ghcr.io/cloudnative-pg/cloudnative-pg:1.25.1
kind load docker-image ghcr.io/cloudnative-pg/cloudnative-pg:1.25.1 --name ecommerce
podman pull ghcr.io/cloudnative-pg/postgresql:16.6-bookworm
kind load docker-image ghcr.io/cloudnative-pg/postgresql:16.6-bookworm --name ecommerce
:: PgBouncer image utilisee par le Pooler CNPG (version distincte de l'operateur)
podman pull ghcr.io/cloudnative-pg/pgbouncer:1.23.0
kind load docker-image ghcr.io/cloudnative-pg/pgbouncer:1.23.0 --name ecommerce

:: ------------------------------------------------------------------
echo.
echo [3/5] Images observabilite...
:: ------------------------------------------------------------------
podman pull otel/opentelemetry-collector-contrib:0.153.0
kind load docker-image otel/opentelemetry-collector-contrib:0.153.0 --name ecommerce
podman pull jaegertracing/all-in-one:1.76.0
kind load docker-image jaegertracing/all-in-one:1.76.0 --name ecommerce
podman pull prom/prometheus:v3.11.3
kind load docker-image prom/prometheus:v3.11.3 --name ecommerce
podman pull grafana/grafana:13.0.1-security-01
kind load docker-image grafana/grafana:13.0.1-security-01 --name ecommerce
podman pull prometheuscommunity/postgres-exporter:v0.16.0
kind load docker-image prometheuscommunity/postgres-exporter:v0.16.0 --name ecommerce
podman pull registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0
kind load docker-image registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0 --name ecommerce
podman pull quay.io/prometheus/node-exporter:v1.9.1
kind load docker-image quay.io/prometheus/node-exporter:v1.9.1 --name ecommerce

:: ------------------------------------------------------------------
echo.
echo [3b/5] Images KEDA + Metrics Server...
:: ------------------------------------------------------------------
:: Pre-charger les images KEDA dans Kind AVANT pulumi up.
:: Sans pre-chargement, Kind tire depuis ghcr.io pendant le Helm install
:: => timeout "context deadline exceeded" sur connexions lentes.
:: Les trois images correspondent aux composants installes par le chart KEDA 2.17.0.
podman pull ghcr.io/kedacore/keda:2.17.0
kind load docker-image ghcr.io/kedacore/keda:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-metrics-apiserver:2.17.0
kind load docker-image ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-admission-webhooks:2.17.0
kind load docker-image ghcr.io/kedacore/keda-admission-webhooks:2.17.0 --name ecommerce

:: Metrics Server (chart 3.12.2 = app v0.7.2)
:: Gere automatiquement par Pulumi (MetricsServerResources.cs) — image pre-chargee ici
:: pour eviter les pulls depuis registry.k8s.io pendant pulumi up (connexions lentes).
:: Sur Kind, --kubelet-insecure-tls est active via Pulumi.dev.yaml (metricsServer:kubeletInsecureTls).
podman pull registry.k8s.io/metrics-server/metrics-server:v0.7.2
kind load docker-image registry.k8s.io/metrics-server/metrics-server:v0.7.2 --name ecommerce

:: ------------------------------------------------------------------
echo.
echo [4/5] Build et chargement des images applicatives (versioning SemVer + SHA)...
:: ------------------------------------------------------------------
:: build-images.ps1 calcule un tag {SemVer}-{SHA-par-service} (VERSION + git log),
:: build + kind load chaque image, et pousse le tag dans Pulumi config (xxxApi:image).
:: Seul un service réellement modifié change de tag → ArgoCD ne redéploie que lui.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-images.ps1"
if errorlevel 1 ( echo ERREUR : build-images.ps1 a echoue & exit /b 1 )

:: ------------------------------------------------------------------
echo.
echo [5/5] Deploiement Pulumi...
:: ------------------------------------------------------------------
pushd infra\Ecommerce.Infra
pulumi up --yes
if errorlevel 1 ( popd & echo ERREUR : pulumi up a echoue & exit /b 1 )
popd

echo.
echo ==============================================
echo  Deploiement termine !
echo  Gateway    -^> http://localhost:30080
echo  Grafana    -^> http://localhost:30030
echo  Jaeger     -^> http://localhost:30686
echo  Argo CD    -^> kubectl port-forward -n argocd svc/argocd-server 8080:80
echo                    then http://localhost:8080
echo  HPA        -^> kubectl get hpa -n ecommerce  (metriques CPU via Metrics Server)
echo  Top        -^> kubectl top pods -n ecommerce
echo ==============================================

endlocal