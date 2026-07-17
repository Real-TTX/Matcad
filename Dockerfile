# syntax=docker/dockerfile:1

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-local
WORKDIR /src
COPY src/Matcat/Matcat.csproj src/Matcat/
RUN dotnet restore src/Matcat/Matcat.csproj
COPY src/ src/
RUN dotnet publish src/Matcat/Matcat.csproj -c Release -o /app \
    -p:InformationalVersion=${VERSION}

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Data volume holds the SQLite database + JSON configs.
VOLUME ["/data"]
EXPOSE 4433
ENV ASPNETCORE_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "Matcat.dll"]
