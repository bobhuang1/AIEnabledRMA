# Build and run the MCP tool server.
#
# The tool server is headless, so the same SDK-build/publish/runtime split as the web host
# applies. Unlike the web host it carries no knowledge-base/: its tools evaluate stored data through
# the repositories, nothing in this process reads the corpus, and the startup check that inspects one
# is registered by AddRag, which this host does not call. Shipping the articles anyway made "the
# image carries the corpus" a true statement about bytes and a false one about behaviour.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Ai/AIEnabledRma.Ai.csproj src/AIEnabledRma.Ai/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/
COPY src/AIEnabledRma.Mcp/AIEnabledRma.Mcp.csproj src/AIEnabledRma.Mcp/
RUN dotnet restore src/AIEnabledRma.Mcp/AIEnabledRma.Mcp.csproj

COPY . .
RUN dotnet publish src/AIEnabledRma.Mcp -c Release -o /app --no-restore

# The ASP.NET runtime, not the plain one: the same binary serves the streamable HTTP transport,
# which needs the shared framework the stdio path never touches. stdio on this image behaves
# exactly as before — it simply carries a framework it does not use.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The commit this image was built from. scripts/verify-images.sh passes it and compares the label
# with the checkout, so "is this image from before or after the fix" is a question the image answers
# instead of a question about which build produced it.
ARG GIT_REVISION=unknown
LABEL org.opencontainers.image.revision=$GIT_REVISION

USER $APP_UID
ENTRYPOINT ["dotnet", "AIEnabledRma.Mcp.dll"]
