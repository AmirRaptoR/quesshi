# Build and run Quesshi. One image: the ASP.NET host serves the API, the Blazor bundle and the
# Orleans silo, so there is nothing else to containerise.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, so a change to source does not re-download every package.
COPY src/Quesshi.Domain/*.csproj                 src/Quesshi.Domain/
COPY src/Quesshi.Application/*.csproj            src/Quesshi.Application/
COPY src/Quesshi.Infrastructure/*.csproj         src/Quesshi.Infrastructure/
COPY src/Quesshi.Grains.Abstractions/*.csproj    src/Quesshi.Grains.Abstractions/
COPY src/Quesshi.Grains/*.csproj                 src/Quesshi.Grains/
COPY src/Quesshi.Shared/*.csproj                 src/Quesshi.Shared/
COPY src/Quesshi.Web/*.csproj                    src/Quesshi.Web/
COPY src/Quesshi.Server/*.csproj                 src/Quesshi.Server/
RUN dotnet restore src/Quesshi.Server/Quesshi.Server.csproj

COPY src/ src/
RUN dotnet publish src/Quesshi.Server/Quesshi.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Uploaded media and pictures fetched from Wikipedia are bind-mounted over these, so they must
# exist and be writable by the user the container runs as.
RUN mkdir -p wwwroot/media/uploads wwwroot/media/sourced && chown -R 1000:1000 /app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["./Quesshi.Server"]
