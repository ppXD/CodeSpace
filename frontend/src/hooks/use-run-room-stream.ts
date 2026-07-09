import { useEffect, useState } from "react";
import { streamRunRecords } from "@/api/run-stream";
import type { RunRecordView } from "@/api/workflows";

/**
 * The live text of the run's CURRENTLY-streaming model call, or null when nothing is streaming. Subscribes to the
 * `/records/stream` SSE tail (this hook is mounted only while the turn is live, so its mount lifecycle IS the gate) and
 * folds the streamed records with {@link pickLiveText}: accumulate `interaction.delta` text by correlationId, and drop a
 * call's live text once its `interaction.completed` / `.failed` lands — the 2s room/journal poll then settles the
 * finished call into its model-call row. Additive + read-only; the poll stays the correctness fallback.
 */
export function useRunRoomStream(runId: string): string | null {
  const [liveText, setLiveText] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const records: RunRecordView[] = [];
    let cursor = 0;

    const onRecord = (record: RunRecordView) => {
      if (record.sequence > cursor) cursor = record.sequence;
      records.push(record);
      setLiveText(pickLiveText(records));
    };

    void streamRunRecords(runId, () => cursor, onRecord, controller.signal);

    return () => controller.abort();
  }, [runId]);

  return liveText;
}

interface StreamingCall {
  text: string;
  done: boolean;
  lastSeq: number;
}

/**
 * The text of the run's currently-streaming model call from its raw ledger records, or null when none is streaming.
 * Pure: groups `interaction.delta` fragments by correlationId (a call's fragments arrive ordinal-ordered on the wire, so
 * appending in stream order is correct), marks a call done on `interaction.completed` / `.failed`, and returns the
 * still-streaming call with the newest activity (so two interleaved calls resolve to the latest). Tolerates a null
 * correlationId and a malformed delta payload.
 */
export function pickLiveText(records: readonly RunRecordView[]): string | null {
  const calls = new Map<string, StreamingCall>();

  for (const record of records) {
    const correlationId = record.correlationId;
    if (!correlationId) continue;

    if (record.recordType === "interaction.delta") {
      const call = calls.get(correlationId) ?? { text: "", done: false, lastSeq: 0 };
      const fragment = deltaText(record.payloadJson);
      if (fragment !== null) {
        call.text += fragment;
        call.lastSeq = record.sequence;
        calls.set(correlationId, call);
      }
    } else if (record.recordType === "interaction.completed" || record.recordType === "interaction.failed") {
      const call = calls.get(correlationId);
      if (call) call.done = true;
    }
  }

  let latest: StreamingCall | null = null;
  for (const call of calls.values()) {
    if (call.done || !call.text) continue;
    if (!latest || call.lastSeq > latest.lastSeq) latest = call;
  }

  return latest?.text ?? null;
}

/** The coalesced text fragment from an `interaction.delta` payload (`{kind, provider, ordinal, text}`); null when absent or malformed. */
function deltaText(payloadJson: string): string | null {
  try {
    const payload = JSON.parse(payloadJson) as { text?: unknown };
    return typeof payload.text === "string" ? payload.text : null;
  } catch {
    return null;
  }
}
