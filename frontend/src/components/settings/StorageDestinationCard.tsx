import { useEffect, useMemo, useRef, useState } from "react";

import { ApiError } from "@/api/request";
import type { ArtifactLocationState, ProfilePlacementTotal, StorageCredentialMetadata, StorageProfileDetail, StorageProfileSummary, StorageProviderModuleSummary } from "@/api/storage";
import type { RoutedDataClass, StorageRouteSummary } from "@/api/storageRoutes";
import { useProbeStorageProfile, useProfilePlacementTotals, useStorageProfile } from "@/hooks/use-storage";
import { credentialForRef } from "@/lib/storageCredentialRef";
import { RowMenu } from "@/components/settings/RowMenu";

import { StorageHealthBadge, when } from "./StorageHealthBadge";
import { probeFailureGuidance, probeFailureReference } from "./storageProbeGuidance";

/**
 * One place this team's data is kept, as one card.
 *
 * Three control-plane rows sit underneath — a credential, a profile, and a route per data class — and the card
 * deliberately shows them as one thing, because nothing an operator decides is expressed by having three of them.
 * The words profile, credential, route and revision do not appear; the lifecycle they belong to lives behind the
 * "…" menu, which is where it is a decision rather than a vocabulary lesson.
 */
export function StorageDestinationCard({ profile, providers, credentials, routes, dataClasses, mayManage, onFix, onEditRouting, onAdvanced }: {
  profile: StorageProfileSummary;
  providers: StorageProviderModuleSummary[];
  credentials: StorageCredentialMetadata[];
  routes: StorageRouteSummary[];
  dataClasses: RoutedDataClass[];
  mayManage: boolean;
  onFix: () => void;
  onEditRouting: () => void;
  onAdvanced: () => void;
}) {
  const detail = useStorageProfile(profile.id);
  const totals = useProfilePlacementTotals(profile.id);
  const probe = useProbeStorageProfile();
  const [probeError, setProbeError] = useState<string | null>(null);
  // The check writes and removes a real object, so an unmounted card must stop waiting for one rather than settle
  // into state nothing is rendering.
  const probeController = useRef<AbortController | null>(null);
  useEffect(() => () => probeController.current?.abort(), []);

  const provider = providers.find((candidate) => candidate.typeKey === profile.providerTypeKey);
  const current = currentRevision(detail.data, profile.currentRevision);
  const landing = useMemo(() => landsHere(profile.id, routes, dataClasses), [profile.id, routes, dataClasses]);
  const stored = useMemo(() => storedHere(totals.data), [totals.data]);
  const credential = useMemo(() => credentialForRef(current?.credentialRef, credentials), [current?.credentialRef, credentials]);
  const failing = profile.health != null && profile.health.status !== "Available";

  return (
    <div className="cn-list" style={{ marginBottom: 12 }}>
      <div className="cn-row">
        <div className="cn-row-head">
          <div className="cn-meta">
            <div className="cn-name">
              {profile.stableName}
              <StorageHealthBadge health={profile.health} currentRevision={profile.currentRevision} />
            </div>
            <div className="cn-sub">{address(provider, current)}</div>
          </div>
          {mayManage && (
            <>
              {failing && <button type="button" className="btn" onClick={onFix}>Fix the connection</button>}
              <RowMenu label={`Actions for ${profile.stableName}`}>
                {(close) => (
                  <>
                    <button
                      type="button"
                      className="sb-pop-item"
                      onClick={() => {
                        close();
                        setProbeError(null);
                        probeController.current?.abort();
                        probeController.current = new AbortController();
                        probe.mutate(
                          { profileId: profile.id, input: { profileRevision: null, verifyWriteAccess: true }, signal: probeController.current.signal },
                          { onError: (error) => setProbeError(error instanceof ApiError ? error.message : "The check could not be run.") },
                        );
                      }}
                    >
                      Check it now
                    </button>
                    <button type="button" className="sb-pop-item" onClick={() => { close(); onFix(); }}>Change the connection…</button>
                    <button type="button" className="sb-pop-item" onClick={() => { close(); onEditRouting(); }}>What lands here…</button>
                    <button type="button" className="sb-pop-item" onClick={() => { close(); onAdvanced(); }}>Change history and lifecycle…</button>
                  </>
                )}
              </RowMenu>
            </>
          )}
        </div>

        {/* The failing state's own sentence. A red chip says something is wrong; only this says which end to fix. */}
        {failing && profile.health?.failureCode && (
          <div className="cn-sub" style={{ color: "var(--danger)", marginTop: 8 }}>
            {probeFailureGuidance(profile.health.failureCode) ?? "The destination did not answer."}
            {profile.health.failureStage && ` Reported as ${probeFailureReference({ stage: profile.health.failureStage, code: profile.health.failureCode, retryable: false })}.`}
          </div>
        )}

        {probeError && <div className="cn-sub" style={{ color: "var(--danger)", marginTop: 8 }}>{probeError}</div>}

        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 14, marginTop: 12 }}>
          <Fact label="Lands here">{landing.length > 0 ? landing.join(", ") : "Nothing yet"}</Fact>
          <Fact label="Stored here">{totals.isLoading ? "…" : stored}</Fact>
          <Fact label="Access key">{credential?.safeHint ?? credential?.stableName ?? (provider && !providerTakesSecret(provider) ? "None needed" : "—")}</Fact>
          <Fact label="Last checked">{profile.health ? when(profile.health.observedAt) : "Never"}</Fact>
        </div>
      </div>
    </div>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <div className="wf-form-label" style={{ fontSize: 11, textTransform: "uppercase", letterSpacing: ".06em" }}>{label}</div>
      <div className="cn-sub" style={{ marginTop: 2 }}>{children}</div>
    </div>
  );
}

