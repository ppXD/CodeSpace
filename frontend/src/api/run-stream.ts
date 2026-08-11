import type { RunRecordView } from "@/api/workflows";

const baseUrl = import.meta.env.VITE_API_URL ?? "";

/**
 * Subscribe to a run's LIVE ledger tail over Server-Sent Events (the backend `/records/stream` endpoint). Uses
 * `fetch` + `ReadableStream` — NOT native `EventSource` — because auth is a Bearer + X-Team-Id HEADER pair EventSource
 * can't set (the same headers `fetchJson` injects). Parses SSE frames (`id: {seq}\nevent: {type}\ndata: {json}\n\n`) and
 * invokes `onRecord` with each `RunRecordView`. Reconnects with `?after={lastSeenSequence}` on a transient mid-stream
 * drop; STOPS on a clean server close (the run reached a terminal record) or when the signal aborts. Errors are
 * swallowed — the 2s room/journal poll is the correctness fallback, so a dropped stream never loses data.
 */
export async function streamRunRecords(runId: string, getAfter: () => number, onRecord: (record: RunRecordView) => void, signal: AbortSignal): Promise<void> {
  while (!signal.aborted) {
    let opened = false;
    try {
      const headers = new Headers({ Accept: "text/event-stream" });
      const jwt = localStorage.getItem("codespace.jwt");
      if (jwt) headers.set("Authorization", `Bearer ${jwt}`);
      const teamId = localStorage.getItem("codespace.activeTeamId");
      if (teamId) headers.set("X-Team-Id", teamId);

      const response = await fetch(`${baseUrl}/api/workflows/runs/${runId}/records/stream?after=${getAfter()}`, { headers, signal });
      if (!response.ok || !response.body) return; // a non-2xx (401 / 404) is not transient — stop, the poll covers it

      opened = true;
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      while (true) {
        const { value, done } = await reader.read();
        if (done) return; // a clean close ⇒ the run reached a terminal record ⇒ stop (reconnecting would hang the tail)

        buffer += decoder.decode(value, { stream: true });

        let boundary: number;
        while ((boundary = buffer.indexOf("\n\n")) >= 0) {
          const frame = buffer.slice(0, boundary);
          buffer = buffer.slice(boundary + 2);
          const record = parseFrame(frame);
          if (record) onRecord(record);
        }
      }
    } catch {
      if (signal.aborted || !opened) return; // aborted, or a hard initial failure — don't hot-loop; fall back to the poll
      await new Promise((resolve) => setTimeout(resolve, 1500)); // a mid-stream drop ⇒ reconnect from the advanced cursor
    }
  }
}

/** Extract the `RunRecordView` from an SSE frame's `data:` line; null when the frame carries no parseable record. */
function parseFrame(frame: string): RunRecordView | null {
  const dataLine = frame.split("\n").find((line) => line.startsWith("data:"));
  if (!dataLine) return null;

  try {
    return JSON.parse(dataLine.slice(5).trim()) as RunRecordView;
  } catch {
    return null;
  }
}
