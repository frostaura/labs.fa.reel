# syntax=docker/dockerfile:1
# The build stage runs on the build machine's architecture: its output is static files, identical
# for every target, so emulating arm64 to produce them only made the arm64 image slower to build.
FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /app

# Reproducible install from the lockfile, in its own layer keyed on the manifests. No fallback to
# `npm install` (K-13): a lockfile out of sync with package.json is a defect to fix in the repo,
# and resolving around it here built images from dependency versions nobody had reviewed.
COPY src/frontend/package*.json ./
RUN npm ci --no-audit --no-fund

COPY src/frontend/ ./
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/dist /usr/share/nginx/html
COPY src/frontend/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
