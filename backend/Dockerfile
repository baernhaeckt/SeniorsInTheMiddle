# The forward proxy, the WebAPI and the telemetry stream in one process, on three
# listeners. The SPA is a separate image; see frontend/Dockerfile.

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# csproj first, so restore is cached until a dependency actually changes.
COPY ["src/SeniorsInTheMiddle.Proxy/SeniorsInTheMiddle.Proxy.csproj", "src/SeniorsInTheMiddle.Proxy/"]
RUN dotnet restore "src/SeniorsInTheMiddle.Proxy/SeniorsInTheMiddle.Proxy.csproj"

COPY . .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "src/SeniorsInTheMiddle.Proxy/SeniorsInTheMiddle.Proxy.csproj" \
    -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Kestrel listeners come from the Proxy:*Port settings below, not from this preset.
ENV ASPNETCORE_HTTP_PORTS=

# No key material is baked into the image. The MITM CA is generated on first start into
# this directory, which the app user owns.
#
# MOUNT A VOLUME HERE. Without one the CA lives in the container's writable layer:
# it survives a restart of that same container, but any new container -- a redeploy,
# a new revision, a fresh docker run -- mints a new CA, and every client then has to
# trust it again. That is an OS-level step on each device, not a browser click.
#
#   docker run -v sitm-ca:/app/certs ...
#
# An existing CA can be supplied instead with Mitm__CertificatePath /
# Mitm__CertificatePassword, ideally from a mounted secret.
RUN mkdir -p /app/certs && chown $APP_UID:$APP_UID /app/certs
ENV Mitm__CertificatePath=/app/certs/mitm-ca.pfx

COPY --from=publish /app/publish .

# One port per role -- see Proxy:HttpPort / Proxy:HttpsPort / Proxy:ApiPort.
#
# 3128 carries proxy traffic: CONNECT tunnels and absolute-form HTTP. Devices are pointed
#      here, and read /ca.crt and /proxy.pac from it. Nothing else answers on it.
# 3127 is the same proxy inside TLS, using a certificate signed by the CA served at
#      /ca.crt, for clients configured with an HTTPS proxy.
# 8080 is the WebAPI, Swagger and the telemetry stream. It never proxies.
EXPOSE 3128
EXPOSE 3127
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "SeniorsInTheMiddle.Proxy.dll"]
