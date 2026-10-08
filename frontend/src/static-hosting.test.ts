import { describe, expect, it } from "vitest";

import { API_ORIGIN_VARIABLE, apiOriginDirective, cspApiOrigin } from "../docker/csp-api-origin.mjs";
import initScript from "../docker/init.sh?raw";
import dockerfile from "../Dockerfile?raw";
import indexHtml from "../index.html?raw";
import nginxConf from "../nginx.conf?raw";

/// Unit: the shipped SPA image (Dockerfile → nginx.conf + docker/init.sh) serves the app with the browser-side
/// defenses on, and the API origin its CSP allows is derived from the same runtime env the SPA itself reads.
///
/// Why pinned here: none of this runs in `pnpm dev` (Vite serves the app, not nginx), so a header lost in an nginx edit
/// or an inline script reintroduced into index.html would ship without a single other test noticing. These pin the
/// policy's content and the config as text; docker/e2e/run.sh (CI: frontend.yml's spa-image job) runs the real image
/// and checks that nginx accepts the config and serves what is pinned here.

// serialize-javascript's escapes, which the CLI applies to the serialized env: < > / and the two JS line separators.
const SERIALIZE_UNSAFE = new RegExp("[<>/" + String.fromCharCode(0x2028, 0x2029) + "]", "g");

/**
 * The env script exactly as @import-meta-env/cli@0.7.4 leaves dist/env.js after substituting `.env` at container start —
 * byte-for-byte against the real CLI for plain, quote- and backslash-carrying values: each value's `\` and `"` escaped,
 * the env serialized, and the result dropped into the JSON.parse('…') placeholder.
 */
