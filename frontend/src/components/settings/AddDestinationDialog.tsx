import { useMemo, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import { storageApi, type StorageConfigurationProbeResult, type StorageProviderModuleSummary } from "@/api/storage";
import { storageRouteApi, type RoutedDataClass } from "@/api/storageRoutes";
import { SchemaForm } from "@/components/workflows/SchemaForm";

import { probeFailureGuidance, probeFailureReference } from "./storageProbeGuidance";

/**
 * Setting up a place to keep this team's data, as three questions instead of four modals.
 *
 * The shape is load-bearing rather than cosmetic. A storage profile cannot be deleted, so the old flow — create a
 * credential, create a profile, then test — charged a permanent, un-removable row for a mistyped secret, and the only
 * way forward was to learn a lifecycle vocabulary and build a second of everything. Here the test comes FIRST and
 * against the real destination, so a wrong key costs a retry; and the recording is one transaction, so there is no
 * half-built state to reason about either.
 *
 * None of the words profile, credential, route, revision, draft or active appears on screen. They are all still true
 * underneath; none of them is a decision an operator makes.
 */
export function AddDestinationDialog({ providers, onClose, onCreated }: { providers: StorageProviderModuleSummary[]; onClose: () => void; onCreated: () => void }) {
  const queryClient = useQueryClient();
  const [providerTypeKey, setProviderTypeKey] = useState(providers[0]?.typeKey ?? "");
  const [config, setConfig] = useState<Record<string, unknown>>({});
  const [secret, setSecret] = useState<Record<string, unknown>>({});
  const [name, setName] = useState("");
  const [nameEdited, setNameEdited] = useState(false);
  const [claimed, setClaimed] = useState<string[]>([]);
  const [qualified, setQualified] = useState<StorageConfigurationProbeResult | null>(null);

  const provider = useMemo(() => providers.find((candidate) => candidate.typeKey === providerTypeKey), [providers, providerTypeKey]);
  const dataClasses = useQuery({ queryKey: ["storage", "data-classes"], queryFn: ({ signal }) => storageRouteApi.listDataClasses(signal) });

  // The name is derived from whatever the provider says identifies its namespace — the bucket, the root path — so the
  // operator types nothing extra, and can still overrule it.
  const derivedName = useMemo(() => deriveName(provider, config), [provider, config]);
  const effectiveName = nameEdited ? name : derivedName;

  const probe = useMutation({
    mutationFn: () => storageApi.probeConfiguration({ providerTypeKey, nonSecretConfig: config, secret: hasSecretInputs(provider) ? secret : null }),
    onSuccess: (result) => setQualified(result.status === "Available" ? result : null),
  });

  const create = useMutation({
    mutationFn: () => storageApi.createDestination({
      name: effectiveName,
      providerTypeKey,
      nonSecretConfig: config,
      secret: hasSecretInputs(provider) ? secret : null,
      safeHint: safeHint(provider, secret),
      dataClassTypeKeys: claimed,
    }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["storage"] });
      onCreated();
    },
  });

  const answered = probe.data;
  const step: Step = qualified ? "use" : answered && answered.status !== "Available" ? "refused" : "connect";
  const canTest = Boolean(provider) && requiredValuesPresent(provider?.configSchema, config) && (!requiresSecret(provider) || requiredValuesPresent(provider?.secretSchema, secret));

  return (
    <Frame onClose={onClose} step={step}>
      {step === "connect" && (
        <>
          <div className="wf-form">
            {providers.length > 1 && (
              <div className="wf-form-row">
                <label className="wf-form-label" htmlFor="destination-provider">Where should this team&rsquo;s data be written?</label>
                <select
                  id="destination-provider"
                  className="wf-form-input"
                  value={providerTypeKey}
                  onChange={(event) => { setProviderTypeKey(event.target.value); setConfig({}); setSecret({}); probe.reset(); }}
                >
                  {providers.map((candidate) => <option key={candidate.typeKey} value={candidate.typeKey}>{candidate.displayName}</option>)}
                </select>
              </div>
            )}

            {provider && <SchemaForm schema={provider.configSchema} value={config} onChange={setConfig} />}
            {provider && hasSecretInputs(provider) && <SchemaForm schema={provider.secretSchema} value={secret} onChange={setSecret} sensitive />}

            <div className="wf-form-row">
              <label className="wf-form-label" htmlFor="destination-name">Name</label>
              <input
                id="destination-name"
                className="wf-form-input"
                value={effectiveName}
                placeholder={derivedName}
                onChange={(event) => { setNameEdited(true); setName(event.target.value); }}
              />
              <span className="wf-form-help">What this place is called on this screen. Lowercase letters, digits and hyphens. It cannot be renamed later.</span>
            </div>
          </div>

          {probe.error instanceof ApiError && <Banner title="Couldn&rsquo;t run the test">{probe.error.message}</Banner>}

          <p className="wf-form-help" style={{ marginTop: 14 }}>
            Nothing is saved until this test writes an object to the destination and reads it back.
          </p>
        </>
      )}

      {step === "refused" && answered?.failure && (
        <>
          <div className="cn-banner cn-banner-err" role="alert">
            <div className="cn-banner-h">That didn&rsquo;t work.</div>
            <div className="cn-banner-p">{probeFailureGuidance(answered.failure.code) ?? "The destination did not answer, and gave no reason this build can act on."}</div>
          </div>
          <p className="wf-form-help" style={{ marginTop: 12 }}>Reported as {probeFailureReference(answered.failure)}.</p>
          <p className="wf-form-help" style={{ marginTop: 10 }}>
            <strong>Nothing was saved.</strong> There is no destination and no key &mdash; this test wrote nothing anywhere, including at the destination itself.
          </p>
        </>
      )}

      {step === "use" && qualified && (
        <>
          <div className="cn-banner" role="status">
            <div className="cn-banner-h">{effectiveName} answered.</div>
            <div className="cn-banner-p">It wrote a test object, read it back and removed it. {qualified.latencyMilliseconds}&thinsp;ms.</div>
          </div>

          <div className="wf-form" style={{ marginTop: 16 }}>
            <div className="wf-form-row">
              <span className="wf-form-label">What lands here?</span>
              <span className="wf-form-help">Nothing is ticked for you. Each choice moves where NEW writes go &mdash; data already stored stays where it is.</span>
            </div>

            {dataClasses.isLoading && <span className="wf-form-help">Loading&hellip;</span>}
            {dataClasses.data?.map((dataClass: RoutedDataClass) => (
              <label key={dataClass.typeKey} className="wf-form-row" style={{ flexDirection: "row", alignItems: "flex-start", gap: 9 }}>
                <input
                  type="checkbox"
                  checked={claimed.includes(dataClass.typeKey)}
                  onChange={(event) => setClaimed((current) => event.target.checked ? [...current, dataClass.typeKey] : current.filter((key) => key !== dataClass.typeKey))}
                />
                <span>
                  <span className="cn-name" style={{ fontSize: 13 }}>{dataClass.displayName}</span>
                  <span className="wf-form-help" style={{ display: "block" }}>{unroutedToday(dataClass)}</span>
                </span>
              </label>
            ))}
          </div>

          {create.error instanceof ApiError && <Banner title="Couldn&rsquo;t save the destination">{create.error.message}</Banner>}
        </>
      )}

      <div className="mdl-foot">
        <button type="button" className="btn" onClick={step === "refused" ? () => probe.reset() : onClose}>
          {step === "refused" ? "Change the connection details" : "Cancel"}
        </button>
        {step === "connect" && (
          <button type="button" className="btn btn-primary" disabled={!canTest || probe.isPending} onClick={() => probe.mutate()}>
            {probe.isPending ? "Testing…" : "Test connection"}
          </button>
        )}
        {step === "refused" && (
          <button type="button" className="btn btn-primary" disabled={probe.isPending} onClick={() => probe.mutate()}>
            {probe.isPending ? "Testing…" : "Test again"}
          </button>
        )}
        {step === "use" && (
          <button type="button" className="btn btn-primary" disabled={create.isPending} onClick={() => create.mutate()}>
            {create.isPending ? "Saving…" : "Start storing here"}
          </button>
        )}
      </div>
    </Frame>
  );
}

