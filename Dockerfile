FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY MaterialLedger.sln ./
COPY MaterialLedger.Data/MaterialLedger.Data.csproj MaterialLedger.Data/
COPY MaterialLedger.Web/MaterialLedger.Web.csproj MaterialLedger.Web/
COPY MaterialLedger.Tests/MaterialLedger.Tests.csproj MaterialLedger.Tests/
RUN dotnet restore MaterialLedger.Web/MaterialLedger.Web.csproj

COPY MaterialLedger.Data/ MaterialLedger.Data/
COPY MaterialLedger.Web/ MaterialLedger.Web/
RUN dotnet publish MaterialLedger.Web/MaterialLedger.Web.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "MaterialLedger.Web.dll"]