function substituted(env: Record<string, string>): string {
  const escaped = Object.fromEntries(Object.entries(env).map(([key, value]) => [key, value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')]));
  const serialized = JSON.stringify(escaped).replace(SERIALIZE_UNSAFE, c => `\\u${c.charCodeAt(0).toString(16).toUpperCase().padStart(4, "0")}`);

  return `globalThis.import_meta_env = JSON.parse('${serialized}');\n`;
}

/** Production .tsx sources under src/, keyed by path. */
function productionSources(): Array<[string, string]> {
  const sources = import.meta.glob("./**/*.tsx", { eager: true, import: "default", query: "?raw" }) as Record<string, string>;

  return Object.entries(sources).filter(([path]) => !path.includes(".test."));
}

/** Each JSX opening tag `<name …>` in `source`, read up to the first `>` outside a `{…}` expression (so `=>` in a handler does not end it). */
function openingTags(source: string, name: string): string[] {
  return Array.from(source.matchAll(new RegExp(`<${name}\\b`, "g")), match => {
    let depth = 0;
    let end = match.index;

    for (; end < source.length; end++) {
      if (source[end] === "{") depth++;
      else if (source[end] === "}") depth--;
      else if (source[end] === ">" && depth === 0) break;
    }

    return source.slice(match.index, end + 1);
  });
}

/** The `location /` block — where the SPA (index.html and every asset) is served from. */
function spaLocation(): string {
  const start = nginxConf.indexOf("location / {");
  const end = nginxConf.indexOf("error_page", start);

  expect(start, "nginx.conf no longer has a `location / {` block — the SPA's headers are pinned to it").toBeGreaterThanOrEqual(0);
  return nginxConf.slice(start, end);
}

/** The value of one `add_header <name> "<value>" always;` in the SPA location. */
function header(name: string): string {
  const match = new RegExp(`add_header\\s+${name}\\s+"([^"]*)"\\s+always;`).exec(spaLocation());

  expect(match, `nginx.conf does not set ${name} (with "always") on the SPA location`).not.toBeNull();
  return match![1];
}

/** One CSP directive's sources, e.g. `script-src` → ["'self'"]. */
function cspDirective(name: string): string[] {
  const directive = header("Content-Security-Policy").split(";").map(d => d.trim().split(/\s+/)).find(([key]) => key === name);

  expect(directive, `the CSP has no ${name} directive`).toBeDefined();
  return directive!.slice(1);
}

describe("SPA response headers (nginx.conf)", () => {
  it("allows scripts only from the app's own origin — no inline script, no eval", () => {
    expect(cspDirective("default-src")).toEqual(["'self'"]);
    expect(cspDirective("script-src")).toEqual(["'self'"]);
  });

  it("embeds no plugins", () => {
    expect(cspDirective("object-src")).toEqual(["'none'"]);
  });

  it("closes the directives default-src does not fall back to: <base>, form targets, framing", () => {
    expect(cspDirective("base-uri")).toEqual(["'none'"]);
    expect(cspDirective("form-action")).toEqual(["'none'"]);
    expect(cspDirective("frame-ancestors")).toEqual(["'none'"]);
  });

  it("leaves no form in the app that form-action 'none' would break — each submits through its onSubmit handler", () => {
    // `pnpm dev` serves no CSP, so a form that relied on a native submission target would only break once deployed.
    const forms = productionSources().flatMap(([path, source]) => openingTags(source, "form").map(tag => [path, tag] as const));
    const native = forms.filter(([, tag]) => !/\bonSubmit=/.test(tag) || /\baction=/.test(tag)).map(([path, tag]) => `${path}: ${tag}`);

    expect(forms.length).toBeGreaterThan(0);
    expect(native).toEqual([]);
  });

  it("lets the app call only itself and the API origin derived at container start", () => {
    expect(cspDirective("connect-src")).toEqual(["'self'", `$${API_ORIGIN_VARIABLE}`]);
  });

  it("loads images from the app, the API, inline data and https — never cleartext http or another scheme", () => {
    expect(cspDirective("img-src")).toEqual(["'self'", "data:", "blob:", "https:", `$${API_ORIGIN_VARIABLE}`]);
  });

  it("sends nosniff, and a Referer trimmed to the origin on cross-origin requests — so no outside host sees which repository or item was open", () => {
    expect(header("X-Content-Type-Options")).toBe("nosniff");
    expect(header("Referrer-Policy")).toBe("strict-origin-when-cross-origin");
  });

  it("reads the API origin from the file init.sh writes, at server level so every location sees it", () => {
    const include = /^\s{4}include\s+(\S+);/m.exec(nginxConf);

    expect(include, "nginx.conf does not include the generated API-origin file at server level").not.toBeNull();
    expect(initScript).toContain(`> ${include![1]}`);
  });
});

describe("the SPA's runtime env script", () => {
  it("is loaded from /env.js — index.html carries no inline script a strict script-src would block", () => {
    const scripts = indexHtml.match(/<script\b[^>]*>/g) ?? [];

    expect(scripts).toContain('<script src="/env.js">');
    expect(scripts.filter(tag => !/\bsrc=/.test(tag))).toEqual([]);
  });

  it("is what init.sh substitutes at container start, before deriving the CSP's API origin from it and starting nginx", () => {
    const substitute = initScript.indexOf("@import-meta-env/cli@0.7.4 -x .env -e .env -p dist/env.js");
    const derive = initScript.indexOf("node /usr/local/lib/codespace/csp-api-origin.mjs dist/env.js");
    const serve = initScript.indexOf('nginx -g "daemon off;"');

    expect(substitute).toBeGreaterThanOrEqual(0);
    expect(derive).toBeGreaterThan(substitute);
    expect(serve).toBeGreaterThan(derive);
    expect(initScript).toMatch(/^set -e$/m);
  });

  it("is wired into the image: the derivation script and init.sh are copied where init.sh and CMD expect them", () => {
    expect(dockerfile).toContain("COPY ./docker/csp-api-origin.mjs /usr/local/lib/codespace/csp-api-origin.mjs");
    expect(dockerfile).toContain("COPY ./docker/init.sh /init.sh");
    expect(dockerfile).toContain('CMD ["/init.sh"]');
  });
});

describe("cspApiOrigin (docker/csp-api-origin.mjs)", () => {
  it("pins the nginx variable name nginx.conf reads", () => {
    // Renaming it on one side only leaves `$codespace_api_origin` undefined in nginx — which refuses to start.
    expect(API_ORIGIN_VARIABLE).toBe("codespace_api_origin");
  });

  it.each([
    ["an API on another host, with a path", "https://api.codespace.example/base/", "https://api.codespace.example"],
    ["an API on a non-default port", "http://10.0.0.5:5099", "http://10.0.0.5:5099"],
    ["an API on an IPv6 loopback", "http://[::1]:5099", "http://[::1]:5099"],
    ["a mixed-case host", "https://API.Example.Test", "https://api.example.test"],
    ["the same-origin default (blank)", "", ""],
    // The SPA requests VITE_API_URL + "/api/…" and the browser resolves that against the page, as below.
    ["a path on the app's own origin, which 'self' already allows", "/backend", ""],
    ["a path relative to the page, which stays on the app's own origin", "api.example.test", ""],
    ["a protocol-relative API, which takes the page's scheme — so its CSP source names no scheme either", "//api.example.test:8443/base", "api.example.test:8443"],
  ])("names the origin for %s", (_, apiUrl, origin) => {
    expect(cspApiOrigin(substituted({ VITE_API_URL: apiUrl }))).toBe(origin);
  });

  it("names no origin when the env carries no API URL at all", () => {
    expect(cspApiOrigin(substituted({}))).toBe("");
  });

  it.each([
    ["a non-http scheme", "javascript:alert(1)"],
    ["a websocket scheme", "wss://api.example.test"],
    ["a value no URL parser accepts", "http://"],
    ["a host that would break out of the nginx string", 'https://a"b.example'],
    ["a protocol-relative host that would break out of the nginx string", '//a"b.example'],
  ])("refuses %s rather than write it into the CSP — naming the setting to fix", (_, apiUrl) => {
    expect(() => cspApiOrigin(substituted({ VITE_API_URL: apiUrl }))).toThrow(`VITE_API_URL ${JSON.stringify(apiUrl)} does not name an http(s) origin the CSP can allow`);
  });

  it("renders the nginx set directive init.sh writes", () => {
    expect(apiOriginDirective(substituted({ VITE_API_URL: "https://api.codespace.example/v1" }))).toBe('set $codespace_api_origin "https://api.codespace.example";\n');
    expect(apiOriginDirective(substituted({ VITE_API_URL: "" }))).toBe('set $codespace_api_origin "";\n');
  });
});
