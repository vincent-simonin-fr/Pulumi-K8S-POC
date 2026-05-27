# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Ecommerce.sln ./
COPY src/Gateway/Ecommerce.Gateway/Ecommerce.Gateway.csproj src/Gateway/Ecommerce.Gateway/

RUN dotnet restore src/Gateway/Ecommerce.Gateway/Ecommerce.Gateway.csproj

COPY src/Gateway/ src/Gateway/

RUN dotnet publish src/Gateway/Ecommerce.Gateway/Ecommerce.Gateway.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2: Runtime ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Ecommerce.Gateway.dll"]
