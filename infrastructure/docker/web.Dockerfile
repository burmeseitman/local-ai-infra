# syntax=docker/dockerfile:1
# Build context: repository root.
#   docker build -f infrastructure/docker/web.Dockerfile --target runtime .   # image
#   docker build -f infrastructure/docker/web.Dockerfile --target test .      # lint + typecheck + tests

FROM node:22-alpine AS build
WORKDIR /app
COPY src/LocalAI.Web/package.json src/LocalAI.Web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/LocalAI.Web/ ./
# Empty = same-origin /api (served through nginx below). Never pass secrets here:
# VITE_* values are embedded in the public JavaScript bundle.
ARG VITE_API_BASE_URL=
ENV VITE_API_BASE_URL=${VITE_API_BASE_URL}
RUN npm run build

FROM build AS test
RUN npm run lint && npm run typecheck && npm test

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY infrastructure/docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
