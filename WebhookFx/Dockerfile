FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY WebhookFx.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/logs
EXPOSE 8089
ENTRYPOINT ["dotnet", "WebhookFx.dll"]
