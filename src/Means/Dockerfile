FROM node:22-alpine AS frontend
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci --include=dev
COPY web/ ./
RUN ./node_modules/.bin/tsc -b && ./node_modules/.bin/vite build --outDir /tmp/web-dist

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
ARG ENABLE_MEANS_OPENTELEMETRY=false
ARG TARGETARCH
WORKDIR /src
RUN apk add --no-cache clang build-base zlib-dev
COPY ["src/Means.Core/Means.Core.csproj", "src/Means.Core/"]
COPY ["src/Means.Infrastructure.XlFs/Means.Infrastructure.XlFs.csproj", "src/Means.Infrastructure.XlFs/"]
COPY ["src/Means.Protocol.S3/Means.Protocol.S3.csproj", "src/Means.Protocol.S3/"]
COPY ["src/Means/Means.csproj", "src/Means/"]
RUN case "${TARGETARCH:-amd64}" in \
        amd64) echo linux-musl-x64 > /tmp/rid ;; \
        arm64) echo linux-musl-arm64 > /tmp/rid ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && dotnet restore "src/Means/Means.csproj" -r "$(cat /tmp/rid)" /p:EnableMeansOpenTelemetry="$ENABLE_MEANS_OPENTELEMETRY"
COPY . .
WORKDIR "/src/src/Means"
RUN dotnet publish "./Means.csproj" \
    -c "$BUILD_CONFIGURATION" \
    -r "$(cat /tmp/rid)" \
    -o /app/publish \
    --self-contained true \
    /p:PublishAot=true \
    /p:EnableMeansOpenTelemetry="$ENABLE_MEANS_OPENTELEMETRY" \
    /p:StripSymbols=true \
    /p:InvariantGlobalization=true \
    /p:IlcOptimizationPreference=Size
RUN rm -rf /app/publish/wwwroot \
    && find /app/publish -type f \( -name "*.dbg" -o -name "*.pdb" \) -delete \
    && rm -f /app/publish/appsettings.Development.json
COPY --from=frontend /tmp/web-dist/ /app/publish/wwwroot/

FROM alpine:3.23 AS runtime-root
ARG TARGETARCH
RUN apk add --no-cache ca-certificates libcrypto3 libssl3 \
    && mkdir -p /scratch-root/app /scratch-root/data /scratch-root/tmp /scratch-root/etc/ssl/certs /scratch-root/lib /scratch-root/usr/lib \
    && cp /etc/ssl/certs/ca-certificates.crt /scratch-root/etc/ssl/certs/ \
    && cp /usr/lib/libcrypto.so.3 /usr/lib/libssl.so.3 /scratch-root/usr/lib/ \
    && case "${TARGETARCH:-amd64}" in \
        amd64) cp /lib/ld-musl-x86_64.so.1 /scratch-root/lib/ ;; \
        arm64) cp /lib/ld-musl-aarch64.so.1 /scratch-root/lib/ ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac \
    && printf 'app:x:1654:1654:Means app:/app:/sbin/nologin\n' > /scratch-root/etc/passwd \
    && printf 'app:x:1654:\n' > /scratch-root/etc/group \
    && chown -R 1654:1654 /scratch-root/app /scratch-root/data \
    && chmod 1777 /scratch-root/tmp

FROM scratch AS final
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true \
    SSL_CERT_FILE=/etc/ssl/certs/ca-certificates.crt \
    HOME=/app
WORKDIR /app
COPY --from=runtime-root /scratch-root/ /
COPY --from=build --chown=1654:1654 /app/publish .
USER 1654:1654
EXPOSE 8080
EXPOSE 8081
ENTRYPOINT ["./Means"]