/**
 * The address, built from the provider's own configuration schema rather than from knowledge of any provider: its
 * display name, then every string it declares, in the order it declares them. A provider that ships tomorrow reads
 * correctly here with no change.
 */
function address(provider: StorageProviderModuleSummary | undefined, revision: StorageProfileDetail["revisions"][number] | undefined): string {
  if (!provider) return revision?.providerTypeKey ?? "";
  const properties = isRecord(provider.configSchema) && isRecord(provider.configSchema.properties) ? provider.configSchema.properties : {};
  const config = isRecord(revision?.nonSecretConfig) ? (revision!.nonSecretConfig as Record<string, unknown>) : {};
  const parts = Object.keys(properties)
    .map((name) => config[name])
    .filter((value): value is string => typeof value === "string" && value.length > 0);

  return [provider.displayName, ...parts].join(" · ");
}

/** Which data classes' NEXT write lands here. Only an Active route sends anything, so only Active counts. */
function landsHere(profileId: string, routes: StorageRouteSummary[], dataClasses: RoutedDataClass[]): string[] {
  return routes
    .filter((route) => route.state === "Active" && route.storageProfileId === profileId)
    .map((route) => dataClasses.find((dataClass) => dataClass.typeKey === route.dataClassTypeKey)?.displayName ?? route.dataClassTypeKey);
}

/**
 * What is actually here, counting only what a read could still open. Purged and Deleted bytes are gone, and counting
 * them would tell an operator this destination holds data it does not.
 */
function storedHere(totals: ProfilePlacementTotal[] | undefined): string {
  if (!totals) return "—";
  const gone: ArtifactLocationState[] = ["Purged", "Deleted"];
  const live = totals.filter((total) => !gone.includes(total.state));
  const count = live.reduce((sum, total) => sum + total.count, 0);
  const bytes = live.reduce((sum, total) => sum + total.sizeBytes, 0);

  return count === 0 ? "Nothing yet" : `${count.toLocaleString()} object${count === 1 ? "" : "s"} · ${size(bytes)}`;
}

function size(bytes: number): string {
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return `${unit === 0 ? value : value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

function currentRevision(detail: StorageProfileDetail | undefined, revision: number) {
  return detail?.revisions.find((candidate) => candidate.revision === revision) ?? detail?.revisions[0];
}


function providerTakesSecret(provider: StorageProviderModuleSummary): boolean {
  return isRecord(provider.secretSchema) && isRecord(provider.secretSchema.properties) && Object.keys(provider.secretSchema.properties).length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
