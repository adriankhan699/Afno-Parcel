# syntax=docker/dockerfile:1
# Build context: repository root (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached layer) using only the files that affect restore.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/NepalParcel.Domain/NepalParcel.Domain.csproj src/NepalParcel.Domain/
COPY src/NepalParcel.Contracts/NepalParcel.Contracts.csproj src/NepalParcel.Contracts/
COPY src/NepalParcel.Application/NepalParcel.Application.csproj src/NepalParcel.Application/
COPY src/NepalParcel.Infrastructure/NepalParcel.Infrastructure.csproj src/NepalParcel.Infrastructure/
COPY src/NepalParcel.API/NepalParcel.API.csproj src/NepalParcel.API/
RUN dotnet restore src/NepalParcel.API/NepalParcel.API.csproj

COPY src/ src/
RUN dotnet publish src/NepalParcel.API/NepalParcel.API.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app/publish .
# Proofs of delivery (signatures and photos) are kept here, in private storage that is never served as static files.
# Mount a volume on it so they survive new containers. The aspnet image ships a non-root 'app' user.
RUN mkdir -p /var/afno/proofs && chown -R $APP_UID /var/afno
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "NepalParcel.API.dll"]
