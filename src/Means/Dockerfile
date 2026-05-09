FROM node:22-alpine AS frontend
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci --include=dev
COPY web/ ./
RUN ./node_modules/.bin/tsc -b && ./node_modules/.bin/vite build --outDir /tmp/web-dist

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Means.Core/Means.Core.csproj", "src/Means.Core/"]
COPY ["src/Means.Infrastructure.SqliteFs/Means.Infrastructure.SqliteFs.csproj", "src/Means.Infrastructure.SqliteFs/"]
COPY ["src/Means.Infrastructure.XlFs/Means.Infrastructure.XlFs.csproj", "src/Means.Infrastructure.XlFs/"]
COPY ["src/Means.Protocol.S3/Means.Protocol.S3.csproj", "src/Means.Protocol.S3/"]
COPY ["src/Means/Means.csproj", "src/Means/"]
RUN dotnet restore "src/Means/Means.csproj"
COPY . .
WORKDIR "/src/src/Means"
RUN dotnet build "./Means.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Means.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false
RUN rm -rf /app/publish/wwwroot
COPY --from=frontend /tmp/web-dist/ /app/publish/wwwroot/

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Means.dll"]
