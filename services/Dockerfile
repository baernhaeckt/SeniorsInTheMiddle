# Integration image: the python example_service and the dotnet test host run
# side by side in one container and talk over a unix socket, the same way they
# do in the deployment. On windows this container is the only way to run them.
#
#   docker build -t services-integration ./services
#   docker run --rm services-integration

# ---------------------------------------------------------------- dotnet build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
ARG TARGETARCH
WORKDIR /src

COPY test-host/ test-host/

# self-contained, so the final image only needs python plus glibc
RUN case "$TARGETARCH" in arm64) RID=linux-arm64 ;; *) RID=linux-x64 ;; esac; \
    dotnet publish test-host/ServiceRuntime.TestHost.csproj \
        -c Release \
        -r "$RID" \
        --self-contained true \
        -o /app/test-host

# ------------------------------------------------------------------- runtime
FROM python:3.14-slim AS final

# the services are plain packages on PYTHONPATH, nothing to install
ENV PYTHONUNBUFFERED=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONPATH=/srv/services \
    SERVICE_SOCKET_DIR=/run/services

WORKDIR /srv/services

COPY service_runtime/ service_runtime/
COPY example_service/ example_service/

COPY --from=dotnet-build /app/test-host /app/test-host

COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/docker-entrypoint.sh \
    && chmod +x /usr/local/bin/docker-entrypoint.sh /app/test-host/ServiceRuntime.TestHost \
    && mkdir -p "$SERVICE_SOCKET_DIR"

ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
