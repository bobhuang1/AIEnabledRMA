# Build and run AIEnabledRma.Web.
#
# Base images are pinned by digest (refresh them deliberately, not by accident) and the restore is
# locked to the committed packages.lock.json files. The runtime runs as the image's non-root user
# and is meant to run with a read-only root filesystem (docker-compose.prod.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/packages.lock.json src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/packages.lock.json src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Ai/AIEnabledRma.Ai.csproj src/AIEnabledRma.Ai/packages.lock.json src/AIEnabledRma.Ai/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/packages.lock.json src/AIEnabledRma.Rag/
COPY src/AIEnabledRma.Web/AIEnabledRma.Web.csproj src/AIEnabledRma.Web/packages.lock.json src/AIEnabledRma.Web/
RUN dotnet restore src/AIEnabledRma.Web/AIEnabledRma.Web.csproj --locked-mode

COPY . .
RUN dotnet publish src/AIEnabledRma.Web -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS runtime
WORKDIR /app
COPY --from=build /app ./

USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "AIEnabledRma.Web.dll"]
