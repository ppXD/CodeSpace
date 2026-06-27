import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { PackArtifactSummary, PackSummary } from "@/api/packs";
import { ApiError } from "@/api/request";
import { ImportPackModal } from "@/components/agents/ImportPackModal";
import { usePack, usePacks } from "@/hooks/use-packs";
import { relativeTime } from "@/lib/codeTree";

import { countLabel, sourceLabel, splitArtifacts } from "./libraryView";

/**
 * Library / store — the team's imported packs as source categories. The left rail lists each pack (a github /
 * git-url library) with its freshness + artifact counts; selecting one shows its agents + skills in the detail
 * pane. This is the only surface where skills are visible, and the home of the per-pack Sync action (re-pull a
 * pack to refresh its artifacts). "Import" opens the import-from-URL modal — the same path that creates packs.
 */
export function LibraryPage() {
  const packs = usePacks();
  const rows = packs.data ?? [];

  // Derived selection (no effect): an explicit pick wins, else the first pack. Avoids the setState-in-effect
  // that the agent editor's load had to be refactored away from.
  const [picked, setPicked] = useState<string | null>(null);
  const selectedId = picked ?? rows[0]?.id ?? null;

  const [importing, setImporting] = useState(false);

  const hasPacks = !packs.isLoading && !packs.error && rows.length > 0;
  const totalAgents = rows.reduce((n, p) => n + p.agentCount, 0);
  const totalSkills = rows.reduce((n, p) => n + p.skillCount, 0);

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Library</span></div>
        <div className="ct-title-row">
          <h1 className="ct-title">Library</h1>
          <div className="ct-actions">
            <button type="button" className="btn btn-primary" onClick={() => setImporting(true)}><Ic.Download size={14} /> Import</button>
          </div>
        </div>
        {hasPacks && (
          <div className="ct-sub">
            {rows.length} {rows.length === 1 ? "pack" : "packs"} · {countLabel(totalAgents, totalSkills)}
          </div>
        )}
      </div>

      <div className="ct-body">
        {packs.isLoading && (
          <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>
        )}

        {packs.error instanceof ApiError && (
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load the library</div>
            <div className="cn-banner-p">{packs.error.message}</div>
          </div>
        )}

        {!packs.isLoading && !packs.error && rows.length === 0 && (
          <div className="ct-empty">
            <div className="ct-empty-h">No packs imported yet</div>
            <div className="ct-empty-p">Import a pack from a GitHub or git URL — its <strong>agents</strong> and <strong>skills</strong> become a source category here you can browse and re-sync.</div>
            <div style={{ display: "flex", justifyContent: "center", marginTop: 14 }}>
              <button type="button" className="btn btn-primary" onClick={() => setImporting(true)}><Ic.Download size={14} /> Import a pack</button>
            </div>
          </div>
        )}

        {hasPacks && (
          <div className="lib">
            <div className="lib-rail" role="listbox" aria-label="Packs">
              {rows.map((p) => (
                <PackRailItem key={p.id} pack={p} active={p.id === selectedId} onSelect={() => setPicked(p.id)} />
              ))}
            </div>
            {selectedId && <PackDetailPane packId={selectedId} />}
          </div>
        )}
      </div>

      {importing && <ImportPackModal onClose={() => setImporting(false)} />}
    </section>
  );
}

const KIND_LABEL: Record<PackSummary["kind"], string> = { Github: "GitHub", GitUrl: "git", Custom: "local" };

/** One pack row in the source rail — name + kind + counts + freshness, selectable by click or keyboard. */
function PackRailItem({ pack, active, onSelect }: { pack: PackSummary; active: boolean; onSelect: () => void }) {
  return (
    <div
      className="lib-rail-item"
      role="option"
      tabIndex={0}
      aria-selected={active}
      data-active={active ? "true" : undefined}
      onClick={onSelect}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onSelect(); } }}
    >
      <span className="lib-rail-ic"><Ic.Box size={15} /></span>
      <div className="lib-rail-body">
        <div className="lib-rail-top">
          <span className="lib-rail-name" title={pack.name}>{pack.name}</span>
          <span className="pill pill-mute">{KIND_LABEL[pack.kind]}</span>
        </div>
        <div className="lib-rail-sub">{countLabel(pack.agentCount, pack.skillCount)}</div>
      </div>
    </div>
  );
}

/** The detail pane — the selected pack's source + freshness header, then its agent + skill sections. */
function PackDetailPane({ packId }: { packId: string }) {
  const detail = usePack(packId);

  if (detail.isLoading) return <div className="lib-detail"><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></div>;

  if (detail.error instanceof ApiError) {
    return (
      <div className="lib-detail">
        <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
          <div className="cn-banner-h">Couldn't load this pack</div>
          <div className="cn-banner-p">{detail.error.message}</div>
        </div>
      </div>
    );
  }

  if (!detail.data) return null;

  const { pack, artifacts } = detail.data;
  const { agents, skills } = splitArtifacts(artifacts);

  return (
    <div className="lib-detail">
      <div className="lib-dhead">
        <div className="lib-dhead-main">
          <h2 className="lib-dhead-name">{pack.name}</h2>
          {pack.url
            ? <a className="lib-dhead-src" href={pack.url} target="_blank" rel="noreferrer"><Ic.Link size={12} /> {sourceLabel(pack)}</a>
            : <span className="lib-dhead-src lib-dhead-src-mute">authored in this team</span>}
        </div>
        <Freshness pack={pack} />
      </div>

      {artifacts.length === 0 && (
        <div className="ct-empty"><div className="ct-empty-h">No active artifacts</div><div className="ct-empty-p">Every agent + skill from this pack has been removed.</div></div>
      )}

      <ArtifactSection title="Agents" icon={<Ic.Bot size={13} />} items={agents} />
      <ArtifactSection title="Skills" icon={<Ic.Book size={13} />} items={skills} />
    </div>
  );
}

/** The pack's freshness chips — git ref, short commit, and when it was last synced. Hidden for local packs. */
function Freshness({ pack }: { pack: PackSummary }) {
  if (pack.kind === "Custom") return null;

  return (
    <div className="lib-fresh">
      {pack.reference && <span className="wf-trigger-chip wf-trigger-chip-soft"><Ic.Branch size={11} /> {pack.reference}</span>}
      {pack.lastSyncedSha && <span className="wf-version" title={pack.lastSyncedSha}>{pack.lastSyncedSha.slice(0, 7)}</span>}
      {pack.lastSyncedDate && <span className="wf-trigger-muted"><Ic.Clock size={11} /> synced {relativeTime(pack.lastSyncedDate)}</span>}
    </div>
  );
}

/** One kind-section of the detail — a labelled heading + the artifact rows. Renders nothing when empty. */
function ArtifactSection({ title, icon, items }: { title: string; icon: React.ReactNode; items: PackArtifactSummary[] }) {
  if (items.length === 0) return null;

  return (
    <div className="lib-sec">
      <div className="lib-sec-h">{icon} {title} <span className="lib-sec-c">{items.length}</span></div>
      {items.map((a) => (
        <div className="lib-art" key={a.id}>
          <div className="repo-info">
            <div className="repo-name">{a.name}<span className="wf-trigger-muted" style={{ marginLeft: 8 }}>@{a.slug}</span></div>
            {a.description && <div className="repo-path"><span className="repo-path-desc" title={a.description}>{a.description}</span></div>}
          </div>
        </div>
      ))}
    </div>
  );
}
