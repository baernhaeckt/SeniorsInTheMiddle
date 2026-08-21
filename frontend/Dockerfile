# Build the static bundle.
FROM node:22-alpine AS build
WORKDIR /app

# The public registry, not an internal mirror.
COPY package.json package-lock.json .npmrc ./
RUN npm ci --no-audit --no-fund

COPY . .
ARG VITE_PROXY_WS_URL=""
ENV VITE_PROXY_WS_URL=$VITE_PROXY_WS_URL
RUN npm run build

# Serve it. Azure Container Apps ingress points at 8080.
FROM nginx:1.27-alpine AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
CMD ["nginx", "-g", "daemon off;"]
