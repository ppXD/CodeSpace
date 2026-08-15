import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import {
  workflowsApi,
  type WorkflowRunCaptureCompleteness,
  type WorkflowRunModelCallAttemptMetadata,
  type WorkflowRunModelCallBody,
  type WorkflowRunModelCallBodyDescriptor,
  type WorkflowRunModelCallBodyPage,
  type WorkflowRunModelCallBodyReferenceState,
  type WorkflowRunModelCallDetailMetadata,
  type WorkflowRunModelCallPart,
  type WorkflowRunModelCallPartAvailability,
  type WorkflowRunModelCallPartPage,
} from "@/api/workflows";
import { formatTokens } from "@/components/workflows/runActivity";

export type WorkflowRunModelCallTab = "result" | "prompt" | "usage" | "trace";

const MODEL_CALL_PAGE_BYTES = 64 * 1024;
const MAX_VISIBLE_MODEL_CALL_PAGES = 8;
const MODEL_CALL_METADATA_STALE_MS = 30_000;
const MODEL_CALL_METADATA_GC_MS = 60_000;

type BoundedRead =
  | { kind: "legacy"; runId: string; sequence: number; part: WorkflowRunModelCallPart }
  | { kind: "stable"; runId: string; modelCallId: string; body: WorkflowRunModelCallBody; attemptId?: string | null };

type BoundedPage = WorkflowRunModelCallPartPage | WorkflowRunModelCallBodyPage;

interface LocalPageState {
  key: string;
  pages: BoundedPage[];
  loading: boolean;
  loadingMore: boolean;
  missing: boolean;
  droppedEarlier: boolean;
  error: Error | null;
}

const emptyPageState = (key: string, loading: boolean): LocalPageState => ({ key, pages: [], loading, loadingMore: false, missing: false, droppedEarlier: false, error: null });

function retryModelCallRead(failureCount: number, error: Error): boolean {
  return error instanceof ApiError && [429, 502, 503, 504].includes(error.status) && failureCount < 2;
}

function boundedReadKey(read: BoundedRead | null): string {
  if (read == null) return "none";
  return read.kind === "legacy"
    ? `legacy:${read.runId}:${read.sequence}:${read.part}`
    : `stable:${read.runId}:${read.modelCallId}:${read.body}:${read.attemptId ?? "logical"}`;
}

async function readPageOnce(read: BoundedRead, offsetBytes: number, signal: AbortSignal): Promise<BoundedPage | null> {
  if (read.kind === "legacy") return workflowsApi.getRunModelCallPart(read.runId, read.sequence, read.part, offsetBytes, MODEL_CALL_PAGE_BYTES, signal);
  return workflowsApi.getRunModelCallBody(read.runId, read.modelCallId, { body: read.body, attemptId: read.attemptId, offsetBytes, limitBytes: MODEL_CALL_PAGE_BYTES }, signal);
}

async function readPage(read: BoundedRead, offsetBytes: number, signal: AbortSignal): Promise<BoundedPage | null> {
  for (let failures = 0; ; failures++) {
    try {
      return await readPageOnce(read, offsetBytes, signal);
    } catch (error) {
      if (signal.aborted || !(error instanceof Error) || !retryModelCallRead(failures, error)) throw error;
    }
  }
}

