# ---- build ---------------------------------------------------------------------------------
# PINNED TO THE FEATURE BAND global.json ASKS FOR, and that is not fussiness.
# global.json pins 10.0.400 with rollForward: latestPatch, which rolls forward within 10.0.4xx and
# NOT across feature bands. The moving `10.0` tag resolves to 10.0.400 today, so it would work -
# right up until 10.0.5xx ships, at which point every container build starts failing with
# "a compatible .NET SDK was not found" while local builds carry on fine. Bump both together.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# Manifests first, so a source-only change reuses the restore layer. Copied one project at a time
# with their directory structure intact - a flattened copy loses the relative paths that the
# ProjectReference elements resolve against, and restore fails.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/BuildingBlocks/Domain/Domain.csproj                   src/BuildingBlocks/Domain/
COPY src/BuildingBlocks/Contracts/Contracts.csproj             src/BuildingBlocks/Contracts/
COPY src/BuildingBlocks/Messaging/Messaging.csproj             src/BuildingBlocks/Messaging/
COPY src/BuildingBlocks/Persistence/Persistence.csproj         src/BuildingBlocks/Persistence/
COPY src/BuildingBlocks/ServiceDefaults/ServiceDefaults.csproj src/BuildingBlocks/ServiceDefaults/
COPY src/Services/Generation.Worker/Generation.Worker.csproj                             src/Services/Generation.Worker/
RUN dotnet restore src/Services/Generation.Worker/Generation.Worker.csproj

COPY . .
RUN dotnet publish src/Services/Generation.Worker/Generation.Worker.csproj     -c Release     -o /app/publish     --no-restore

# ---- runtime -------------------------------------------------------------------------------
# Chiseled: no shell, no package manager, no busybox. If something does achieve remote code
# execution there is nothing in the image to execute.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .

# Non-root. $APP_UID is baked into the Microsoft base images (uid 1654); using it rather than a
# hand-rolled useradd means no shell is needed to create the account.
USER $APP_UID

# Kestrel listens on 8080 because a non-root process cannot bind a privileged port.
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080     DOTNET_EnableDiagnostics=0

# THE CONTAINER HEALTH CHECK IS THE APPLICATION PROBING ITSELF.
# A chiseled image has no curl and no shell, so `curl -f .../health/live` cannot run, and adding
# curl back would defeat the reason for choosing this base image. Instead the entry point accepts
# --healthcheck, performs one HTTP GET against its own liveness endpoint and exits 0 or 1, using
# only the runtime that is already present. See ServiceDefaults/HealthChecks/HealthCheckProbe.cs.
# In Kubernetes this is unnecessary: use an httpGet probe, performed by the kubelet from outside.
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3     CMD ["dotnet", "Generation.Worker.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "Generation.Worker.dll"]
