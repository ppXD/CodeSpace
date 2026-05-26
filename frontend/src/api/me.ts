import { fetchJson } from "./request";
import type { MeResponse } from "./types";

export const meApi = {
  me: () => fetchJson<MeResponse>("/api/users/me"),
};
