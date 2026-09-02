import { useMemo, useState, type ReactNode, useRef } from "react";
import { createPortal } from "react-dom";

import { useDialogKeys } from "./useDialogKeys";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import { storageApi, type StorageConfigurationProbeResult, type StorageProviderModuleSummary } from "@/api/storage";
import { storageRouteApi, type RoutedDataClass, type StorageRouteSummary } from "@/api/storageRoutes";
import { deriveSecretHint } from "@/lib/storageSecretHint";
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
  // A provider that never accepts new bytes is not somewhere data can be sent: route binding refuses it by
  // declaration, so offering it walks an operator through four fields and refuses them at the last step.
  const offerable = useMemo(() => providers.filter((candidate) => !candidate.acceptsNoNewBytes), [providers]);
  const [providerTypeKey, setProviderTypeKey] = useState(offerable[0]?.typeKey ?? "");
  const [config, setConfig] = useState<Record<string, unknown>>({});
  const [secret, setSecret] = useState<Record<string, unknown>>({});
  const [name, setName] = useState("");
  const [nameEdited, setNameEdited] = useState(false);
  const [claimed, setClaimed] = useState<string[]>([]);
  const [qualified, setQualified] = useState<StorageConfigurationProbeResult | null>(null);

  const provider = useMemo(() => offerable.find((candidate) => candidate.typeKey === providerTypeKey), [offerable, providerTypeKey]);
  const dataClasses = useQuery({ queryKey: ["storage", "data-classes"], queryFn: ({ signal }) => storageRouteApi.listDataClasses(signal) });
  // Where each class lands TODAY. Without it a class already routed elsewhere reads as unrouted, and ticking it -
  // which the server performs as a REPOINT - takes it from another destination with nothing on screen saying so.
  const routes = useQuery({ queryKey: ["storage", "routes", "for-add"], queryFn: ({ signal }) => storageRouteApi.listPage(null, 50, signal) });

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
      safeHint: deriveSecretHint(provider?.secretSchema, secret),
      dataClassTypeKeys: claimed,
    }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["storage"] });
      onCreated();
    },
  });

  const answered = probe.data;
  const step: Step = qualified ? "use" : answered && answered.status !== "Available" ? "refused" : "connect";
  const nameValid = NAME_PATTERN.test(effectiveName);
  const canTest = Boolean(provider) && requiredValuesPresent(provider?.configSchema, config) && (!requiresSecret(provider) || requiredValuesPresent(provider?.secretSchema, secret));

  // The promise sits beside the button that acts on it. At the bottom of a scrolling form it was the first thing an
  // operator scrolled past, which is the one sentence that decides whether a typo is expensive. Short, because what
  // the test actually DOES is worth saying where it has just been proven - the Use step's banner says it. The
  // refused step says
  // nothing here: its body already leads with "Nothing was saved", and saying it twice reads as reassurance rather
  // than as fact.
  const footer = (
    <div className="mdl-foot">
      <span className="wf-form-help" style={{ maxWidth: "46ch" }}>
        {step === "connect" && "Nothing is saved until this test passes."}
        {step === "use" && (nameValid ? "Only what you tick starts landing here." : "Give this place a name: lowercase letters, digits and hyphens.")}
      </span>
      <span style={{ display: "flex", gap: 10 }}>
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
        <button type="button" className="btn btn-primary" disabled={!nameValid || create.isPending} onClick={() => create.mutate()}>
          {create.isPending ? "Saving…" : "Start storing here"}
        </button>
      )}
      </span>
    </div>
  );

  return (
    <Frame onClose={onClose} step={step} footer={footer}>
      {step === "connect" && (
        <>
          <div className="wf-form">
            {offerable.length > 1 && (
              <div className="wf-form-row">
                <label className="wf-form-label" htmlFor="destination-provider">Where should this team&rsquo;s data be written?</label>
                <select
                  id="destination-provider"
                  className="wf-form-input"
                  value={providerTypeKey}
                  onChange={(event) => { setProviderTypeKey(event.target.value); setConfig({}); setSecret({}); probe.reset(); }}
                >
                  {offerable.map((candidate) => <option key={candidate.typeKey} value={candidate.typeKey}>{candidate.displayName}</option>)}
                </select>
              </div>
            )}

            {provider && <SchemaForm schema={provider.configSchema} value={config} onChange={setConfig} />}
            {provider && hasSecretInputs(provider) && <SchemaForm schema={provider.secretSchema} value={secret} onChange={setSecret} sensitive />}

          </div>

          {probe.error instanceof ApiError && <Banner title="Couldn&rsquo;t run the test">{probe.error.message}</Banner>}

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
            <strong>Nothing was saved.</strong> There is no destination and no key &mdash; and the only thing this test ever writes at the destination is an empty object it removes again.
          </p>
        </>
      )}

      {step === "use" && qualified && (
        <>
          <div className="cn-banner" role="status">
            <div className="cn-banner-h">{effectiveName} answered.</div>
            <div className="cn-banner-p">It listed the folder and accepted a write. The empty object it wrote is removed again. {qualified.latencyMilliseconds}&thinsp;ms.</div>
          </div>

          <div className="wf-form" style={{ marginTop: 16 }}>
            <div className="wf-form-row">
              <label className="wf-form-label" htmlFor="destination-name">Name</label>
              <input
                id="destination-name"
                className="wf-form-input"
                value={effectiveName}
                placeholder={derivedName || "artifacts"}
                onChange={(event) => { setNameEdited(true); setName(event.target.value); }}
              />
              <span className="wf-form-help">What this place is called on this screen. Lowercase letters, digits and hyphens. It cannot be renamed later.</span>
            </div>

            <div className="wf-form-row">
              <span className="wf-form-label">What lands here?</span>
              <span className="wf-form-help">Nothing is ticked for you. Each choice moves where NEW writes go &mdash; data already stored stays where it is.</span>
            </div>

            {(dataClasses.isLoading || routes.isLoading) && <span className="wf-form-help">Loading&hellip;</span>}
            {(dataClasses.error != null || routes.error != null) && (
              <span className="wf-form-help" style={{ color: "var(--danger)" }}>
                Couldn&rsquo;t read what this team stores today, so these choices cannot be described honestly. Save the destination without ticking anything and set that from its card.
              </span>
            )}
            {dataClasses.data?.map((dataClass: RoutedDataClass) => (
              <label key={dataClass.typeKey} className="wf-form-row" style={{ flexDirection: "row", alignItems: "flex-start", gap: 9 }}>
                <input
                  type="checkbox"
                  checked={claimed.includes(dataClass.typeKey)}
                  onChange={(event) => setClaimed((current) => event.target.checked ? [...current, dataClass.typeKey] : current.filter((key) => key !== dataClass.typeKey))}
                />
                <span>
                  <span className="cn-name" style={{ fontSize: 13 }}>{dataClass.displayName}</span>
                  <span className="wf-form-help" style={{ display: "block" }}>{whatTickingDoes(dataClass, routedElsewhere(dataClass, routes.data?.items))}</span>
                </span>
              </label>
            ))}
          </div>

          {create.error instanceof ApiError && <Banner title="Couldn&rsquo;t save the destination">{create.error.message}</Banner>}
        </>
      )}

    </Frame>
  );
}

