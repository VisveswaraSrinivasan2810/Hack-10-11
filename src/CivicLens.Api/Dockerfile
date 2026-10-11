FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY CivicLens.sln ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore src/CivicLens.Api/CivicLens.Api.csproj
RUN dotnet publish src/CivicLens.Api/CivicLens.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "CivicLens.Api.dll"]
