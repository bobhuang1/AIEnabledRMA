# The operator tool as an image.
#
# The production pack's postgres publishes no port, so the nightly reconciliation sweep cannot run
# from the host's crontab: it runs on the pack's network, as a one-shot container that exits when the
# sweep is done. This is that container's image, and nothing else uses it — the web, mcp and rag
# services each have their own, because each one carries a different set of runtime dependencies.
#
# Base images are pinned by digest, the same pair the web image uses: a tag names whatever was
# published most recently, and an image that changes without a commit is not reproducible.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src

# Restore against the manifests and their lock files first, so a source-only change does not
# re-resolve, and --locked-mode makes a dependency change that was not committed fail this build.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/packages.lock.json src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/packages.lock.json src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Ai/AIEnabledRma.Ai.csproj src/AIEnabledRma.Ai/packages.lock.json src/AIEnabledRma.Ai/
COPY src/AIEnabledRma.Privacy/AIEnabledRma.Privacy.csproj src/AIEnabledRma.Privacy/packages.lock.json src/AIEnabledRma.Privacy/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/packages.lock.json src/AIEnabledRma.Rag/
COPY tools/DbAdmin/AIEnabledRma.Tools.DbAdmin.csproj tools/DbAdmin/packages.lock.json tools/DbAdmin/
RUN dotnet restore tools/DbAdmin/AIEnabledRma.Tools.DbAdmin.csproj --locked-mode

COPY . .
RUN dotnet publish tools/DbAdmin -c Release -o /app --no-restore

# The runtime layer carries no SDK. DbAdmin is a console tool, but it is built against
# Microsoft.AspNetCore.App (configuration and hosting come from there), so aspnet is the smaller of
# the two runtime images that can start it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The commit this image was built from, so "was last night's sweep running the fix" is a question the
# image answers rather than a question about which build produced it.
ARG GIT_REVISION=unknown
LABEL org.opencontainers.image.revision=$GIT_REVISION

# Not root, and the tool writes nothing to disk: its output is the review items it files.
USER $APP_UID

ENTRYPOINT ["dotnet", "AIEnabledRma.Tools.DbAdmin.dll"]
