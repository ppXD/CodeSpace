# ── Build stage: compile dist/ with node ──
FROM node:20-alpine AS build

WORKDIR /app

RUN apk add --no-cache git

RUN corepack enable && corepack prepare pnpm@10.5.2 --activate

# Copy manifests first for better layer caching
COPY package.json ./
COPY pnpm-lock.yaml* package-lock.json* ./

# Preserve original install behaviour (project historically built with `npm install --force`)
RUN pnpm install

# Copy the rest of the source and build
COPY . .
RUN pnpm run build

# ── Runtime stage: clean nginx; install node via apk to keep libc/OpenSSL aligned ──
FROM nginx:stable-alpine

# nodejs + npm are needed at runtime for `npx @import-meta-env/cli` env substitution.
# Installing via apk ensures they link against the same musl/OpenSSL nginx uses
# (the previous `COPY --from=node /usr/lib ...` trick clobbered nginx's OpenSSL 3.5
#  with an older version, breaking QUIC-TLS symbols).
RUN apk add --no-cache nodejs npm

WORKDIR /usr/share/nginx/html

# Ship only what's needed at runtime.
# Note: .env is gitignored and provided at runtime by the K8s ConfigMap
# (mounted at /usr/share/nginx/html/.env via subPath), so it's not copied here.
COPY --from=build /app/dist           /usr/share/nginx/html/dist
COPY --from=build /app/.env.example   /usr/share/nginx/html/.env.example
COPY --from=build /app/package.json   /usr/share/nginx/html/package.json

COPY ./nginx.conf /etc/nginx/conf.d/default.conf

RUN printf '#!/bin/sh\nset -e\ncd /usr/share/nginx/html\nnpx -y @import-meta-env/cli@0.7.4 -x .env -e .env -p dist/index.html\nnginx -g "daemon off;"\n' > /init.sh \
    && chmod +x /init.sh

CMD ["/init.sh"]
