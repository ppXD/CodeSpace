import { ApiError } from "@/api/request";
import type { StorageProviderModuleSummary } from "@/api/storage";
import { useStorageProviderModules } from "@/hooks/use-storage";

/**
 * Settings → Storage provider catalog.
 *
 * This remains read-only. Provider modules are capabilities installed in this deployment, not active profiles.
 * Runtime mutation is enabled only after encrypted credentials and cross-role readback qualification exist, keeping
 * current workflow-run persistence byte-for-byte on the deployment-managed backend during the additive migration.
 */
export function StorageSettings() {
  const providers = useStorageProviderModules();
  const rows = providers.data ?? [];
  const errorMessage = providers.error instanceof ApiError
    ? providers.error.message
    : providers.error instanceof Error ? providers.error.message : null;

  return (
    <div aria-labelledby="storage-settings-title">
      <div className="cn-banner" style={{ margin: 16 }}>
        <h2 className="cn-banner-h" id="storage-settings-title">Artifact storage</h2>
        <div className="cn-banner-p">
          Existing workflow runs continue to use the deployment-managed artifact store. No runtime storage profile is
          active until its provider, credentials, durability, and cross-service readback have been qualified.
        </div>
      </div>

      {providers.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading storage providers…</div></div>}

      {errorMessage && (
        <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
          <div className="cn-banner-h">Couldn't load storage providers</div>
          <div className="cn-banner-p">{errorMessage}</div>
        </div>
      )}

      {!providers.isLoading && !errorMessage && rows.length === 0 && (
        <div className="ct-empty">
          <div className="ct-empty-h">No storage provider modules installed</div>
          <div className="ct-empty-p">
            Provider packages will appear here when installed. This does not change where current run artifacts, model
            calls, or logs are written.
          </div>
        </div>
      )}

      {!providers.isLoading && !errorMessage && rows.length > 0 && (
        <div className="cn-list" style={{ margin: 16 }}>
          {rows.map((provider) => <StorageProviderRow key={provider.typeKey} provider={provider} />)}
        </div>
      )}
    </div>
  );
}

function StorageProviderRow({ provider }: { provider: StorageProviderModuleSummary }) {
  const secretProperties = schemaProperties(provider.secretSchema);

  return (
    <div className="cn-row">
      <div className="cn-row-head">
        <div className="cn-mark">{providerInitials(provider.displayName)}</div>
        <div className="cn-meta" style={{ flex: 1 }}>
          <div className="cn-name">
            {provider.displayName}
            <span className="cn-status">{provider.typeKey}</span>
            <span className="cn-status cn-status-active"><span className="cn-status-dot" /> Profile schema ready</span>
            {secretProperties.length === 0 && <span className="cn-status">No secret inputs</span>}
          </div>
          <div className="cn-sub" aria-label={`${provider.displayName} capabilities`}>
            {provider.capabilities.length === 0
              ? "No optional capabilities declared"
              : provider.capabilities.map(capabilityLabel).join(" · ")}
          </div>
        </div>
      </div>
    </div>
  );
}

function schemaProperties(schema: Record<string, unknown>): string[] {
  const properties = schema.properties;
  return properties != null && typeof properties === "object" && !Array.isArray(properties)
    ? Object.keys(properties)
    : [];
}

function providerInitials(displayName: string): string {
  const words = displayName.match(/[A-Za-z0-9]+/g) ?? [];
  return words.slice(0, 2).map((word) => word[0]).join("").toUpperCase() || "ST";
}

function capabilityLabel(capability: string): string {
  const words = capability.replace(/([a-z0-9])([A-Z])/g, "$1 $2").toLowerCase();
  return words.replace(/^./, (value) => value.toUpperCase());
}
