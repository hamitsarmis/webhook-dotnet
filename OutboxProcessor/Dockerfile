FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY ["OutboxProcessor.csproj", "nuget.config", "./"]
RUN dotnet dev-certs https --clean 2>/dev/null; \
    DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER=0 \
    NUGET_CERT_REVOCATION_MODE=offline \
    SSL_CERT_DIR=/dev/null \
    dotnet restore --disable-parallel \
    || ( \
      sed -i 's|https://api.nuget.org|http://api.nuget.org|g' nuget.config && \
      dotnet restore --disable-parallel \
    )
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "OutboxProcessor.dll"]
