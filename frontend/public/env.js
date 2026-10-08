// The SPA's runtime env. In the shipped image, docker/init.sh has @import-meta-env/cli replace the placeholder below with
// the container's env at start; the built code reads it as `import.meta.env`. A file rather than an inline <script> in
// index.html, so the Content-Security-Policy in nginx.conf can allow scripts from 'self' only.
globalThis.import_meta_env = JSON.parse('"import_meta_env_placeholder"');
