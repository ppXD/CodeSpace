# Production CodeSpace API/worker image. A multi-stage publish: build + publish CodeSpace.Api, which (via the
# CopyMcpProxyToOutput / PublishMcpProxy targets in CodeSpace.Api.csproj) ALSO lands the codespace-mcp proxy binary
# next to the API assembly — i.e. at LocalProcessRunner.McpProxyBinaryPath(). So an image built here has the MCP tool
# fabric ready out of the box; the worker enables it via the env flags wired in docker-compose.yml (default-ON there).
#
#   docker build -f backend/Dockerfile -t codespace-api .        # build context = backend/
#
# git is installed at runtime because the agent workspace layer clones bound repos with the git CLI. The agent
# harness CLIs (claude / codex) are NOT baked in here — an operator who runs real CLI harnesses layers them on or
# repoints CODESPACE_CLAUDE_CODE_PATH / the codex equivalent; without them the MCP endpoint still serves, the harness
# just won't launch (a clean failed run, surfaced like any missing-binary).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore CodeSpace.sln
# Publishing the API runs PublishMcpProxy → codespace-mcp lands in /app alongside CodeSpace.Api.dll.
RUN dotnet publish src/CodeSpace.Api/CodeSpace.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# git: the workspace layer clones bound repos with the git CLI.
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
# codespace-mcp ships as an executable apphost; ensure it is launchable.
RUN chmod +x ./codespace-mcp || true
EXPOSE 8080
ENTRYPOINT ["dotnet", "CodeSpace.Api.dll"]
