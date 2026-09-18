# Build context must be the clean linux-x64 output of dotnet publish, not the repository.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chown=1654:1654 . ./
ENV ASPNETCORE_URLS=https://+:8443 \
    INVENTORY_DATA_DIR=/data \
    INVENTORY_BACKUP_DIR=/backups \
    DOTNET_EnableDiagnostics=0
EXPOSE 8443
USER 1654:1654
ENTRYPOINT ["dotnet", "RemoteAssist.Inventory.Server.dll"]
