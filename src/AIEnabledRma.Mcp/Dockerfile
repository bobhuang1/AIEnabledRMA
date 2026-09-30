# Build and run the MCP tool server.
#
# The tool server is headless, so the same SDK-build/publish/runtime split as the web host
# applies. The knowledge base ships in because the retriever reads it from disk.
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

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

USER $APP_UID
ENTRYPOINT ["dotnet", "AIEnabledRma.Mcp.dll"]
