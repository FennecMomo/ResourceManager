FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish ResourceManager.Server/ResourceManager.Server.csproj -c Release --self-contained false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/resourcemanager && chown -R $APP_UID:$APP_UID /var/lib/resourcemanager
USER $APP_UID
ENV RM_DATA_DIR=/var/lib/resourcemanager RM_API_ADDRESS=0.0.0.0 RM_DISCOVERY_ENABLED=false
EXPOSE 37644
ENTRYPOINT ["dotnet", "ResourceManager.Server.dll"]
