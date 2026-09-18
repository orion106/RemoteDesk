FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY RemoteAssist.Inventory/RemoteAssist.Inventory.csproj RemoteAssist.Inventory/
COPY RemoteAssist.Inventory.Server/RemoteAssist.Inventory.Server.csproj RemoteAssist.Inventory.Server/
RUN dotnet restore RemoteAssist.Inventory.Server/RemoteAssist.Inventory.Server.csproj
COPY RemoteAssist.Inventory/ RemoteAssist.Inventory/
COPY RemoteAssist.Inventory.Server/ RemoteAssist.Inventory.Server/
RUN dotnet publish RemoteAssist.Inventory.Server/RemoteAssist.Inventory.Server.csproj -c Release -o /out --no-restore --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build --chown=1654:1654 /out/ ./
ENV ASPNETCORE_URLS=https://+:8443 \
    INVENTORY_DATA_DIR=/data \
    INVENTORY_BACKUP_DIR=/backups \
    DOTNET_EnableDiagnostics=0
EXPOSE 8443
USER 1654:1654
ENTRYPOINT ["dotnet", "RemoteAssist.Inventory.Server.dll"]
