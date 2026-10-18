import { useContext } from "react";

import { useReplayRun, useRunRemedy } from "@/hooks/use-workflows";
import { RunOpenContext } from "@/components/workflows/runOpenContext";

import { ProviderRemedyCard } from "./ProviderRemedyCard";

/**
 * The remediation card for a failed run: asks the backend what is wrong with the provider connection it failed on and what THIS
 * person can do, and renders it — or nothing, so the caller shows the ordinary failure it always did (no remedy for this run, the
 * read still loading, or it failed). The answer is read fresh and re-read after any fix, so the card turns to "run it again" the
 * moment the connection works.
 *
 * Re-run replays the run and opens the new one, the same Re-run the turn's own footer offers.
 */
export function RunRemedyCard({ runId, rawDetail, onOpenRun }: { runId: string; rawDetail?: string | null; onOpenRun?: (runId: string) => void }) {
  const remedy = useRunRemedy(runId);
  const replay = useReplayRun();
  const contextOpenRun = useContext(RunOpenContext);
  const openRun = onOpenRun ?? contextOpenRun;

  if (!remedy.data) return null;

  const rerun = async () => {
    const result = await replay.mutateAsync(runId);
    openRun?.(result.runId);
  };

  return <ProviderRemedyCard remedy={remedy.data} rawDetail={rawDetail} rerunning={replay.isPending} onRerun={rerun} onFixed={() => void remedy.refetch()} />;
}
