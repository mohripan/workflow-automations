FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/shared/FlowForge.Domain/FlowForge.Domain.csproj src/shared/FlowForge.Domain/
COPY src/shared/FlowForge.Contracts/FlowForge.Contracts.csproj src/shared/FlowForge.Contracts/
COPY src/shared/FlowForge.Infrastructure/FlowForge.Infrastructure.csproj src/shared/FlowForge.Infrastructure/
COPY src/services/FlowForge.WorkflowEngine/FlowForge.WorkflowEngine.csproj src/services/FlowForge.WorkflowEngine/
COPY src/services/FlowForge.WorkflowHost/FlowForge.WorkflowHost.csproj src/services/FlowForge.WorkflowHost/
RUN dotnet restore src/services/FlowForge.WorkflowHost/FlowForge.WorkflowHost.csproj
RUN dotnet restore src/services/FlowForge.WorkflowEngine/FlowForge.WorkflowEngine.csproj

COPY src/ src/
RUN dotnet publish src/services/FlowForge.WorkflowHost/FlowForge.WorkflowHost.csproj \
    -c Release -o /app/publish --no-restore
RUN dotnet publish src/services/FlowForge.WorkflowEngine/FlowForge.WorkflowEngine.csproj \
    -c Release -o /app/engine --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/engine /app/engine/

# Install Python 3 and create an isolated venv at /app/venv.
# Using a venv sidesteps the PEP 668 "externally managed environment"
# restriction present in Debian 12+ images — pip inside a venv is always
# allowed. Workflow scripts should use /app/venv/bin/python3 as their
# interpreter so that `python3 -m pip install` works at job runtime.
RUN apt-get update && apt-get install -y --no-install-recommends \
        python3 python3-venv && \
    python3 -m venv /app/venv && \
    rm -rf /var/lib/apt/lists/*

# Ship example workflow scripts so they are available at a known path inside
# the container without requiring a volume mount.
COPY examples/ /app/examples/

EXPOSE 8080
ENTRYPOINT ["dotnet", "FlowForge.WorkflowHost.dll"]