/** Body bytes live only for this mounted drawer section; React Query never receives them and the visible window stays at or below 512 KiB. */
function useLocalBoundedPages(read: BoundedRead | null) {
  const key = boundedReadKey(read);
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const [state, setState] = useState<LocalPageState>(() => emptyPageState("", true));

  useEffect(() => {
    const currentGeneration = ++generation.current;
    controller.current?.abort();
    if (read == null) {
      setState(emptyPageState(key, false));
      return;
    }

    const nextController = new AbortController();
    let active = true;
    controller.current = nextController;
    setState(emptyPageState(key, true));
    void readPage(read, 0, nextController.signal).then((page) => {
      if (!active || generation.current !== currentGeneration) return;
      setState({ ...emptyPageState(key, false), pages: page == null ? [] : [page], missing: page == null });
    }).catch((error: unknown) => {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...emptyPageState(key, false), error: error instanceof Error ? error : new Error("Model-call body read failed.") });
    });

    return () => {
      active = false;
      controller.current?.abort();
    };
  }, [key, read]);

  const visible = state.key === key ? state : emptyPageState(key, read != null);
  const nextOffset = visible.pages.at(-1)?.availability === "Available" ? visible.pages.at(-1)?.nextOffsetBytes ?? null : null;

  const loadMore = useCallback(async () => {
    if (read == null || nextOffset == null || visible.loadingMore) return;
    const currentGeneration = generation.current;
    controller.current?.abort();
    const nextController = new AbortController();
    controller.current = nextController;
    setState((current) => current.key === key ? { ...current, loadingMore: true, error: null } : current);
    try {
      const page = await readPage(read, nextOffset, nextController.signal);
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => {
        if (current.key !== key) return current;
        const appended = page == null ? current.pages : [...current.pages, page];
        const overflow = appended.length > MAX_VISIBLE_MODEL_CALL_PAGES;
        return { ...current, pages: overflow ? appended.slice(-MAX_VISIBLE_MODEL_CALL_PAGES) : appended, loadingMore: false, missing: page == null, droppedEarlier: current.droppedEarlier || overflow, error: null };
      });
    } catch (error) {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState((current) => current.key === key
        ? { ...current, loadingMore: false, error: error instanceof Error ? error : new Error("Model-call body read failed.") }
        : current);
    }
  }, [key, nextOffset, read, visible.loadingMore]);

  const startOver = useCallback(async () => {
    if (read == null || visible.loading) return;
    const currentGeneration = ++generation.current;
    controller.current?.abort();
    const nextController = new AbortController();
    controller.current = nextController;
    setState(emptyPageState(key, true));
    try {
      const page = await readPage(read, 0, nextController.signal);
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...emptyPageState(key, false), pages: page == null ? [] : [page], missing: page == null });
    } catch (error) {
      if (nextController.signal.aborted || generation.current !== currentGeneration) return;
      setState({ ...emptyPageState(key, false), error: error instanceof Error ? error : new Error("Model-call body read failed.") });
    }
  }, [key, read, visible.loading]);

  return { ...visible, hasNextPage: nextOffset != null, loadMore, startOver };
}

function availabilityLabel(availability: WorkflowRunModelCallPartAvailability): string {
  switch (availability) {
    case "NotRecorded": return "Not recorded";
    case "MetadataMissing": return "Artifact metadata missing";
    case "PhysicalObjectMissing": return "Stored bytes missing";
    case "IntegrityFailure": return "Integrity check failed";
    case "BackendUnavailable": return "Backend unavailable";
    case "AccessDenied": return "Storage access denied";
    case "InvalidOffset": return "Invalid body offset";
    case "Redacted": return "Body redacted";
    case "CapturePartial": return "Body capture partial";
    case "CaptureUnavailable": return "Body capture unavailable";
    case "CaptureCorrupt": return "Body capture corrupt";
    case "LegacyUnknown": return "Legacy body state unknown";
    case "InvalidBodyReference": return "Invalid body reference";
    default: return availability;
  }
}

function referenceLabel(state: WorkflowRunModelCallBodyReferenceState): string {
  switch (state) {
    case "NotRecorded": return "Body not recorded";
    case "Redacted": return "Body intentionally redacted";
    case "Partial": return "Body capture partial";
    case "Unavailable": return "Body capture unavailable";
    case "Corrupt": return "Body reference corrupt";
    case "LegacyUnknown": return "Legacy body state unknown";
    default: return state;
  }
}

function ReadState({ availability, message, completeness }: { availability: WorkflowRunModelCallPartAvailability; message?: string | null; completeness?: WorkflowRunCaptureCompleteness }) {
  return (
    <p className="room-para room-muted" data-state={availability}>
      <strong>{availabilityLabel(availability)}</strong>{completeness ? ` · capture ${completeness.toLowerCase()}` : ""}{message ? ` — ${message}` : ""}
    </p>
  );
}

