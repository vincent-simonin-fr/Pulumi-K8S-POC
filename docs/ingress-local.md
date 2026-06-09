# Ingress en local (Kind) — runbook

Monter l'**Ingress nginx + TLS** sur Kind, comme en prod, mais avec deux adaptations locales
(Kind n'a ni LoadBalancer ni DNS public) :

- **`ingress:nginxHostPort=true`** → nginx bind **80/443 sur le nœud** (au lieu d'un Service
  LoadBalancer qui resterait `<pending>`), exposé sur `localhost` via les extraPortMappings.
- **`ingress:selfSigned=true`** → ClusterIssuer **auto-signé** (au lieu de Let's Encrypt, qui
  exige un domaine public). Le HTTPS marche ; le navigateur avertit (cert non vérifiable) — normal.

> Domaines routés : `wizzz.com` (gateway), `grafana.wizzz.com`, `jaeger.wizzz.com`, et
> `vault.wizzz.com` si Vault est activé. Vault reste joignable par le provider via son NodePort
> `localhost:30820` (on ne change pas `vault:providerAddress` ici).

## 1. Recréer le cluster avec les ports 80/443

```bash
kind delete cluster --name ecommerce
set KIND_EXPERIMENTAL_PROVIDER=podman
kind create cluster --name ecommerce --config kind-config-prodlocal.yaml
kubectl config use-context kind-ecommerce
```

> ⚠️ **Si tu avais déjà un déploiement dev**, recréer le cluster **orpheline l'état Pulumi**
> (il croit que les ressources existent → erreurs `services "..." not found` au `pulumi up`).
> Réinitialise l'état (et retire les creds Vault caduques de l'ancienne instance) :
> ```bash
> cd infra/Ecommerce.Infra
> pulumi cancel --yes 2>$null            # si un up est resté bloqué
> cp Pulumi.dev.yaml Pulumi.dev.yaml.bak # PowerShell : Copy-Item …
> pulumi stack rm dev --force
> mv -f Pulumi.dev.yaml.bak Pulumi.dev.yaml
> pulumi config rm vault:rootToken; pulumi config rm vault:approleRoleId; pulumi config rm vault:approleSecretId
> pulumi stack init dev
> ```
> (Vault redémarrera scellé/non configuré → les apps tournent sur les secrets statiques ;
> tu pourras le bootstrapper après. Cf. [vault.md](vault.md).)

## 2. Précharger / construire les images

```bash
dotnet nuke PreloadImages
dotnet nuke BuildImages
```

## 3. Activer l'Ingress local (sur le stack dev)

```bash
cd infra/Ecommerce.Infra
pulumi config set ingress:enabled       true
pulumi config set ingress:selfSigned    true
pulumi config set ingress:nginxHostPort true
pulumi up --yes
```

> `ingress:enabled=true` bascule gateway/Grafana/Jaeger en **ClusterIP** (l'Ingress route vers
> eux). Tu perds l'accès NodePort `localhost:30080/30030/30686` au profit des hostnames ci-dessous.

Si Vault est activé, refais le bootstrap (init/unseal + token) — cf. [vault.md](vault.md).

## 4. Faire résoudre les domaines vers localhost

Ajouter au fichier hosts (Windows : `C:\Windows\System32\drivers\etc\hosts`, en admin ;
Linux/mac : `/etc/hosts`) :

```
127.0.0.1  wizzz.com grafana.wizzz.com jaeger.wizzz.com vault.wizzz.com argocd.wizzz.com argocd-grpc.wizzz.com
```

## 5. Vérifier

```bash
kubectl get pods -n ingress           # controller nginx Running (1 réplica)
kubectl get ingress -A                # gateway / grafana / jaeger (+ vault)
kubectl get certificate -A            # READY=True (émis par l'issuer self-signed)

# -k : accepte le cert auto-signé
curl -k https://wizzz.com/health             # 200 (gateway)
curl -k https://grafana.wizzz.com/api/health # nécessite le basic-auth si configuré
```

Dans le navigateur : `https://grafana.wizzz.com` → avertissement de sécurité (cert auto-signé)
→ « continuer » → tu vois le routage par hostname + la terminaison TLS fonctionner, comme en prod.

## Revenir au mode dev (NodePort)

```bash
cd infra/Ecommerce.Infra
pulumi config set ingress:enabled       false
pulumi config set ingress:selfSigned    false
pulumi config set ingress:nginxHostPort false
pulumi up --yes
# (le cluster garde les ports 80/443 mappés ; sans Ingress ils sont juste inutilisés.
#  Pour revenir au kind-config standard : recréer le cluster avec kind-config.yaml.)
```

## Ce que ça reproduit de la prod (et ce que ça ne reproduit pas)

| Reproduit en local | NON reproduit (cloud only) |
|---|---|
| Ingress nginx + routage L7 par hostname | LoadBalancer / IP publique |
| Terminaison **TLS** (cert self-signed) | **Let's Encrypt** (certs vérifiables) |
| 1 point d'entrée (80/443) pour N services | DNS public réel |
| basic-auth nginx (Grafana/Jaeger) | auto-unseal KMS, stockage réseau (gp3) |