type Step = "connect" | "refused" | "use";

/** The rail names the three questions, so an operator can see there are only three before answering the first. */
function Frame({ step, onClose, footer, children }: { step: Step; onClose: () => void; footer: ReactNode; children: ReactNode }) {
  const surface = useRef<HTMLDivElement>(null);
  useDialogKeys(surface, onClose);

  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" onClick={onClose} />
      <div ref={surface} className="mdl" role="dialog" aria-modal="true" aria-label="Add a destination">
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
        {footer}
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
 * What ticking this class actually does, for the state it is actually in.
 *
 * Three cases, not two, and the third is the one worth saying: a class already landing at another destination is
 * REPOINTED by the server rather than newly routed, so ticking it takes it from somewhere. Describing that as
 * "currently written to this server's own disk" is how an operator moves a live data class by accident.
 */
function whatTickingDoes(dataClass: RoutedDataClass, elsewhere: StorageRouteSummary | undefined): string {
  if (elsewhere) return `Currently landing in ${elsewhere.storageProfileStableName}. Ticking this MOVES the next write here — what is already stored there stays there and keeps opening.`;

  return dataClass.hasLocalFallback
    ? "Currently written to this server's own disk. Ones already there stay there and keep opening — but they can never move back once this is on."
    : "Not captured at all today. Ticking this is what starts capturing them; leaving it alone changes nothing.";
}

/** The destination this class lands in today, if any. Only an Active route sends anything. */
function routedElsewhere(dataClass: RoutedDataClass, routes: StorageRouteSummary[] | undefined): StorageRouteSummary | undefined {
  return routes?.find((route) => route.dataClassTypeKey === dataClass.typeKey && route.state === "Active");
}

/** The server's own rule for a name, mirrored so a prefill is never something it would refuse. */
const NAME_PATTERN = /^[a-z0-9][a-z0-9-]{0,127}$/;

/**
 * A prefill, not a derivation: the first value the operator typed that is ALREADY a valid name, in the provider's own
 * schema order. For OSS that is the bucket; for a filesystem root, nothing, and the operator names it.
 *
 * Deliberately not `teamNamespaceProperty` - that names the field carrying the provider's NAMESPACE, which for the
 * shipped OSS provider is the optional `keyPrefix`. Reading it produced an empty name in the ordinary case, and an
 * empty name is refused by the server at the very last step.
 */
function deriveName(provider: StorageProviderModuleSummary | undefined, config: Record<string, unknown>): string {
  const properties = schemaProperties(provider?.configSchema);
  for (const name of Object.keys(properties)) {
    const value = config[name];
    if (typeof value === "string" && NAME_PATTERN.test(value.trim())) return value.trim();
  }
  return "";
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
  return required.every((name) => typeof name === "string" && filled(value[name]));
}

/** Present and non-empty. Not string-only: a provider may require a number or a boolean, and one that did could never be tested. */
function filled(value: unknown): boolean {
  return value != null && (typeof value !== "string" || value.trim().length > 0);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
