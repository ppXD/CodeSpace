/**
 * Settings → Storage foundation.
 *
 * This first slice intentionally exposes no mutation controls. Runtime-managed profiles are an additive contract
 * that will be enabled only after the provider catalog, encrypted credentials, and cross-role readback probe are
 * available. Keeping the current deployment storage authoritative avoids silently changing artifact durability for
 * active workflow runs while that contract is being built.
 */
export function StorageSettings() {
  return (
    <div aria-labelledby="storage-settings-title">
      <div className="cn-banner" style={{ margin: 16 }}>
        <h2 className="cn-banner-h" id="storage-settings-title">Artifact storage</h2>
        <div className="cn-banner-p">
          Existing workflow runs continue to use the deployment-managed artifact store. No runtime storage profile is
          active until its provider, credentials, durability, and cross-service readback have been qualified.
        </div>
      </div>

      <div className="ct-empty">
        <div className="ct-empty-h">No runtime-managed storage profiles yet</div>
        <div className="ct-empty-p">
          Provider-backed profiles and policies will appear here after the storage module contract is available. This
          page does not change where current run artifacts, model calls, or logs are written.
        </div>
      </div>
    </div>
  );
}