function BoundedBodyView({ pages, heading, emptyLabel }: { pages: ReturnType<typeof useLocalBoundedPages>; heading?: string; emptyLabel: string }) {
  if (pages.loading) return <>{heading && <div className="room-mcpart-title">{heading}</div>}<p className="room-para room-muted">Loading…</p></>;
  if (pages.pages.length === 0 && pages.error) return <>{heading && <div className="room-mcpart-title">{heading}</div>}<p className="room-para room-muted">Couldn't load this body. {pages.error instanceof ApiError ? `(${pages.error.code})` : ""}</p></>;
  if (pages.pages.length === 0 && pages.missing) return <>{heading && <div className="room-mcpart-title">{heading}</div>}<p className="room-para room-muted">This model call isn't available.</p></>;
  if (pages.pages.length === 0) return null;

  const first = pages.pages[0];
  if (first.availability !== "Available") return <>{heading && <div className="room-mcpart-title">{heading}</div>}<ReadState availability={first.availability} message={first.message} completeness={"captureCompleteness" in first ? first.captureCompleteness : undefined} /></>;
  const unavailable = pages.pages.find((page) => page.availability !== "Available");
  const available = pages.pages.filter((page) => page.availability === "Available");
  const hasText = available.some((page) => (page.text?.length ?? 0) > 0);
  const startOffset = available[0]?.offsetBytes ?? 0;
  const finalPage = available.at(-1);
  const endOffset = finalPage == null ? 0 : finalPage.offsetBytes + finalPage.returnedBytes;
  const totalBytes = available.find((page) => page.totalBytes != null)?.totalBytes ?? null;
  return (
    <>
      {heading && <div className="room-mcpart-title">{heading}</div>}
      <div className="room-mcwindow">
        <span>Showing bytes {startOffset.toLocaleString()}–{endOffset.toLocaleString()}{totalBytes == null ? "" : ` of ${totalBytes.toLocaleString()}`}</span>
        {pages.droppedEarlier && <button type="button" disabled={pages.loading || pages.loadingMore} onClick={() => void pages.startOver()}>Start over</button>}
      </div>
      {pages.droppedEarlier && <p className="room-mcwindow-note">Earlier bytes were removed from this view to keep the DOM and memory bounded.</p>}
      {hasText ? <pre className="room-mcpre">{available.map((page) => <span className="room-mcchunk" key={page.offsetBytes}>{page.text ?? ""}</span>)}</pre> : <p className="room-para room-muted">No {emptyLabel} recorded for this call.</p>}
      {unavailable && <ReadState availability={unavailable.availability} message={unavailable.message} completeness={"captureCompleteness" in unavailable ? unavailable.captureCompleteness : undefined} />}
      {pages.missing && <ReadState availability="MetadataMissing" message="The next body page is unavailable." />}
      {pages.error && <p className="room-para room-muted">Couldn't load the next body page. {pages.error instanceof ApiError ? `(${pages.error.code})` : ""}</p>}
      {pages.hasNextPage && <button className="room-mcload" disabled={pages.loadingMore} onClick={() => void pages.loadMore()}>{pages.loadingMore ? "Loading…" : "Load more"}</button>}
    </>
  );
}

function BoundedBody({ read, heading, emptyLabel }: { read: BoundedRead; heading?: string; emptyLabel: string }) {
  const pages = useLocalBoundedPages(read);
  return <BoundedBodyView pages={pages} heading={heading} emptyLabel={emptyLabel} />;
}

function LegacyPart({ runId, sequence, part, heading, emptyLabel }: { runId: string; sequence: number; part: WorkflowRunModelCallPart; heading?: string; emptyLabel: string }) {
  const read = useMemo<BoundedRead>(() => ({ kind: "legacy", runId, sequence, part }), [part, runId, sequence]);
  return <BoundedBody read={read} heading={heading} emptyLabel={emptyLabel} />;
}

function LegacyTab({ runId, sequence, tab }: { runId: string; sequence: number; tab: WorkflowRunModelCallTab }) {
  if (tab === "result") return <LegacyPart runId={runId} sequence={sequence} part="Result" emptyLabel="result" />;
  if (tab === "usage") return <LegacyPart runId={runId} sequence={sequence} part="Usage" emptyLabel="usage" />;
  if (tab === "trace") return <LegacyPart runId={runId} sequence={sequence} part="Trace" emptyLabel="trace" />;
  return <><LegacyPart runId={runId} sequence={sequence} part="SystemPrompt" heading="SYSTEM" emptyLabel="system prompt" /><LegacyPart runId={runId} sequence={sequence} part="UserPrompt" heading="USER" emptyLabel="user prompt" /></>;
}

function StableBody({ metadata, descriptor, heading, emptyLabel }: { metadata: WorkflowRunModelCallDetailMetadata; descriptor?: WorkflowRunModelCallBodyDescriptor; heading?: string; emptyLabel: string }) {
  const read = useMemo<BoundedRead | null>(() => descriptor?.referenceState === "Referenced"
    ? { kind: "stable", runId: metadata.runId, modelCallId: metadata.workflowRunModelCallId, body: descriptor.body, attemptId: descriptor.attemptId }
    : null, [descriptor, metadata.runId, metadata.workflowRunModelCallId]);
  const pages = useLocalBoundedPages(read);

  if (descriptor == null) return <>{heading && <div className="room-mcpart-title">{heading}</div>}<ReadState availability="MetadataMissing" message="No body descriptor was projected." /></>;
  if (descriptor.referenceState !== "Referenced") return <>{heading && <div className="room-mcpart-title">{heading}</div>}<p className="room-para room-muted" data-state={descriptor.referenceState}><strong>{referenceLabel(descriptor.referenceState)}</strong>{` · capture ${descriptor.captureCompleteness.toLowerCase()}`}</p></>;
  return <BoundedBodyView pages={pages} heading={heading} emptyLabel={emptyLabel} />;
}

function descriptor(owner: { bodies: WorkflowRunModelCallBodyDescriptor[] }, body: WorkflowRunModelCallBody) {
  return owner.bodies.find((candidate) => candidate.body === body);
}

function totalTokens(attempt: WorkflowRunModelCallAttemptMetadata) {
  return (attempt.usage.inputTokens ?? 0) + (attempt.usage.outputTokens ?? 0);
}

function StableMetadata({ metadata, selectedAttemptId, onSelectAttempt }: { metadata: WorkflowRunModelCallDetailMetadata; selectedAttemptId?: string; onSelectAttempt: (attemptId: string) => void }) {
  return (
    <section className="room-mcprojection" aria-label="Model call metadata">
      <div className="room-mclogical">
        <strong>Logical #{metadata.callOrdinal}</strong>
        <span>{metadata.purpose}</span>
        {(metadata.requestedModel || metadata.requestedProvider) && <span>requested {metadata.requestedModel ?? metadata.requestedProvider}</span>}
        <span>capture {metadata.captureCompleteness.toLowerCase()}</span>
        {metadata.executionGeneration != null && <span>generation {metadata.executionGeneration}</span>}
      </div>
      <div className="room-mcattempts" role="group" aria-label="Physical attempts">
        {metadata.attempts.length === 0
          ? <span className="room-muted">No physical attempt metadata was projected.</span>
          : metadata.attempts.map((attempt) => (
            <button key={attempt.attemptId} className={`room-mcattempt${attempt.attemptId === selectedAttemptId ? " room-mcattempt-on" : ""}`}
              aria-pressed={attempt.attemptId === selectedAttemptId} onClick={() => onSelectAttempt(attempt.attemptId)}>
              <strong>Attempt {attempt.attemptOrdinal}</strong>
              <span>{attempt.effectiveModel ?? attempt.effectiveProvider ?? "unknown model"}</span>
              <span>{attempt.status}</span>
              <span>{attempt.sourceEvidence}</span>
              <span>capture {attempt.captureCompleteness.toLowerCase()}</span>
            </button>
          ))}
      </div>
    </section>
  );
}

function StableUsage({ attempt }: { attempt: WorkflowRunModelCallAttemptMetadata | null }) {
  if (attempt == null) return <ReadState availability="MetadataMissing" message="No physical attempt usage was projected." />;
  const usage = attempt.usage;
  return (
    <dl className="room-mcusage">
      <div><dt>Input</dt><dd>{formatTokens(usage.inputTokens ?? 0)} tokens</dd></div>
      <div><dt>Output</dt><dd>{formatTokens(usage.outputTokens ?? 0)} tokens</dd></div>
      <div><dt>Reasoning</dt><dd>{formatTokens(usage.reasoningTokens ?? 0)} tokens</dd></div>
      <div><dt>Total</dt><dd>{formatTokens(totalTokens(attempt))} tokens</dd></div>
      <div><dt>Cost</dt><dd>{attempt.costAmount == null ? "not recorded" : `${attempt.costAmount} ${attempt.costCurrency ?? ""}`.trim()}</dd></div>
      <div><dt>Finish</dt><dd>{attempt.finishReason ?? attempt.errorCode ?? "not recorded"}</dd></div>
    </dl>
  );
}

function StableTrace({ metadata, attempt }: { metadata: WorkflowRunModelCallDetailMetadata; attempt: WorkflowRunModelCallAttemptMetadata | null }) {
  if (attempt == null) return <ReadState availability="MetadataMissing" message="No physical attempt trace was projected." />;
  return (
    <>
      <dl className="room-mcusage">
        <div><dt>Status</dt><dd>{attempt.status}</dd></div>
        <div><dt>Transport</dt><dd>{attempt.transportKind ?? "not recorded"}</dd></div>
        <div><dt>Provider request</dt><dd>{attempt.providerRequestId ?? "not recorded"}</dd></div>
        <div><dt>HTTP</dt><dd>{attempt.httpStatusCode ?? "not recorded"}</dd></div>
        <div><dt>Evidence</dt><dd>{attempt.sourceEvidence} · revision {attempt.sourceEvidenceRevision}</dd></div>
        <div><dt>Schema</dt><dd>logical {metadata.schemaVersion} · attempt {attempt.schemaVersion}</dd></div>
      </dl>
      <StableBody metadata={metadata} descriptor={descriptor(attempt, "AttemptError")} heading="ERROR BODY" emptyLabel="error body" />
    </>
  );
}

function StableCall({ metadata, tab }: { metadata: WorkflowRunModelCallDetailMetadata; tab: WorkflowRunModelCallTab }) {
  const orderedAttempts = useMemo(() => [...metadata.attempts].sort((left, right) => left.attemptOrdinal - right.attemptOrdinal || left.attemptId.localeCompare(right.attemptId)), [metadata.attempts]);
  const [selectedAttemptId, setSelectedAttemptId] = useState<string>();
  const selectedAttempt = orderedAttempts.find((attempt) => attempt.attemptId === selectedAttemptId) ?? orderedAttempts.at(-1) ?? null;

  return (
    <>
      <StableMetadata metadata={metadata} selectedAttemptId={selectedAttempt?.attemptId} onSelectAttempt={setSelectedAttemptId} />
      {tab === "result"
        ? selectedAttempt == null ? <ReadState availability="MetadataMissing" message="No physical attempt response was projected." /> : <StableBody metadata={metadata} descriptor={descriptor(selectedAttempt, "AttemptResponse")} emptyLabel="result" />
        : tab === "usage" ? <StableUsage attempt={selectedAttempt} />
        : tab === "trace" ? <StableTrace metadata={metadata} attempt={selectedAttempt} />
        : <><StableBody metadata={metadata} descriptor={descriptor(metadata, "LogicalRequest")} heading="LOGICAL REQUEST" emptyLabel="logical request" />{selectedAttempt && <StableBody metadata={metadata} descriptor={descriptor(selectedAttempt, "AttemptRequest")} heading={`ATTEMPT ${selectedAttempt.attemptOrdinal} REQUEST`} emptyLabel="attempt request" />}</>}
    </>
  );
}

/** Sequence metadata is the compatibility gate; projected calls switch to stable-id reads without changing legacy runs. */
export function WorkflowRunModelCallContent({ runId, sequence, tab }: { runId: string; sequence: number; tab: WorkflowRunModelCallTab }) {
  const route = useQuery({
    queryKey: ["workflow-run-model-call-route", runId, sequence],
    queryFn: ({ signal }) => workflowsApi.getRunModelCall(runId, sequence, signal),
    staleTime: MODEL_CALL_METADATA_STALE_MS,
    gcTime: MODEL_CALL_METADATA_GC_MS,
    retry: retryModelCallRead,
  });
  const stableId = route.data?.projectionState === "Projected" ? route.data.workflowRunModelCallId ?? null : null;
  const stable = useQuery({
    queryKey: ["workflow-run-model-call-stable", runId, stableId],
    queryFn: ({ signal }) => stableId == null ? Promise.resolve(null) : workflowsApi.getRunModelCallById(runId, stableId, signal),
    enabled: stableId != null,
    staleTime: MODEL_CALL_METADATA_STALE_MS,
    gcTime: MODEL_CALL_METADATA_GC_MS,
    retry: retryModelCallRead,
  });

  if (route.isLoading) return <p className="room-para room-muted">Loading…</p>;
  if (route.isError) return <p className="room-para room-muted">Couldn't load this model call. {route.error instanceof ApiError ? `(${route.error.code})` : ""}</p>;
  if (route.data == null) return <p className="room-para room-muted">This model call's detail isn't available.</p>;
  if (route.data.projectionState === "LegacyFallback") return <LegacyTab runId={runId} sequence={sequence} tab={tab} />;
  if (stableId == null) return <ReadState availability="MetadataMissing" message="Projected metadata is missing its stable model-call id." completeness={route.data.captureCompleteness} />;
  if (stable.isLoading) return <p className="room-para room-muted">Loading stable model-call metadata…</p>;
  if (stable.isError) return <p className="room-para room-muted">Couldn't load stable model-call metadata. {stable.error instanceof ApiError ? `(${stable.error.code})` : ""}</p>;
  if (stable.data == null) return <ReadState availability="MetadataMissing" message="The stable model-call projection is unavailable." completeness={route.data.captureCompleteness} />;
  if (stable.data.runId !== runId || stable.data.workflowRunModelCallId !== stableId) return <ReadState availability="IntegrityFailure" message="Stable model-call identity does not match the requested Workflow Run." completeness={stable.data.captureCompleteness} />;
  return <StableCall metadata={stable.data} tab={tab} />;
}
