FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/shared/FlowForge.Domain/FlowForge.Domain.csproj src/shared/FlowForge.Domain/
COPY src/shared/FlowForge.Contracts/FlowForge.Contracts.csproj src/shared/FlowForge.Contracts/
COPY src/shared/FlowForge.Infrastructure/FlowForge.Infrastructure.csproj src/shared/FlowForge.Infrastructure/
COPY src/services/FlowForge.WebApi/FlowForge.WebApi.csproj src/services/FlowForge.WebApi/
RUN dotnet restore src/services/FlowForge.WebApi/FlowForge.WebApi.csproj

COPY src/ src/
RUN dotnet publish src/services/FlowForge.WebApi/FlowForge.WebApi.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "FlowForge.WebApi.dll"]
