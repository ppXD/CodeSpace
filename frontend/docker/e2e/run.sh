#!/usr/bin/env bash
# SPA image E2E: build the REAL frontend image and start it the way the cluster does (the ConfigMap's .env mounted at
# /usr/share/nginx/html/.env, the image's own CMD /init.sh), then prove over HTTP what static-hosting.test.ts can only
# pin as source text: init.sh derives the API origin from the substituted env, nginx accepts the config and reads that
# origin, and every response carries the CSP, nosniff and Referrer-Policy, with /env.js handing the SPA its env.
#
# Fidelity: high. The real Dockerfile, init.sh, @import-meta-env/cli (fetched by npx at start, as in production),
# csp-api-origin.mjs and nginx. The expected CSP is nginx.conf's own header with the derived source filled in, so the
# policy's content stays pinned in one place (static-hosting.test.ts) and this checks only that nginx serves it.
#
#   frontend/docker/e2e/run.sh            # from anywhere; needs docker, curl and node
set -eEuo pipefail
trap 'echo "FAIL: line $LINENO: \`$BASH_COMMAND\` exited $?" >&2' ERR
cd "$(dirname "$0")/../.."

RUN_ID="codespace-spa-e2e-$(date +%s)-$$"
IMAGE="$RUN_ID"
WORK="$(mktemp -d)"

cleanup() {
  docker ps -aq --filter "label=codespace.e2e=$RUN_ID" | xargs -r docker rm -f >/dev/null 2>&1 || true
  docker rmi -f "$IMAGE" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

fail() { # $1 = what failed and how to look into it; $2 = the container whose logs explain it (optional)
  echo "FAIL: $1" >&2
  if [ -n "${2:-}" ]; then
    echo "--- docker logs $2 (tail) ---" >&2
    docker logs --tail 60 "$2" >&2 2>&1 || true
  fi
  exit 1
}

# The CSP exactly as nginx.conf writes it, with `$codespace_api_origin` still in it.
CSP_TEMPLATE="$(sed -n 's/^ *add_header Content-Security-Policy "\(.*\)" always;$/\1/p' nginx.conf)"
[ -n "$CSP_TEMPLATE" ] || fail "nginx.conf has no one-line 'add_header Content-Security-Policy \"...\" always;' to compare the served header with"

header() { # $1 = a `curl -D -` header dump, $2 = a header name; prints that header's value
  printf '%s\n' "$1" | tr -d '\r' | { grep -i "^$2:" || true; } | head -1 | sed 's/^[^:]*: *//'
}

# The VITE_API_URL a served env script hands the SPA, read by running the script as the browser does.
served_api_url() { # $1 = the env script's path
  node -e 'const s = {}; require("vm").runInNewContext(require("fs").readFileSync(process.argv[1], "utf8"), s); process.stdout.write(String(s.import_meta_env.VITE_API_URL));' "$1"
}

start() { # $1 = case, $2 = VITE_API_URL; prints the container's name
  local name="$RUN_ID-$1"

  printf 'VITE_API_URL=%s\n' "$2" > "$WORK/$1.env"
  docker run -d --name "$name" --label "codespace.e2e=$RUN_ID" -p 127.0.0.1::80 -v "$WORK/$1.env:/usr/share/nginx/html/.env:ro" "$IMAGE" >/dev/null || fail "$1: docker run of $IMAGE failed"
  echo "$name"
}

# nginx starts only after init.sh has fetched the env CLI and substituted the env, so this waits on GET / itself.
wait_serving() { # $1 = container; prints the host port nginx answers on
  local port

  for _ in $(seq 1 60); do
    [ "$(docker inspect -f '{{.State.Running}}' "$1")" = "true" ] || fail "$1 exited before serving: init.sh stopped (logs below)" "$1"

    port="$(docker port "$1" 80/tcp 2>/dev/null | head -1 | sed 's/.*://' || true)"

    if [ -n "$port" ] && curl -fsS -o /dev/null "http://127.0.0.1:$port/" 2>/dev/null; then
      echo "$port"
      return 0
    fi

    sleep 3
  done

  fail "$1 did not answer GET / within 180s: look for the npx fetch or the nginx start in 'docker logs $1'" "$1"
}

expect_served() { # $1 = case, $2 = VITE_API_URL, $3 = the CSP source init.sh must derive for it ("" = same-origin)
  local name port base expected path headers csp index inline served

  echo "==> $1: VITE_API_URL='$2' serves with the API source '$3'"
  name="$(start "$1" "$2")"
  port="$(wait_serving "$name")"
  base="http://127.0.0.1:$port"

  docker exec "$name" nginx -t >/dev/null 2>&1 || fail "$1: nginx -t rejects the config init.sh completed (run 'docker exec $name nginx -t')" "$name"

  expected="${CSP_TEMPLATE//\$codespace_api_origin/$3}"

  for path in / /teams/demo/runs /env.js; do
    headers="$(curl -fsS -D - -o /dev/null "$base$path")" || fail "$1: GET $path failed" "$name"
    csp="$(header "$headers" Content-Security-Policy)"

    [ "$csp" = "$expected" ] || fail "$1: GET $path sent the CSP '$csp', expected nginx.conf's with '$3' as the API source: '$expected'" "$name"
    [ "$(header "$headers" X-Content-Type-Options)" = "nosniff" ] || fail "$1: GET $path sent no 'X-Content-Type-Options: nosniff'" "$name"
    [ "$(header "$headers" Referrer-Policy)" = "strict-origin-when-cross-origin" ] || fail "$1: GET $path sent no 'Referrer-Policy: strict-origin-when-cross-origin'" "$name"
  done

  # The built index.html, not the source one: a build step that inlined a script would be blocked by script-src 'self'.
  index="$(curl -fsS "$base/teams/demo/runs")" || fail "$1: a deep link did not fall back to index.html" "$name"
  case "$index" in *'<script src="/env.js"></script>'*) ;; *) fail "$1: the served index.html does not load /env.js" "$name" ;; esac
  inline="$(printf '%s' "$index" | { grep -o '<script[^>]*>' || true; } | { grep -v ' src=' || true; })"
  [ -z "$inline" ] || fail "$1: the served index.html carries an inline script script-src 'self' blocks: $inline" "$name"

  headers="$(curl -fsS -D - -o "$WORK/$1.env.js" "$base/env.js")" || fail "$1: GET /env.js failed" "$name"
  case "$(header "$headers" Content-Type)" in application/javascript*|text/javascript*) ;; *) fail "$1: /env.js is served as '$(header "$headers" Content-Type)', which nosniff stops the browser running" "$name" ;; esac
  served="$(served_api_url "$WORK/$1.env.js")" || fail "$1: /env.js does not run (see $WORK/$1.env.js)" "$name"
  [ "$served" = "$2" ] || fail "$1: /env.js hands the SPA VITE_API_URL '$served', expected '$2'" "$name"

  docker rm -f "$name" >/dev/null 2>&1 || true
  echo "    CSP, nosniff and Referrer-Policy on every response; /env.js carries the env"
}

