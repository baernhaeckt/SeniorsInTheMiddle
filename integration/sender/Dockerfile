# No dependencies, so no install step and no lockfile: the app and its UI are Node core
# modules and one static HTML file.
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
    UI_PORT=3100 \
    NODE_ENV=production

# The testing UI. The traffic itself goes out through the proxy, not through this port.
EXPOSE 3100

USER node
CMD ["node", "src/main.js"]
