import { fetchJson } from "@/api/request";
import type { StorageProviderModuleSummary } from "@/api/storage";
import type { RoutedDataClass } from "@/api/storageRoutes";

/**
 * The same catalog the team screen reads, under the deployment capability.
 *
 * <p>Separate because an operator who authors templates need not belong to any team: reaching the
 * team-scoped list from the admin screen would make the catalog unreadable for exactly the person the
 * screen is for, and would route it through a controller where the ambient `X-Team-Id` header is live.</p>
 */
export const storageDefaultProvidersApi = {
  list: (signal?: AbortSignal) => fetchJson<StorageProviderModuleSummary[]>("/api/admin/storage-defaults/provider-modules", { signal }),
  listDataClasses: (signal?: AbortSignal) => fetchJson<RoutedDataClass[]>("/api/admin/storage-defaults/data-classes", { signal }),
};
