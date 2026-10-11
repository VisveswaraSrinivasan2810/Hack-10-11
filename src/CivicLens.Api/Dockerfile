FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY src/CivicLens.Domain/CivicLens.Domain.csproj src/CivicLens.Domain/
COPY src/CivicLens.Application/CivicLens.Application.csproj src/CivicLens.Application/
COPY src/CivicLens.Infrastructure/CivicLens.Infrastructure.csproj src/CivicLens.Infrastructure/
COPY src/CivicLens.Api/CivicLens.Api.csproj src/CivicLens.Api/
RUN dotnet restore src/CivicLens.Api/CivicLens.Api.csproj
COPY src/ src/
RUN dotnet publish src/CivicLens.Api/CivicLens.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /app/uploads && chown app:app /app/uploads
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CivicLens.Api.dll"]