type Step = "connect" | "refused" | "use";

/** The rail names the three questions, so an operator can see there are only three before answering the first. */
function Frame({ step, onClose, children }: { step: Step; onClose: () => void; children: ReactNode }) {
  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label="Add a destination">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">Add a destination</div>
            <div className="mdl-sub">
              <span style={{ color: step === "connect" ? "var(--accent)" : "var(--muted-2)" }}>Connect</span>
              {" · "}
              <span style={{ color: step === "refused" ? "var(--accent)" : "var(--muted-2)" }}>Test</span>
              {" · "}
              <span style={{ color: step === "use" ? "var(--accent)" : "var(--muted-2)" }}>Use</span>
            </div>
          </div>
          <button type="button" className="mdl-x" aria-label="Close" onClick={onClose}>&times;</button>
        </div>
        <div className="mdl-body">{children}</div>
      </div>
    </>,
    document.body,
  );
}

function Banner({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 14 }}>
      <div className="cn-banner-h">{title}</div>
      <div className="cn-banner-p">{children}</div>
    </div>
  );
}

/**
 * What happens to a class the operator does NOT tick, said plainly. The difference between the two sentences is not
 * cosmetic: one says the data has a home already, the other says it is being dropped.
 */
function unroutedToday(dataClass: RoutedDataClass): string {
  return dataClass.hasLocalFallback
    ? "Currently written to this server's own disk. Ones already there stay there and keep opening — but they can never move back once this is on."
    : "Not captured at all today. Ticking this is what starts capturing them; leaving it alone changes nothing.";
}

