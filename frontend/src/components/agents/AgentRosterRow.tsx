import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentDefinitionSummary, AgentStat } from "@/api/agents";
import { relativeTime } from "@/lib/codeTree";

import { emptyEvidenceLabel, formatCost, formatDuration, formatSuccessRate, initials, outcomeTone, successTone } from "./agentEvidence";
import { toolsLabel } from "./agentRuntime";

/**
 * One agent on the roster — a row that reads left-to-right as identity → loadout → recent-run evidence, with a
 * primary "Launch task" action. The evidence (an outcome sparkline, windowed success rate, latency, spend, and
 * last-active) comes from the per-agent stats join; when there's no stat the row is HONEST about why (still loading,
 * errored, no runs in the window, or genuinely never ran) rather than always claiming "No runs yet". The role
 * heuristic is gone — the avatar is name initials, so nothing on the row is guessed.
 */
export function AgentRosterRow({ agent, stat, statsPending, statsError, windowed, onLaunch, onEdit, onViewRuns }: {
  agent: AgentDefinitionSummary;
  stat: AgentStat | undefined;
  statsPending: boolean;
  statsError: boolean;
  windowed: boolean;
  onLaunch: () => void;
  onEdit: () => void;
  onViewRuns: () => void;
}) {
  const skillCount = agent.boundSkills?.length ?? 0;

  return (
    <article className="ar-row">
      <div className="ar-head">
        <div className="ar-avatar" aria-hidden="true">{initials(agent.name)}</div>

        <div className="ar-id">
          <div className="ar-namerow">
            <span className="ar-name" title={agent.name}>{agent.name}</span>
            <span className="ar-origin">
              {agent.origin === "Imported"
                ? <><Ic.Box size={12} /> <span className="ar-origin-pack" title={agent.packName ?? undefined}>{agent.packName ?? "Imported"}</span></>
                : <><span className="ar-dot ar-dot-good" /> Authored</>}
            </span>
          </div>
          <div className="ar-handle">@{agent.slug}</div>
        </div>

        <button type="button" className="btn ar-launch" onClick={onLaunch} aria-label={`Launch a task with ${agent.name}`}><Ic.Play size={13} /> Launch task</button>
        <button type="button" className="btn ar-icon-btn" onClick={onEdit} aria-label={`Edit ${agent.name}`}><Ic.Edit size={14} /></button>
      </div>

      <div className="ar-config">
        <span className="ar-chip"><Ic.Sparkles size={12} /> {agent.model ?? "Auto"}</span>
        <span className="ar-chip"><Ic.Zap size={12} /> {agent.defaultAutonomy ?? "Standard"}</span>
        <span className="ar-chip"><Ic.Wrench size={12} /> {toolsLabel(agent.tools)}</span>
        <span className="ar-chip"><Ic.Book size={12} /> {skillCount === 0 ? "No skills" : `${skillCount} skill${skillCount === 1 ? "" : "s"}`}</span>
      </div>

      {stat
        ? <Evidence stat={stat} agentName={agent.name} onViewRuns={onViewRuns} />
        : <div className="ar-evidence ar-noruns">{emptyEvidenceLabel({ pending: statsPending, error: statsError, windowed })}</div>}
    </article>
  );
}

/** The recent-run evidence strip: a sparkline of the last outcomes, then the windowed success / count / latency /
 *  spend / last-active, and a drill-down into this agent's runs. Every number is read-only + already computed by the
 *  stats endpoint; the row only formats. */
function Evidence({ stat, agentName, onViewRuns }: { stat: AgentStat; agentName: string; onViewRuns: () => void }) {
  const cost = formatCost(stat.estimatedCostUsd);
  const costText = stat.unknownCostRuns > 0 ? `${cost} · ${stat.unknownCostRuns} unpriced` : cost;

  return (
    <div className="ar-evidence">
      <span className="ar-spark" aria-hidden="true">
        {stat.recentOutcomes.map((s, i) => <span key={i} className={`ar-dot ar-dot-${outcomeTone(s)}`} />)}
      </span>

      {stat.total === 0
        ? <span className="ar-ev-item ar-ev-muted">no scored runs</span>
        : <span className={`ar-rate ar-rate-${successTone(stat.successRate, stat.total)}`}>{formatSuccessRate(stat.successRate)}</span>}

      <span className="ar-ev-item">{stat.total} run{stat.total === 1 ? "" : "s"}</span>
      <span className="ar-ev-item">p50 {formatDuration(stat.p50DurationSeconds)}</span>
      <span className="ar-ev-item">{costText}</span>
      <span className="ar-ev-item"><Ic.Clock size={11} /> {relativeTime(stat.lastRunAt)}</span>

      <button type="button" className="ar-runs-link" onClick={onViewRuns} aria-label={`View ${agentName}'s runs`}>View runs <Ic.ArrowOut size={12} /></button>
    </div>
  );
}
