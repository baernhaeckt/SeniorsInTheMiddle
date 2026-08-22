# No dependencies, so no install step and no lockfile: the app is Node core modules only.
FROM node:22-alpine

WORKDIR /app
COPY package.json ./
COPY src ./src

# The certificate and key are mounted read-only from the pki volume at run time.
ENV HTTP_PORT=3000 \
    HTTPS_PORT=3443 \
    TLS_CERT=/pki/receiver.crt \
    TLS_KEY=/pki/receiver.key \
    NODE_ENV=production

EXPOSE 3000
EXPOSE 3443

USER node
CMD ["node", "src/server.js"]
