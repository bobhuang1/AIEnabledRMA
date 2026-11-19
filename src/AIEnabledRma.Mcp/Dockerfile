# Build and run the MCP tool server.
#
# The tool server is headless, so the same SDK-build/publish/runtime split as the web host
# applies. The knowledge base ships in because any host that reads it expects it under the
# content root; the publish list lives in the .csproj, and PublishLayoutTests fails if the
# articles stop reaching the output.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Ai/AIEnabledRma.Ai.csproj src/AIEnabledRma.Ai/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/
COPY src/AIEnabledRma.Mcp/AIEnabledRma.Mcp.csproj src/AIEnabledRma.Mcp/
RUN dotnet restore src/AIEnabledRma.Mcp/AIEnabledRma.Mcp.csproj

COPY . .
RUN dotnet publish src/AIEnabledRma.Mcp -c Release -o /app --no-restore

# The corpus is part of the image, not an optional mount. A host that starts without it answers every
# question as though the corpus said nothing, so fail the build here instead of shipping that — the
# publish step alone does not: a project that stops declaring the content still publishes cleanly.
RUN test -f /app/knowledge-base/REVIEWED.sha256 \
    && find /app/knowledge-base -name '*.md' | grep -q . \
    || (echo "knowledge-base is missing from /app: the publish output carried no corpus" >&2; exit 1)

# The ASP.NET runtime, not the plain one: the same binary serves the streamable HTTP transport,
# which needs the shared framework the stdio path never touches. stdio on this image behaves
# exactly as before — it simply carries a framework it does not use.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

USER $APP_UID
ENTRYPOINT ["dotnet", "AIEnabledRma.Mcp.dll"]
