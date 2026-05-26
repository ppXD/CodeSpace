import createClient, { type Middleware } from "openapi-fetch";

import type { paths } from "./schema";

/**
 * Bearer token injector. JWT is stored in localStorage. Keep this as the only place that
 * knows where the token lives.
 */
const authMiddleware: Middleware = {
  async onRequest({ request }) {
    const token = localStorage.getItem("codespace.jwt");
    if (token) request.headers.set("Authorization", `Bearer ${token}`);
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
});

api.use(authMiddleware);
api.use(teamMiddleware);
