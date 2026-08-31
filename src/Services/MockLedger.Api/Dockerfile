# ---- build ---------------------------------------------------------------------------------
# Same pinned feature band as every other Dockerfile; see Generation.Worker/Dockerfile for why
# the moving `10.0` tag is a time bomb.
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# The mock's restore closure is deliberately tiny: it references nothing in this repository.
# That is the point of the service - a resilience policy tested against an in-process stub is
# not tested, and a mock that shares code with its caller is a self-agreement test.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Services/MockLedger.Api/MockLedger.Api.csproj src/Services/MockLedger.Api/
RUN dotnet restore src/Services/MockLedger.Api/MockLedger.Api.csproj

COPY . .
RUN dotnet publish src/Services/MockLedger.Api/MockLedger.Api.csproj     -c Release     -o /app/publish     --no-restore

# ---- runtime -------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app/publish .

USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080     DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "MockLedger.Api.dll"]
