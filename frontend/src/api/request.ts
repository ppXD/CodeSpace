/**
 * Minimal fetch wrapper that mirrors the two middlewares in `client.ts` (Bearer JWT and
 * X-Team-Id) so hand-rolled calls — used for endpoints not yet covered by the regenerated
 * openapi-fetch schema — get the same auth treatment. Centralises:
 *   • header injection
 *   • JSON content-type defaults
 *   • backend error envelope → ApiError
 */

// Empty default → relative URLs → Vite proxies /api to the backend. Set
// VITE_API_URL=http://host:port in .env.local to hit the backend directly (cross-origin,
// requires CORS allow-list on the backend). See frontend/.env.example.
const baseUrl: string = import.meta.env.VITE_API_URL ?? "";

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly body?: unknown;

  constructor(status: number, code: string, message: string, body?: unknown) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.body = body;
  }
}

export async function fetchJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers ?? {});

  if (!headers.has("Accept")) headers.set("Accept", "application/json");
  if (init.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");

  const jwt = localStorage.getItem("codespace.jwt");
  // Sign-in is anonymous. If a stale token is still in localStorage from a previous
  // session, sending it would make the backend rotation-gate fire and 403 the request
  // before the new credentials are even checked.
  if (jwt && path !== "/api/auth/sign-in") headers.set("Authorization", `Bearer ${jwt}`);

  const teamId = localStorage.getItem("codespace.activeTeamId");
  if (teamId) headers.set("X-Team-Id", teamId);

  const response = await fetch(baseUrl + path, { ...init, headers });

  if (response.status === 204) return undefined as T;

  const text = await response.text();
  const parsed = text.length > 0 ? safeJsonParse(text) : undefined;

  if (!response.ok) {
    const code = (parsed as { code?: string })?.code ?? `http_${response.status}`;
    const message = (parsed as { message?: string })?.message ?? response.statusText;

    // 401 means our JWT is invalid / expired / missing. Wipe it so the auth guard
    // sends the user to /signin — except on the endpoints a signed-out visitor is
    // SUPPOSED to call, where a 401 is the answer to what they asked, not a broken
    // session (bad password; dead invite token). Bouncing those replaces the page's
    // own message with a redirect the visitor can do nothing with.
    if (response.status === 401 && !isAnonymousSurface(path)) handleUnauthorized();

    // 403 with password_rotation_required → user must rotate before continuing. Same
    // exemption: /api/auth/change-password is the rotation itself, so don't bounce on
    // its own response.
    if (response.status === 403 && code === "password_rotation_required" && !isAnonymousSurface(path)) redirectToPasswordRotation();

    throw new ApiError(response.status, code, message, parsed);
  }

  return parsed as T;
}

/**
 * Endpoints reachable without a session: sign-in / rotation, and invitation preview + accept, where
 * the credential is the token in the URL rather than a stored JWT.
 */
function isAnonymousSurface(path: string): boolean {
  return path.startsWith("/api/auth/") || path.startsWith("/api/invitations/");
}

function safeJsonParse(text: string): unknown {
  try { return JSON.parse(text); } catch { return text; }
}

function handleUnauthorized() {
  if (typeof window === "undefined") return;

  const currentPath = window.location.pathname;
  // No redirect loop: if we're already on /signin or the OAuth popup callback page,
  // leave the page alone so the user can recover via the form / popup as designed.
  if (currentPath === "/signin" || currentPath === "/oauth-callback") return;

  localStorage.removeItem("codespace.jwt");
  localStorage.removeItem("codespace.activeTeamId");
  window.location.assign("/signin");
}

function redirectToPasswordRotation() {
  if (typeof window === "undefined") return;
  if (window.location.pathname === "/change-password") return;
  window.location.assign("/change-password");
}
