/**
 * Prepare provider-rendered README HTML for embedding. The provider (GitHub / GitLab) sanitizes its own render, but that
 * is not taken on trust: a team Admin can point a provider instance at any host, and the HTML lands in a page holding
 * the viewer's session. So the HTML is first cut down to the same allowlist the client-side <Markdown> path uses
 * (readmeSanitizeSchema), and only then are its URLs touched: relative asset/link paths (the provider leaves
 * `./logo.png` relative, which would resolve against OUR origin) are resolved against the repo, links open in a new
 * tab, in-page links point at the names as the sanitizer prefixed them, and images are requested without a Referer.
 * Pure + side-effect-free.
 */
import type { Element, Root } from "hast";
import { fromHtml } from "hast-util-from-html";
import { sanitize } from "hast-util-sanitize";
import { toHtml } from "hast-util-to-html";

import { readmeSanitizeSchema } from "@/lib/readmeSanitizeSchema";
import { resolveReadmeUrl } from "@/lib/repoUrls";

// The sanitizer prefixes these properties' values so a README's ids cannot shadow the app's own.
const CLOBBER_PREFIX = readmeSanitizeSchema.clobberPrefix ?? "";
const CLOBBERED_PROPERTIES = readmeSanitizeSchema.clobber ?? [];

interface RepoLocation {
  webUrl: string;
  ref: string;
  dir: string;
}

export function prepareProviderHtml(html: string, webUrl: string, ref: string, dir: string): string {
  if (!html) return "";

  const tree = sanitize(fromHtml(html, { fragment: true }), readmeSanitizeSchema) as Root;

  forEachElement(tree, el => prefixNamesOnce(el));
  forEachElement(tree, el => resolveUrls(el, { webUrl, ref, dir }));

  return toHtml(tree);
}

function forEachElement(parent: Root | Element, visit: (el: Element) => void): void {
  for (const child of parent.children) {
    if (child.type !== "element") continue;

    visit(child);
    forEachElement(child, visit);
  }
}

/**
 * Every id, name and aria reference carries the clobber prefix exactly once. The sanitizer adds it without checking for
 * one already there, and GitHub sends its ids prefixed (`user-content-fn-1` would become `user-content-user-content-fn-1`)
 * while GitLab sends footnote ids bare (`fn-1`): both end up as `user-content-fn-1`, which in-page links then target.
 */
function prefixNamesOnce(el: Element): void {
  for (const key of CLOBBERED_PROPERTIES) {
    const value = el.properties[key];

    if (typeof value === "string") el.properties[key] = prefixedOnce(value);
    else if (Array.isArray(value)) el.properties[key] = value.map(name => prefixedOnce(String(name)));
  }
}

function prefixedOnce(name: string): string {
  let bare = name;

  while (CLOBBER_PREFIX && bare.startsWith(CLOBBER_PREFIX)) bare = bare.slice(CLOBBER_PREFIX.length);

  return CLOBBER_PREFIX + bare;
}

/** Every URL the allowlist lets through (`src`, `srcset`, `<a href>`) resolved against the repo. */
function resolveUrls(el: Element, repo: RepoLocation): void {
  const { properties } = el;

  if (typeof properties.src === "string") properties.src = resolveImageUrl(properties.src, repo);

  if (typeof properties.srcSet === "string") properties.srcSet = resolveSrcset(properties.srcSet, repo);

  if (el.tagName === "img") properties.referrerPolicy = "no-referrer";

  if (el.tagName === "a") resolveLink(el, repo);
}

function resolveImageUrl(url: string, { webUrl, ref, dir }: RepoLocation): string {
  return resolveReadmeUrl(url, webUrl, ref, dir, true);
}

/**
 * Each srcset candidate ("url 2x") resolved against the repo, preserving its descriptors. The allowlist has no scheme
 * rule for srcset (it holds several URLs), so a candidate with any scheme but http(s) — data:, javascript: — is dropped
 * here rather than resolved.
 */
function resolveSrcset(srcset: string, repo: RepoLocation): string {
  return srcset
    .split(",")
    .map(candidate => candidate.trim().split(/\s+/))
    .filter(([url]) => isHttpOrRelative(url))
    .map(([url, ...descriptors]) => [resolveImageUrl(url, repo), ...descriptors].join(" "))
    .join(", ");
}

/** Relative, or http(s). A scheme is whatever precedes a colon that comes before any `/`, `?` or `#` — the sanitizer's rule. */
function isHttpOrRelative(url: string): boolean {
  return !/^[^/?#]*:/.test(url) || /^https?:/i.test(url);
}

/**
 * A link leaving the README opens the provider's view in a new tab; an in-page anchor (#section, a footnote) scrolls
 * within the card, to the name as prefixNamesOnce left it.
 */
function resolveLink(el: Element, { webUrl, ref, dir }: RepoLocation): void {
  const href = el.properties.href;

  if (typeof href !== "string") return;

  if (href.startsWith("#")) {
    el.properties.href = inPageHref(href);
    return;
  }

  el.properties.href = resolveReadmeUrl(href, webUrl, ref, dir, false);
  el.properties.target = "_blank";
  el.properties.rel = ["noopener", "noreferrer"];
}

/** `#name` pointed at the name prefixed once, whether the provider prefixed it or not. A bare `#` stays as it is. */
function inPageHref(href: string): string {
  return href === "#" ? href : `#${prefixedOnce(href.slice(1))}`;
}
