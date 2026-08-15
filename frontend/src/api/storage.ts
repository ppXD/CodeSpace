import { fetchJson } from "./request";

/** One installed storage provider type. Schemas describe future profile forms and never contain secret values. */
export interface StorageProviderModuleSummary {
  typeKey: string;
  displayName: string;
  configSchema: Record<string, unknown>;
  secretSchema: Record<string, unknown>;
  capabilities: string[];
}

export const storageApi = {
  listProviderModules: () => fetchJson<StorageProviderModuleSummary[]>("/api/storage/provider-modules"),
};
