# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Fichiers de configuration MSBuild/NuGet (doivent précéder le restore)
COPY Directory.Build.props ./
COPY NuGet.config ./
COPY global.json ./

COPY src/Gateway/Ecommerce.Gateway/Ecommerce.Gateway.csproj src/Gateway/Ecommerce.Gateway/

RUN dotnet restore src/Gateway/Ecommerce.Gateway/Ecommerce.Gateway.csproj

COPY src/Gateway/ src/Gateway/

RUN dotnet publish src/Gateway/Ecommerce.Gateway/Ecommerce.Gateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2: Runtime ───────────────────────────────────────────────────────────
# noble = Ubuntu 24.04 complet (addgroup/adduser disponibles)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

COPY --from=build --chown=app:app /app/publish .

USER app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Ecommerce.Gateway.dll"]
