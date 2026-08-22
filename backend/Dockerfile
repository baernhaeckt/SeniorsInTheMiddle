# The forward proxy, the WebAPI and the telemetry stream in one process, on three
# listeners, plus the python services it talks to over unix sockets. The SPA is a
# separate image; see frontend/Dockerfile.
#
# The build context is the REPOSITORY ROOT, because the python services and the uv
# lock file live there:
#
#   docker build -f backend/Dockerfile -t sitm-proxy .
#
# Process layout at runtime (supervisord is PID 1, everything runs as the app user):
#
#   pii-service            /opt/venv/bin/python -m pii_service            -> /run/services/pii-service.sock
#   privacy-check-service  /opt/venv/bin/python -m privacy_check_service  -> /run/services/privacy-check-service.sock
#   proxy                  dotnet SeniorsInTheMiddle.Proxy.dll            <- Services__Pii__SocketPath,
#                                                                            Services__PrivacyCheck__SocketPath
#
# Adding a service: COPY its package below, add a [program:..] block to
# backend/supervisord.conf and a Services__<Name>__SocketPath env here.

# ---------- dotnet build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# csproj first, so restore is cached until a dependency actually changes.
COPY ["backend/src/SeniorsInTheMiddle.Proxy/SeniorsInTheMiddle.Proxy.csproj", "src/SeniorsInTheMiddle.Proxy/"]
RUN dotnet restore "src/SeniorsInTheMiddle.Proxy/SeniorsInTheMiddle.Proxy.csproj"

COPY backend/ .

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "src/SeniorsInTheMiddle.Proxy/SeniorsInTheMiddle.Proxy.csproj" \
    -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# ---------- python build ----------
# Same base as the final stage, so the uv-managed interpreter and the wheels it
# installs are binary compatible with what gets copied over.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS python-build
COPY --from=ghcr.io/astral-sh/uv:latest /uv /uvx /usr/local/bin/

ENV UV_PYTHON_INSTALL_DIR=/opt/python \
    UV_PROJECT_ENVIRONMENT=/opt/venv \
    UV_LINK_MODE=copy \
    UV_COMPILE_BYTECODE=1 \
    UV_PYTHON_PREFERENCE=only-managed \
    HF_HOME=/opt/hf

WORKDIR /build

# Lock file first: the dependency layer is cached until pyproject/uv.lock change.
COPY pyproject.toml uv.lock .python-version ./
RUN uv python install \
    && uv sync --frozen --no-dev --no-install-project

# The spaCy models are part of the image; nothing is downloaded at runtime.
# Both models pii_detector.py can select (DEFAULT_LANGUAGE=de/en). Space separated;
# trim with --build-arg SPACY_MODELS="de_core_news_lg" for a smaller image.
ARG SPACY_MODELS="de_core_news_lg en_core_web_lg"
RUN for model in $SPACY_MODELS; do \
        uv run --no-sync --frozen python -m spacy download "$model" || exit 1; \
    done \
    && for model in $SPACY_MODELS; do \
        uv run --no-sync --frozen python -c "import spacy; spacy.load('$model'); print('ok: $model')" || exit 1; \
    done

# The sentence-transformers embedding model privacy_check_service uses is part of the
# image too. It goes into HF_HOME (/opt/hf), which is copied into the final stage and
# read there offline.
ARG EMBEDDING_MODEL="sentence-transformers/all-MiniLM-L6-v2"
RUN uv run --no-sync --frozen hf download "$EMBEDDING_MODEL" \
    && HF_HUB_OFFLINE=1 uv run --no-sync --frozen python -c \
        "from sentence_transformers import SentenceTransformer; SentenceTransformer('$EMBEDDING_MODEL'); print('ok: $EMBEDDING_MODEL')"

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# supervisord runs the python daemons and the dotnet process side by side. The
# system python it pulls in is only used by supervisord itself.
# g++ is for pytensor (pymc), which compiles its sampling graph to C at runtime;
# without a compiler it falls back to a much slower python backend.
RUN apt-get update \
    && apt-get install -y --no-install-recommends supervisor g++ \
    && rm -rf /var/lib/apt/lists/*

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
RUN mkdir -p /app/certs /run/services /var/log/supervisor \
    && chown $APP_UID:$APP_UID /app/certs /run/services
ENV Mitm__CertificatePath=/app/certs/mitm-ca.pfx

# ---- python services
ENV PYTHONPATH=/srv/services \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PATH=/opt/venv/bin:$PATH \
    SERVICE_SOCKET_DIR=/run/services \
    DEFAULT_LANGUAGE=de \
    HF_HOME=/opt/hf \
    HF_HUB_OFFLINE=1 \
    TOKENIZERS_PARALLELISM=false \
    PYTENSOR_FLAGS=base_compiledir=/tmp/pytensor

COPY --from=python-build /opt/python /opt/python
COPY --from=python-build /opt/venv /opt/venv
COPY --from=python-build /opt/hf /opt/hf

# One socket per service. These are the paths supervisord starts them on and the
# paths the proxy connects to; override both together if they ever need to move.
ENV PII_SOCKET_PATH=/run/services/pii-service.sock \
    Services__Pii__SocketPath=/run/services/pii-service.sock \
    PRIVACY_CHECK_SOCKET_PATH=/run/services/privacy-check-service.sock \
    Services__PrivacyCheck__SocketPath=/run/services/privacy-check-service.sock

COPY services/service_runtime        /srv/services/service_runtime
COPY services/pii_service            /srv/services/pii_service
COPY services/privacy_check_service  /srv/services/privacy_check_service

# ---- dotnet
WORKDIR /app
COPY --from=publish /app/publish .

COPY backend/supervisord.conf /etc/supervisor/conf.d/sitm.conf

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

# supervisord stays root so it can drop each program to the app user (see
# user= in supervisord.conf); none of the workloads run as root.
ENTRYPOINT ["/usr/bin/supervisord", "-n", "-c", "/etc/supervisor/supervisord.conf"]
