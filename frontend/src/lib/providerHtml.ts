/**
 * Prepare provider-rendered README HTML for embedding. The provider (GitHub / GitLab) already returns
 * sanitized HTML, but two things still need fixing before we inject it: relative asset/link paths (the
 * provider leaves `./logo.png` relative, which would resolve against OUR origin), and a defensive XSS
 * pass (belt-and-suspenders on top of the provider's own sanitization). Pure + side-effect-free; relies
 * on the DOM (browser + jsdom test env both provide DOMParser).
 */
import { resolveReadmeUrl } from "@/lib/repoUrls";

// Tags that never belong in embedded README content — dropped regardless of the provider's output.
const DROP_TAGS = ["script", "style", "iframe", "object", "embed", "link", "meta", "base", "form"];

/** Resolve each candidate in a srcset ("url 1x, url2 2x") against the repo, preserving the descriptors. */
function resolveSrcset(srcset: string, webUrl: string, ref: string, dir: string): string {
  return srcset
    .split(",")
    .map(part => {
      const [url, ...descriptors] = part.trim().split(/\s+/);
      return [resolveReadmeUrl(url, webUrl, ref, dir, true), ...descriptors].join(" ");
    })
    .join(", ");
}

export function prepareProviderHtml(html: string, webUrl: string, ref: string, dir: string): string {
  if (!html) return "";

  const doc = new DOMParser().parseFromString(html, "text/html");

  doc.querySelectorAll(DROP_TAGS.join(",")).forEach(el => el.remove());

  doc.body.querySelectorAll("*").forEach(el => {
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase();

      if (name.startsWith("on")) { el.removeAttribute(attr.name); continue; }

      if ((name === "href" || name === "src" || name === "poster") && /^\s*javascript:/i.test(attr.value))
        el.removeAttribute(attr.name);
    }

    const src = el.getAttribute("src");
    if (src) el.setAttribute("src", resolveReadmeUrl(src, webUrl, ref, dir, true));

    const poster = el.getAttribute("poster");
    if (poster) el.setAttribute("poster", resolveReadmeUrl(poster, webUrl, ref, dir, true));

    const srcset = el.getAttribute("srcset");
    if (srcset) el.setAttribute("srcset", resolveSrcset(srcset, webUrl, ref, dir));

    if (el.tagName.toLowerCase() === "a") {
      const href = el.getAttribute("href");

      // In-page anchors (#section) scroll within the card — leave them as-is, no new tab.
      if (href && !href.startsWith("#")) {
        el.setAttribute("href", resolveReadmeUrl(href, webUrl, ref, dir, false));
        el.setAttribute("target", "_blank");
        el.setAttribute("rel", "noopener noreferrer");
      }
    }
  });

  return doc.body.innerHTML;
}
