# Build the isolated platform host. The server project references the Blazor client, so publishing
# it produces the client shell and static-web-assets manifest served by the one host process.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY platform/ platform/
RUN dotnet restore platform/src/Quesshi.Server/Quesshi.Server.csproj
RUN dotnet publish platform/src/Quesshi.Server/Quesshi.Server.csproj \
    --configuration Release --output /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# The runtime image otherwise has no HTTP client for the Compose health check.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .
COPY platform/entrypoint.sh /app/entrypoint.sh

RUN mkdir -p /app/wwwroot/media \
 && chmod 755 /app/entrypoint.sh \
 && chown -R 1000:1000 /app

USER 1000:1000
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["/app/entrypoint.sh"]
