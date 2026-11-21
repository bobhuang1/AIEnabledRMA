# Build and run AIEnabledRma.Rag.
#
# Base images are pinned by digest (refresh them deliberately, not by accident) and the restore is
# locked to the committed packages.lock.json files. The runtime runs as the image's non-root user
# and is meant to run with a read-only root filesystem (docker-compose.prod.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/packages.lock.json src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/packages.lock.json src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Privacy/AIEnabledRma.Privacy.csproj src/AIEnabledRma.Privacy/packages.lock.json src/AIEnabledRma.Privacy/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/packages.lock.json src/AIEnabledRma.Rag/
RUN dotnet restore src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj --locked-mode

COPY . .
RUN dotnet publish src/AIEnabledRma.Rag -c Release -o /app --no-restore

# This host reads the corpus from its content root, so a publish that lost it starts cleanly and
# answers "no evidence" forever. The web image has had this guard since the corpus first went
# missing; the RAG image is the other host that reads it, and it had none.
RUN test -f /app/knowledge-base/REVIEWED.sha256 \
    && find /app/knowledge-base -name '*.md' | grep -q . \
    || (echo "knowledge-base is missing from /app: the publish output carried no corpus" >&2; exit 1)


FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The commit this image was built from, compared with the checkout by scripts/verify-images.sh.
ARG GIT_REVISION=unknown
LABEL org.opencontainers.image.revision=$GIT_REVISION

USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "AIEnabledRma.Rag.dll"]
