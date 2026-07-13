import { MapFanout } from "../MapFanout";
import { RunRowDetail } from "./ReceiptFooter";
import type { NodeFooterProps } from "./index";

/**
 * The flow.map fan-out footer: the activity-terminal {@link MapFanout} (summary line + per-branch status-dot
 * strip + one focused branch terminal), so a K-branch map reads as ONE card instead of K stacked rows. The
 * per-branch detail is the shared {@link RunRowDetail} (error / output / input / live agent embed).
 */
export function BranchDotsFooter(props: NodeFooterProps) {
  return <MapFanout rows={props.rows} renderBranch={(row) => <RunRowDetail row={row} />} />;
}
