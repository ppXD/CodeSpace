/**
 * Where this browser's session lives, and the credentials every API request carries. The only module that knows.
 *
 * The session is an HttpOnly cookie the API sets when it mints one (sign-in, invitation accept, password change). The
 * browser attaches it to API requests made with `credentials: "include"`, and no script on the page — ours or an
 * injected one — can read it. Two things stay in localStorage, and neither is the session itself:
 *   • `codespace.session = "cookie"` — the API confirmed the cookie, so the route guards answer "signed in?" without a
 *     round trip. It is a hint, not a credential: an expired or revoked cookie surfaces as the first 401, exactly as an
 *     expired token always did.
 *   • `codespace.jwt` — the bearer token, ONLY while the API has not confirmed a cookie session: a browser signed in
 *     before the cookie existed (until its one-time exchange below succeeds), an API pod that predates the cookie
 *     during a rolling deploy, or a browser that will not send the cookie back (an API on another site, where
 *     SameSite=Strict withholds it). Keeping it then is what stops any of those from signing the user out.
 *
 * Nor does the token linger anywhere else the page keeps state: a response that carries one is settled here and handed
 * on without it (`adoptSessionFrom`), and the API leaves it out of the body altogether when the cookie carried the request.
 */

const baseUrl: string = import.meta.env.VITE_API_URL ?? "";

const SESSION_PATH = "/api/auth/session";
const LEGACY_TOKEN_KEY = "codespace.jwt";
const SESSION_KEY = "codespace.session";
const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

/**
 * Sent on every request. The API refuses a write authenticated by the cookie alone without it: a form on another site,
 * or a fetch it makes without a preflight, can make the browser send the cookie but cannot set a header.
 */
export const CSRF_HEADER = "X-CodeSpace-Csrf";

/** Signed in, as far as this browser knows: a confirmed cookie session, or a bearer token not yet moved onto one. */
export function isAuthenticated(): boolean {
  return localStorage.getItem(SESSION_KEY) === "cookie" || localStorage.getItem(LEGACY_TOKEN_KEY) != null;
}

/**
 * The headers a request needs on top of the cookie the browser attaches itself: the CSRF header, and — only while this
 * browser is not on the cookie — its bearer token. `bearer: false` withholds the token from a request that must go
 * anonymous whatever this browser holds.
 */
export function applySessionHeaders(headers: Headers, options: { bearer?: boolean } = {}): void {
  headers.set(CSRF_HEADER, "1");

  const legacy = localStorage.getItem(LEGACY_TOKEN_KEY);
  if (legacy && options.bearer !== false) headers.set("Authorization", `Bearer ${legacy}`);
}

/**
 * Settles where a session the API just handed this browser lives. On the cookie when the API confirms it — and then the
 * token is never written anywhere a script can read — otherwise kept as the bearer token, exactly as before.
 *
 * Confirming takes two requests. POST /api/auth/session presents the token and has the API set the cookie with it (a
 * sign-in response normally set it already; it is the same call that moves an older browser over). GET
 * /api/auth/session WITHOUT the token then proves the browser actually sends the cookie back. Anything else — a 404
 * from an API pod that predates the cookie, a 401, no network — leaves the bearer path in place.
 *
 * No token means the API already answered on the cookie: it leaves the token out of the body only for a request the
 * cookie carried. That is the confirmation, so nothing is asked — a probe meeting a pre-cookie pod mid-deploy would
 * answer 401 for a session that is fine — and there is no token to fall back to.
 */
export async function adoptSession(token?: string): Promise<void> {
  if (!token || (await cookieCarriesSession(token))) {
    localStorage.removeItem(LEGACY_TOKEN_KEY);
    localStorage.setItem(SESSION_KEY, "cookie");
    return;
  }

  localStorage.setItem(LEGACY_TOKEN_KEY, token);
  localStorage.removeItem(SESSION_KEY);
}

/**
 * Settles the session a response carries and returns the response WITHOUT its token, for the page to keep. Whatever
 * the page keeps — react-query holds a mutation's result for minutes, reachable from the DOM — must never hold the
 * token, or a script finds it there instead of in localStorage.
 */
export async function adoptSessionFrom<T extends { token?: string }>(response: T): Promise<Omit<T, "token">> {
  const { token, ...rest } = response;

  await adoptSession(token);

  return rest;
}

/**
 * Whether this browser's cookie session is still live, asked of the API with the cookie alone. A browser not on the
 * cookie answers false without asking. Asked after a 401: mid rolling deploy, an API pod from before the cookie reads
 * only the Authorization header and refuses a session that is fine; a live answer here means the 401 met such a pod.
 */
export async function cookieSessionLive(): Promise<boolean> {
  if (localStorage.getItem(SESSION_KEY) !== "cookie") return false;

  return probeCookie();
}

/**
 * Moves a browser signed in before the cookie existed onto it: its token is exchanged for the cookie once, then
 * deleted. Until that succeeds the token keeps working as the bearer it always was, so nobody is signed out by the
 * deploy. Runs at startup; requests already in flight carry the token and still succeed.
 */
export async function migrateLegacySession(): Promise<void> {
  const legacy = localStorage.getItem(LEGACY_TOKEN_KEY);
  if (legacy) await adoptSession(legacy);
}

/**
 * Signs this browser out. Only the API can clear an HttpOnly cookie, so it is asked to; local state goes whatever it
 * answers — the person asked to be signed out of this page, and an unreachable API must not keep them in.
 */
export async function signOut(): Promise<void> {
  const headers = new Headers();
  applySessionHeaders(headers);

  try {
    await fetch(baseUrl + SESSION_PATH, { method: "DELETE", credentials: "include", headers });
  } catch {
    // Signed out locally regardless; a cookie the API did not clear dies with its token.
  }

  clearAuthState();
}

/** Forgets this browser's session and active team. The cookie, if any, is the API's to clear (see `signOut`). */
export function clearAuthState(): void {
  localStorage.removeItem(LEGACY_TOKEN_KEY);
  localStorage.removeItem(SESSION_KEY);
  localStorage.removeItem(ACTIVE_TEAM_STORAGE_KEY);
}

async function cookieCarriesSession(token: string): Promise<boolean> {
  try {
    const exchanged = await fetch(baseUrl + SESSION_PATH, { method: "POST", credentials: "include", headers: { Authorization: `Bearer ${token}`, [CSRF_HEADER]: "1" } });
    if (!exchanged.ok) return false;
  } catch {
    return false;
  }

  return probeCookie();
}

/** GET /api/auth/session with no Authorization header: a 204 proves the browser holds and sends a live session cookie. */
async function probeCookie(): Promise<boolean> {
  try {
    const probe = await fetch(baseUrl + SESSION_PATH, { method: "GET", credentials: "include", headers: { [CSRF_HEADER]: "1" } });
    return probe.ok;
  } catch {
    return false;
  }
}
