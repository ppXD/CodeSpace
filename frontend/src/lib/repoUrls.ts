/**
 * Provider web-URL builders for the Code browser: "open this file on the provider", and resolving a
 * README's relative assets/links to absolute URLs (so embedded images render like they do on the
 * provider). Detected from the repo web-URL host — github.com uses raw.githubusercontent.com and
 * /blob/; everything else is assumed GitLab-style (/-/raw/, /-/blob/). Pure + side-effect-free.
 */

function isGitHub(webUrl: string): boolean {
  try {
    return new URL(webUrl).host.toLowerCase().includes("github");
  } catch {
    return false;
  }
}

const trimSlashes = (s: string) => s.replace(/^\/+|\/+$/g, "");

/** Link to view a file/folder on the provider's web UI (`/blob/{ref}/{path}`). */
export function blobUrl(webUrl: string, ref: string, path: string): string {
  const base = webUrl.replace(/\/+$/, "");
  const p = trimSlashes(path);
  return isGitHub(webUrl) ? `${base}/blob/${ref}/${p}` : `${base}/-/blob/${ref}/${p}`;
}

/** Raw content URL for a path — GitHub uses raw.githubusercontent.com, GitLab uses `/-/raw/{ref}/`. */
export function rawUrl(webUrl: string, ref: string, path: string): string {
  const p = trimSlashes(path);

  if (isGitHub(webUrl)) {
    try {
      const ownerRepo = trimSlashes(new URL(webUrl).pathname);
      return `https://raw.githubusercontent.com/${ownerRepo}/${ref}/${p}`;
    } catch {
      return `${webUrl.replace(/\/+$/, "")}/raw/${ref}/${p}`;
    }
  }

  return `${webUrl.replace(/\/+$/, "")}/-/raw/${ref}/${p}`;
}

/**
 * Resolve a URL found inside a README rendered at <c>dir</c> (the README's folder, repo-root-relative).
 * Absolute URLs, data URIs, mailto, and in-page anchors pass through untouched. Relative paths resolve
 * against the repo: images (<c>asImage</c>) to the raw URL, links to the provider's blob view.
 */
export function resolveReadmeUrl(url: string, webUrl: string, ref: string, dir: string, asImage: boolean): string {
  if (!url || /^(https?:|data:|mailto:|tel:|#)/i.test(url)) return url;

  const rel = url.replace(/^\.\//, "");
  const joined = dir ? `${trimSlashes(dir)}/${rel}` : rel;
  return asImage ? rawUrl(webUrl, ref, joined) : blobUrl(webUrl, ref, joined);
}
