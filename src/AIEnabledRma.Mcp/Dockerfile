# Build and run the MCP tool server.
#
# The tool server is headless, so the same SDK-build/publish/runtime split as the web host
# applies. Unlike the web host it carries no knowledge-base/: its tools evaluate stored data through
# the repositories, nothing in this process reads the corpus, and the startup check that inspects one
# is registered by AddRag, which this host does not call. Shipping the articles anyway made "the
# image carries the corpus" a true statement about bytes and a false one about behaviour.
# Base images are pinned by digest: a tag names whatever was published most recently, and an
# image that changes without a commit is not reproducible. Refresh deliberately.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS build
WORKDIR /src

# The manifests and their lock files, restored in locked mode: an uncommitted dependency change
# fails this build instead of resolving packages the suite never tested (the RAG image already did
# this; these two did not).
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/packages.lock.json src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/packages.lock.json src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Ai/AIEnabledRma.Ai.csproj src/AIEnabledRma.Ai/packages.lock.json src/AIEnabledRma.Ai/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/packages.lock.json src/AIEnabledRma.Rag/
COPY src/AIEnabledRma.Mcp/AIEnabledRma.Mcp.csproj src/AIEnabledRma.Mcp/packages.lock.json src/AIEnabledRma.Mcp/
RUN dotnet restore src/AIEnabledRma.Mcp/AIEnabledRma.Mcp.csproj --locked-mode

COPY . .
RUN dotnet publish src/AIEnabledRma.Mcp -c Release -o /app --no-restore

# The ASP.NET runtime, not the plain one: the same binary serves the streamable HTTP transport,
# which needs the shared framework the stdio path never touches. stdio on this image behaves
# exactly as before — it simply carries a framework it does not use.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The commit this image was built from. scripts/verify-images.sh passes it and compares the label
# with the checkout, so "is this image from before or after the fix" is a question the image answers
# instead of a question about which build produced it.
ARG GIT_REVISION=unknown
LABEL org.opencontainers.image.revision=$GIT_REVISION

USER $APP_UID
ENTRYPOINT ["dotnet", "AIEnabledRma.Mcp.dll"]