expect_refused() { # $1 = case, $2 = VITE_API_URL init.sh must stop on, naming the setting, before nginx starts
  local name state="" logs

  echo "==> $1: VITE_API_URL='$2' must stop the container"
  name="$(start "$1" "$2")"

  for _ in $(seq 1 60); do
    state="$(docker inspect -f '{{.State.Status}}' "$name" 2>/dev/null || true)"
    [ "$state" = "exited" ] && break
    sleep 3
  done

  [ "$state" = "exited" ] || fail "$1: still '$state' after 180s; init.sh should have stopped on VITE_API_URL '$2'" "$name"
  [ "$(docker inspect -f '{{.State.ExitCode}}' "$name")" != "0" ] || fail "$1: init.sh exited 0" "$name"

  logs="$(docker logs "$name" 2>&1)"
  case "$logs" in *"VITE_API_URL \"$2\" does not name an http(s) origin the CSP can allow"*) ;; *) fail "$1: the container stopped without naming the setting to fix" "$name" ;; esac

  docker rm -f "$name" >/dev/null 2>&1 || true
  echo "    stopped before nginx, naming VITE_API_URL"
}

echo "==> build the SPA image (frontend/Dockerfile)"
docker build -t "$IMAGE" .

expect_served same-origin "" ""
expect_served api-elsewhere "https://api.example.test/base" "https://api.example.test"
expect_served api-path "/backend" ""
expect_served api-protocol-relative "//api.example.test:8443/base" "api.example.test:8443"
expect_refused api-websocket "wss://api.example.test"

echo "PASS: the SPA image serves its CSP with the API origin init.sh derived, plus nosniff and Referrer-Policy"
