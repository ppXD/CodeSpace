#!/bin/sh
# Container start for the SPA image (Dockerfile CMD).
set -e
cd /usr/share/nginx/html

# Substitute the runtime env (.env from the ConfigMap, overridden by the process env) into the SPA's env script. It is
# served as /env.js rather than inlined in index.html, so the CSP needs no inline-script allowance.
npx -y @import-meta-env/cli@0.7.4 -x .env -e .env -p dist/env.js

# Name the API origin that env points the SPA at, for the CSP in nginx.conf (which includes this file). A malformed
# VITE_API_URL stops the container here instead of serving a policy that blocks the app.
mkdir -p /etc/nginx/codespace
node /usr/local/lib/codespace/csp-api-origin.mjs dist/env.js > /etc/nginx/codespace/api-origin.conf

nginx -g "daemon off;"
