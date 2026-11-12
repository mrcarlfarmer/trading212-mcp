FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Trading212Mcp.Server/Trading212Mcp.Server.csproj src/Trading212Mcp.Server/
RUN dotnet restore src/Trading212Mcp.Server/Trading212Mcp.Server.csproj
COPY src/Trading212Mcp.Server/ src/Trading212Mcp.Server/
RUN dotnet publish src/Trading212Mcp.Server/Trading212Mcp.Server.csproj --no-restore -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER 10001:10001
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "Trading212Mcp.Server.dll", "--health-check"]
ENTRYPOINT ["dotnet", "Trading212Mcp.Server.dll"]
