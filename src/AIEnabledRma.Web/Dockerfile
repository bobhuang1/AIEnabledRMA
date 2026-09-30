# Build and run the customer-facing wizard.
#
# Multi-stage so the runtime layer carries no SDK. The knowledge base is copied in as content
# because the RAG index reads it from disk relative to the content root.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the manifests alone first, so a source-only change does not re-resolve.
COPY Directory.Build.props Directory.Packages.props ./
COPY AIEnabledRma.slnx ./
COPY src/AIEnabledRma.Domain/AIEnabledRma.Domain.csproj src/AIEnabledRma.Domain/
COPY src/AIEnabledRma.Data/AIEnabledRma.Data.csproj src/AIEnabledRma.Data/
COPY src/AIEnabledRma.Ai/AIEnabledRma.Ai.csproj src/AIEnabledRma.Ai/
COPY src/AIEnabledRma.Rag/AIEnabledRma.Rag.csproj src/AIEnabledRma.Rag/
COPY src/AIEnabledRma.Web/AIEnabledRma.Web.csproj src/AIEnabledRma.Web/
RUN dotnet restore src/AIEnabledRma.Web/AIEnabledRma.Web.csproj

COPY . .
RUN dotnet publish src/AIEnabledRma.Web -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# The app is not root. The image carries no writable state the process needs, so the whole
# filesystem can stay read-only apart from /tmp.
USER $APP_UID
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "AIEnabledRma.Web.dll"]
