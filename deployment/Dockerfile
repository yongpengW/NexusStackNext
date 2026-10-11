FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS publish
WORKDIR /source
COPY . .
RUN dotnet publish src/Hosts/NexusStackNext.PlatformHost -c Release -o /out/platform \
 && dotnet publish src/Hosts/NexusStackNext.CostingHost -c Release -o /out/costing \
 && dotnet publish src/Hosts/NexusStackNext.PricingHost -c Release -o /out/pricing \
 && dotnet publish src/Gateway/NexusStackNext.Gateway -c Release -o /out/gateway

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
RUN mkdir -p /data/files /app/agile && chown -R app:app /data/files /app/agile
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID

FROM runtime AS platform
COPY --from=publish /out/platform .
ENTRYPOINT ["dotnet", "NexusStackNext.PlatformHost.dll"]

FROM runtime AS costing
COPY --from=publish /out/costing .
ENTRYPOINT ["dotnet", "NexusStackNext.CostingHost.dll"]

FROM runtime AS pricing
COPY --from=publish /out/pricing .
ENTRYPOINT ["dotnet", "NexusStackNext.PricingHost.dll"]

FROM runtime AS gateway
COPY --from=publish /out/gateway .
ENTRYPOINT ["dotnet", "NexusStackNext.Gateway.dll"]
