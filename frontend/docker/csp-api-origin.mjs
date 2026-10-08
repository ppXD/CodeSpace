// Names the API origin for the SPA's Content-Security-Policy, at container start (docker/init.sh).
//
//   node csp-api-origin.mjs dist/env.js > /etc/nginx/codespace/api-origin.conf
//
// The origin is read from the SPA's own env script AFTER @import-meta-env/cli has substituted it, so the CSP allows
// exactly the backend the app was configured to call — the same .env / process env, resolved by the same tool — and no
// second copy of that configuration exists to drift. Prints the nginx `set` directive nginx.conf includes; an empty
// origin means the API is same-origin ('self' already covers it).

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";
import { runInNewContext } from "node:vm";

/** The nginx variable nginx.conf's CSP reads. */
export const API_ORIGIN_VARIABLE = "codespace_api_origin";

// What may be written into the CSP header and the quoted nginx string: an http(s) scheme, a host name or bracketed IPv6
// literal, an optional port. Anything else (quotes, `$`, `;`, spaces) would break out of one or the other.
const SAFE_ORIGIN = /^https?:\/\/([a-z0-9.-]+|\[[0-9a-f:.]+\])(:\d{1,5})?$/;

// The SPA requests VITE_API_URL + "/api/…" as written, so the browser resolves the value against the page. Resolving it
// against stand-in pages tells its shapes apart: a path (or blank) lands on the stand-in's own origin, a protocol-relative
// value (`//api.example`) takes the stand-in's scheme, and anything else names its own origin.
const PAGE = "http://same-origin.invalid";
const SECURE_PAGE = "https://same-origin.invalid";

/**
 * The CSP source for the env script's VITE_API_URL: its origin; its host alone when it is protocol-relative, which a
 * CSP source with no scheme matches the same way; or "" when it stays on the app's own origin ('self' already allows
 * it). Throws, naming the setting, on anything else.
 */
export function cspApiOrigin(envScript) {
  const apiUrl = readApiUrl(envScript);
  const onPage = resolveAgainst(apiUrl, PAGE);

  if (onPage.origin === PAGE) return "";

  if (!SAFE_ORIGIN.test(onPage.origin)) throw refused(apiUrl);

  return takesPageScheme(apiUrl) ? onPage.host : onPage.origin;
}

function readApiUrl(envScript) {
  const sandbox = {};

  runInNewContext(envScript, sandbox);

  return sandbox.import_meta_env?.VITE_API_URL ?? "";
}

function resolveAgainst(apiUrl, page) {
  try {
    return new URL(apiUrl, page);
  } catch {
    throw refused(apiUrl);
  }
}

function takesPageScheme(apiUrl) {
  return resolveAgainst(apiUrl, PAGE).protocol !== resolveAgainst(apiUrl, SECURE_PAGE).protocol;
}

function refused(apiUrl) {
  return new Error(`VITE_API_URL ${JSON.stringify(apiUrl)} does not name an http(s) origin the CSP can allow`);
}

export function apiOriginDirective(envScript) {
  return `set $${API_ORIGIN_VARIABLE} "${cspApiOrigin(envScript)}";\n`;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  process.stdout.write(apiOriginDirective(readFileSync(process.argv[2], "utf8")));