/** The provider's own namespace field is the name an operator would have typed anyway. */
function deriveName(provider: StorageProviderModuleSummary | undefined, config: Record<string, unknown>): string {
  const property = provider?.teamNamespaceProperty;
  const value = property ? config[property] : undefined;
  if (typeof value !== "string") return "";
  const slug = value.trim().toLowerCase().replace(/^[a-z]+:\/\//, "").replace(/[^a-z0-9-]+/g, "-").replace(/^-+|-+$/g, "");
  return slug.slice(0, 128);
}

/** A non-secret reminder of WHICH key this is. Built from the first non-secret-looking string the operator gave. */
function safeHint(provider: StorageProviderModuleSummary | undefined, secret: Record<string, unknown>): string | null {
  const properties = schemaProperties(provider?.secretSchema);
  for (const [key, declared] of Object.entries(properties)) {
    if (isRecord(declared) && declared.writeOnly === true) continue;
    const value = secret[key];
    if (typeof value === "string" && value.length > 0) return value.length <= 12 ? value : `${value.slice(0, 7)}…${value.slice(-4)}`;
  }
  return null;
}

function requiresSecret(provider: StorageProviderModuleSummary | undefined): boolean {
  const required = provider?.secretSchema?.required;
  return Array.isArray(required) && required.some((value) => typeof value === "string");
}

function hasSecretInputs(provider: StorageProviderModuleSummary | undefined): boolean {
  return Object.keys(schemaProperties(provider?.secretSchema)).length > 0;
}

function schemaProperties(schema: unknown): Record<string, unknown> {
  return isRecord(schema) && isRecord(schema.properties) ? schema.properties : {};
}

function requiredValuesPresent(schema: unknown, value: Record<string, unknown>): boolean {
  if (!isRecord(schema)) return true;
  const required = Array.isArray(schema.required) ? schema.required : [];
  return required.every((name) => typeof name === "string" && typeof value[name] === "string" && (value[name] as string).trim().length > 0);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
