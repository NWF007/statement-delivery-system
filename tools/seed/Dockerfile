# ---- build ---------------------------------------------------------------------------------
# Pinned to the feature band global.json asks for - see the note in the service Dockerfiles.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/BuildingBlocks/Domain/Domain.csproj                   src/BuildingBlocks/Domain/
COPY src/BuildingBlocks/Crypto/Crypto.csproj                   src/BuildingBlocks/Crypto/
COPY src/BuildingBlocks/Contracts/Contracts.csproj             src/BuildingBlocks/Contracts/
COPY src/BuildingBlocks/Messaging/Messaging.csproj             src/BuildingBlocks/Messaging/
COPY src/BuildingBlocks/Persistence/Persistence.csproj         src/BuildingBlocks/Persistence/
COPY src/BuildingBlocks/ServiceDefaults/ServiceDefaults.csproj src/BuildingBlocks/ServiceDefaults/
COPY tools/seed/SeedTool.csproj                                tools/seed/
RUN dotnet restore tools/seed/SeedTool.csproj

COPY . .
RUN dotnet publish tools/seed/SeedTool.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---- runtime -------------------------------------------------------------------------------
# The base runtime, not aspnet: the seed is a console application that runs once and exits, the
# same shape as the migrator. Chiseled and non-root for the same reasons as every other image.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENV DOTNET_EnableDiagnostics=0

# No HEALTHCHECK and no EXPOSE. One-shot: it seeds, drives one generation run through the API,
# prints the demo ids, and exits 0. Compose runs it after the services are healthy.
ENTRYPOINT ["dotnet", "SeedTool.dll"]
CMD ["--demo"]
