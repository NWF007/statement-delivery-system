# ---- build ---------------------------------------------------------------------------------
# Pinned to the feature band global.json asks for - see the note in the service Dockerfiles.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Migrations/Db.Migrator/Db.Migrator.csproj src/Migrations/Db.Migrator/
RUN dotnet restore src/Migrations/Db.Migrator/Db.Migrator.csproj

COPY . .
RUN dotnet publish src/Migrations/Db.Migrator/Db.Migrator.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---- runtime -------------------------------------------------------------------------------
# The base runtime, not aspnet: the migrator is a console application that runs once and exits.
# There is no reason for it to carry a web server it will never start.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

ENV DOTNET_EnableDiagnostics=0

# No HEALTHCHECK and no EXPOSE. This container is a one-shot job: it applies migrations, exits 0,
# and everything else in the stack waits on `service_completed_successfully`. A health check on a
# container that is supposed to exit would report the successful case as a failure.
ENTRYPOINT ["dotnet", "Db.Migrator.dll"]
