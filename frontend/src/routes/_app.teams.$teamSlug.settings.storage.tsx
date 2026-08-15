import { createFileRoute } from "@tanstack/react-router";

import { StorageSettings } from "@/components/settings/StorageSettings";

/** Settings → Storage. Runtime-managed artifact storage profiles and durability policies. */
export const Route = createFileRoute("/_app/teams/$teamSlug/settings/storage")({
  component: StorageSettings,
});
