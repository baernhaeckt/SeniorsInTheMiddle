# No dependencies, so no install step and no lockfile: the evaluator and its UI are Node
# core modules and one static HTML file. Same shape as ../../sender/Dockerfile.
FROM node:22-alpine

WORKDIR /app
COPY package.json ./
COPY src ./src
COPY public ./public

ENV PROXY_HOST=proxy \
    PROXY_PORT=3128 \
    PROXY_TLS_PORT=3127 \
    TARGET_HOST=receiver.sitm.local \
    TARGET_HTTP_PORT=3000 \
    TARGET_HTTPS_PORT=3443 \
    RECEIVER_BASE_URL=http://receiver:3000 \
    CORPUS_DIR=/corpus \
    DATA_DIR=/data \
    UI_PORT=3200 \
    NODE_ENV=production

# The corpus is mounted read-only and the runs are written to a bind mount on the host,
# so both outlive the container.
VOLUME ["/data"]

# The web UI. The evaluation traffic itself goes out through the proxy, not this port.
EXPOSE 3200

# `node` owns nothing under /data by default; compose bind-mounts a host directory there,
# so the container runs as root to avoid a permission trap that differs per host. Nothing
# here is exposed outside the compose network but the UI port.
CMD ["node", "src/main.js"]
