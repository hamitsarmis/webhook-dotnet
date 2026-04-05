FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl && \
    update-ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY ["OutboxProcessor.csproj", "nuget.config", "./"]
RUN DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0 dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "OutboxProcessor.dll"]
