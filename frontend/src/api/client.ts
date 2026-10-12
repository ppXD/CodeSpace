import createClient, { type Middleware } from "openapi-fetch";

import type { paths } from "./schema";
import { applySessionHeaders } from "./session";

/**
 * Session credentials. The session itself is the HttpOnly cookie the browser attaches (`credentials: "include"`
 * below); this adds the CSRF header, and the bearer token only while this browser is not yet on the cookie.
 * session.ts is the only place that knows where the session lives.
 */
const authMiddleware: Middleware = {
  async onRequest({ request }) {
    applySessionHeaders(request.headers);
    return request;
  },
};

/**
 * X-Team-Id header injector. Every team-scoped backend endpoint (IRequireTeamMembership /
 * IRequireRepositoryAccess / IRequireCredentialAccess) reads team from this header instead
 * of the request body. Frontend keeps the "active team" id in localStorage; the team picker
 * UI writes to it. Auth / admin endpoints ignore the header server-side.
 */
const teamMiddleware: Middleware = {
  async onRequest({ request }) {
    const teamId = localStorage.getItem("codespace.activeTeamId");
    if (teamId) request.headers.set("X-Team-Id", teamId);
    return request;
  },
};

export const api = createClient<paths>({
  // Empty default → relative URLs → Vite proxies /api to the backend (see vite.config.ts).
  // Set VITE_API_URL=http://host:port in .env.local to bypass the proxy and hit a backend
  // directly (e.g. another machine, custom port). See .env.example for the full notes.
  baseUrl: import.meta.env.VITE_API_URL ?? "",
  // The session cookie must travel to an API on another origin too — the deployed image's shape (see request.ts).
  credentials: "include",
});

api.use(authMiddleware);
api.use(teamMiddleware);
