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
echo [2/5] Images infra (postgres, rabbitmq, redis)...
:: ------------------------------------------------------------------
podman pull postgres:16-alpine
kind load docker-image postgres:16-alpine --name ecommerce
podman pull rabbitmq:4.3.1-management-alpine
kind load docker-image rabbitmq:4.3.1-management-alpine --name ecommerce
podman pull redis:7-alpine
kind load docker-image redis:7-alpine --name ecommerce

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
echo [3b/5] Images KEDA (operator + metrics-server + webhooks)...
:: ------------------------------------------------------------------
:: Pré-charger les images KEDA dans Kind AVANT pulumi up.
:: Sans pré-chargement, Kind tire depuis ghcr.io pendant le Helm install
:: → timeout "context deadline exceeded" sur connexions lentes.
:: Les trois images correspondent aux composants installés par le chart KEDA 2.17.0.
podman pull ghcr.io/kedacore/keda:2.17.0
kind load docker-image ghcr.io/kedacore/keda:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-metrics-apiserver:2.17.0
kind load docker-image ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-admission-webhooks:2.17.0
kind load docker-image ghcr.io/kedacore/keda-admission-webhooks:2.17.0 --name ecommerce

:: ------------------------------------------------------------------
echo.
echo [4/5] Build et chargement des images applicatives...
:: ------------------------------------------------------------------
podman build -f docker/order-api/Dockerfile -t localhost/ecommerce/order-api:dev .
if errorlevel 1 ( echo ERREUR : build order-api & exit /b 1 )
kind load docker-image localhost/ecommerce/order-api:dev --name ecommerce

podman build -f docker/inventory-api/Dockerfile -t localhost/ecommerce/inventory-api:dev .
if errorlevel 1 ( echo ERREUR : build inventory-api & exit /b 1 )
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce

podman build -f docker/gateway/Dockerfile -t localhost/ecommerce/gateway:dev .
if errorlevel 1 ( echo ERREUR : build gateway & exit /b 1 )
kind load docker-image localhost/ecommerce/gateway:dev --name ecommerce

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
echo  Gateway  -^> http://localhost:30080
echo  Grafana  -^> http://localhost:30030
echo  Jaeger   -^> http://localhost:30686
echo ==============================================

endlocal
