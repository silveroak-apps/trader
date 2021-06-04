FROM mcr.microsoft.com/dotnet/sdk:5.0-alpine AS build-env
WORKDIR /app

COPY . ./
RUN dotnet restore
RUN dotnet test --filter FullyQualifiedName\!~IntegrationTest
RUN dotnet publish -c Release -o out/Crypto.Trader --no-restore Crypto.Trader/Crypto.Trader.fsproj
RUN dotnet publish -c Release -o out/Crypto.Database.Upgrade --no-restore Crypto.Database.Upgrade/Crypto.Database.Upgrade.fsproj

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:5.0-alpine
WORKDIR /app
COPY --from=build-env /app/out .
CMD dotnet ${STARTUP_DLL} ${cli_args}

EXPOSE 80