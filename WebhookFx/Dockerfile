# Build context is the repository root: WebhookFx references WebhookFx.Core.
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY WebhookFx.Core/WebhookFx.Core.csproj WebhookFx.Core/
COPY WebhookFx/WebhookFx.csproj WebhookFx/
RUN dotnet restore WebhookFx/WebhookFx.csproj
COPY WebhookFx.Core/ WebhookFx.Core/
COPY WebhookFx/ WebhookFx/
RUN dotnet publish WebhookFx/WebhookFx.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /app/logs
EXPOSE 8089
ENTRYPOINT ["dotnet", "WebhookFx.dll"]
