import { useMemo } from "react";

/**
 * Tiny unified-diff renderer. Parses the patch text into hunks (`@@ -a,b +c,d @@`)
 * + rows, then renders a two-column table — old line number, new line number, and
 * the content line with the +/- prefix carrying a colour band. No external diff
 * library; the spec is small and we don't need word-level highlighting yet.
 *
 * Renders deliberately FLAT and READ-ONLY for v0:
 *   - No syntax highlighting (would need per-language Shiki/Prism wiring).
 *   - No "expand context" affordance — only the hunks the provider returned.
 *   - No line-comment composer — needs review-thread backend first.
 *
 * Adding any of those later means swapping the body row template, not the
 * parser; the row shape (`{ kind, oldNo, newNo, text }`) is stable.
 */

type DiffRowKind = "context" | "addition" | "deletion" | "hunk-header";

interface DiffRow {
  kind: DiffRowKind;
  oldNo: number | null;
  newNo: number | null;
  text: string;
}

interface ParsedHunk {
  header: string;
  rows: DiffRow[];
}

export function DiffViewer({ patch }: { patch: string }) {
  const hunks = useMemo(() => parseUnifiedDiff(patch), [patch]);

  if (hunks.length === 0) {
    return <div className="diff-empty">Diff is empty — no line changes.</div>;
  }

  return (
    <div className="diff">
      {hunks.map((hunk, i) => (
        <div key={i} className="diff-hunk">
          <div className="diff-hunk-header">{hunk.header}</div>
          {hunk.rows.map((row, j) => (
            <div key={j} className="diff-row" data-kind={row.kind}>
              <span className="diff-ln diff-ln-old">{row.oldNo ?? ""}</span>
              <span className="diff-ln diff-ln-new">{row.newNo ?? ""}</span>
              <span className="diff-marker">{markerFor(row.kind)}</span>
              <span className="diff-text">{row.text}</span>
            </div>
          ))}
        </div>
      ))}
    </div>
  );
}

function markerFor(kind: DiffRowKind): string {
  if (kind === "addition") return "+";
  if (kind === "deletion") return "-";
  return " ";
}

/**
 * Parse unified-diff text into a list of hunks. The provider sends only the
 * hunk bodies — not the `diff --git` / `index` / `+++` / `---` file headers
 * that `git diff` includes — so we only need to handle `@@` lines and the
 * `+`/`-`/` ` lines that follow.
 *
 * `\ No newline at end of file` markers (rare but real) are skipped — they
 * don't represent a content line.
 */
function parseUnifiedDiff(patch: string): ParsedHunk[] {
  const hunks: ParsedHunk[] = [];
  let current: ParsedHunk | null = null;
  let oldLine = 0;
  let newLine = 0;

  const lines = patch.split("\n");

  for (const line of lines) {
    if (line.startsWith("@@")) {
      const parsed = parseHunkHeader(line);
      if (parsed == null) continue;
      oldLine = parsed.oldStart;
      newLine = parsed.newStart;
      current = { header: line, rows: [] };
      hunks.push(current);
      continue;
    }

    if (current == null) continue;
    if (line.startsWith("\\")) continue; // "\ No newline at end of file"

    if (line.startsWith("+")) {
      current.rows.push({ kind: "addition", oldNo: null, newNo: newLine, text: line.slice(1) });
      newLine++;
    } else if (line.startsWith("-")) {
      current.rows.push({ kind: "deletion", oldNo: oldLine, newNo: null, text: line.slice(1) });
      oldLine++;
    } else {
      // Context line — preserves a leading space; drop it before display so
      // indentation matches the file as-edited.
      const text = line.startsWith(" ") ? line.slice(1) : line;
      current.rows.push({ kind: "context", oldNo: oldLine, newNo: newLine, text });
      oldLine++;
      newLine++;
    }
  }

  return hunks;
}

function parseHunkHeader(header: string): { oldStart: number; newStart: number } | null {
  // Format: `@@ -oldStart,oldLines +newStart,newLines @@ optional context`
  // The line-count parts are optional ("@@ -10 +12 @@" is valid for single-line hunks).
  const match = header.match(/^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
  if (!match) return null;
  return { oldStart: parseInt(match[1], 10), newStart: parseInt(match[2], 10) };
}
