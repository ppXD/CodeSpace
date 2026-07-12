import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";

import type { ScopeSuggestion } from "./scope-introspection";
import { allBranchIds, buildSuggestionTree, flattenVisible, typeFits, type VisibleRow } from "./suggestionTree";

/**
 * Dify-style templated input field with chip rendering. Every {{ref}} token in the value
 * becomes a single visual pill inside a contenteditable editor:
 *
 *   • Backspace next to a chip removes it as one unit — no half-deleted `{{trigger.titl`
 *     debris like a plain &lt;input&gt; would leave behind.
 *   • Click @-button or type `{{` to open the autocomplete picker; picking inserts a chip.
 *   • @-button is idempotent: clicking it while the picker is already open just focuses
 *     the editor (used to dump another `{{` into the text).
 *   • Right-side toolbar always shows @ · copy · expand. Expand toggles single-line vs
 *     multi-line height without leaving the inspector.
 *
 * The persisted value is plain text — `"summary {{trigger.title}} merged"` — exactly
 * what the engine resolves. Chips are purely a rendering layer; serializing the DOM
 * back to text re-emits the `{{path}}` tokens so saves round-trip cleanly.
 */
interface VariablePickerInputProps {
  value: string;
  onChange: (next: string) => void;
  suggestions: ScopeSuggestion[];
  placeholder?: string;
  /** Start in expanded (multi-line) view. The expand button still toggles back. */
  defaultMultiline?: boolean;
  /** Back-compat alias for defaultMultiline. */
  multiline?: boolean;
  /** The JSON type the FIELD expects (from its schema). A selective type (array/number/boolean) accents the
   *  values that fit it in the picker — guidance only, never a filter. */
  expectType?: string;
}

export function VariablePickerInput({ value, onChange, suggestions, placeholder, defaultMultiline, multiline, expectType }: VariablePickerInputProps) {
  const initialMultiline = defaultMultiline ?? multiline ?? false;
  const [expanded, setExpanded] = useState(initialMultiline);

  const editorRef = useRef<HTMLDivElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);

  // Picker state.
  const [open, setOpen] = useState(false);
  const [filter, setFilter] = useState("");
  const [highlightIndex, setHighlightIndex] = useState(0);
  /** Branch ids the user has opened. While a filter is active every branch is opened so matches show. */
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  /**
   * Where the chip will go when a suggestion is picked. Two forms:
   *
   *   {node, start, virtual: false}  — typed-trigger mode (`@`, `{`, `{{`). The trigger
   *                                    char(s) literally live at `[start, caret)` in the
   *                                    document; picking replaces that slice with the chip.
   *
   *   {node, start, virtual: true}   — toolbar-button mode. NO chars were inserted on the
   *                                    button click; `start` is just the caret position
   *                                    at click time. If the user types to filter, those
   *                                    chars land in `[start, caret)` and on pick get
   *                                    replaced by the chip. If they pick without typing
   *                                    anything, the chip is inserted at `start`.
   *
   * Lives in a ref so input events (which fire across renders) can keep referring to the
   * same anchor without state-update timing surprises.
   */
  const triggerRangeRef = useRef<{ node: Text; start: number; virtual: boolean } | null>(null);

  /**
   * Single close path — every "we're done with this picker session" branch goes through
   * here so the virtual anchor never outlives the popup it belongs to. Forgetting to
   * clear the ref is what let stale virtual anchors silently re-open the picker on a
   * later click that happened to land in path-legal text.
   */
  const closePicker = () => {
    triggerRangeRef.current = null;
    setOpen(false);
  };

  /**
   * One-shot guard for the @-button → editor.focus() → onFocus race. The button click
   * sets a virtual anchor and IMMEDIATELY focuses the editor; the focus handler runs in
   * the next frame and would otherwise call detectTriggerAtCaret(false) which clears
   * virtual. This flag lets openExplicitly tell the next focus event "this isn't a
   * fresh tab-in, it's the focus I just triggered — leave my virtual anchor alone."
   */
  const suppressNextFocusDetectRef = useRef(false);
  const [justCopied, setJustCopied] = useState(false);

  // Mirror the latest serialized value so we can ignore input events that don't change
  // anything (e.g. focus shifts, IME composition setup) and avoid stomping the caret on
  // identity re-renders from upstream. Starts as `null` (NOT `value`) so the mount-time
  // hydration effect below actually runs for a non-empty initial value — otherwise a field
  // loaded with an existing `{{ref}}` (e.g. reopening a saved workflow) would render EMPTY
  // and a later blur would flush that empty DOM back as "", silently dropping the value.
  const lastSerializedRef = useRef<string | null>(null);

  // Filter → tree → the flat list of VISIBLE rows the popover renders and the keyboard navigates. A row is a
  // branch (expandable) and/or a leaf (selectable); nav + Enter work off `visibleRows[highlightIndex]`.
  const filtered = useMemo(() => filterSuggestions(suggestions, filter), [suggestions, filter]);
  const tree = useMemo(() => buildSuggestionTree(filtered), [filtered]);
  // While searching, open every branch so a matching deep field is actually visible; otherwise honour the
  // user's own expand/collapse.
  const effectiveExpanded = useMemo(() => (filter ? allBranchIds(tree) : expandedIds), [filter, tree, expandedIds]);
  const visibleRows = useMemo(() => flattenVisible(tree, effectiveExpanded), [tree, effectiveExpanded]);

  const toggleExpand = (id: string) =>
    setExpandedIds((prev) => { const next = new Set(prev); if (next.has(id)) next.delete(id); else next.add(id); return next; });

  // Reset the highlight to the first row whenever the filter text or open-state changes. The write is
  // idempotent (0 → 0 is a no-op for React), so there's no cascading-render risk the rule guards against.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { setHighlightIndex(0); }, [filter, open]);
  // Keep the highlight in range when a collapse shrinks the visible list.
  // eslint-disable-next-line react-hooks/set-state-in-effect
  useEffect(() => { setHighlightIndex((i) => Math.min(i, Math.max(0, visibleRows.length - 1))); }, [visibleRows.length]);

  // Hydrate / rehydrate the editor when `value` changes from outside. We DON'T rebuild on
  // every render — only when the externally-provided value diverges from our last serialized
  // snapshot. That keeps the caret stable while the user types and only resets it when the
  // parent component reassigns (form reset, undo, programmatic change).
  useLayoutEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;
    if (lastSerializedRef.current === value) return;

    lastSerializedRef.current = value;
    editor.innerHTML = valueToHtml(value);
  }, [value]);

  // Close picker on outside click.
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) closePicker();
    };
    window.addEventListener("mousedown", handler);
    return () => window.removeEventListener("mousedown", handler);
  }, [open]);

  // ─── Serialization + caret helpers ────────────────────────────────────────

  const flush = () => {
    const editor = editorRef.current;
    if (!editor) return;
    const text = nodeToValue(editor);
    if (text === lastSerializedRef.current) return;
    lastSerializedRef.current = text;
    onChange(text);
  };

  // ─── Picker open / pick handlers ──────────────────────────────────────────

  /**
   * Re-check picker state against the caret. Two paths:
   *
   *   (1) Virtual mode (toolbar-button anchor): the anchor sits at a saved caret position
   *       with no trigger char in the document. Keep the picker open as long as the
   *       cursor stays in the same text node, has moved forward (or stayed put) since
   *       the anchor, AND everything between anchor and cursor is path-legal — that
   *       chunk becomes the running filter. Anything else closes the picker.
   *
   *   (2) Typed-trigger mode: anchored regex matches `@`, `{`, or `{{` immediately
   *       before the caret (no chars between trigger and caret outside the path-char set).
   *       This is the only way to OPEN the picker from typing — clicking somewhere far
   *       from a trigger is a deliberate no-op.
   *
   * Pass <c>allowVirtual: false</c> to skip path (1) — used by mouse-click handlers so
   * a stale virtual anchor can never re-open the picker. The typing/onInput path leaves
   * the default (true) so live filtering after the @ button still works.
   */
  const detectTriggerAtCaret = (allowVirtual: boolean = true) => {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) { closePicker(); return; }
    const range = sel.getRangeAt(0);
    if (!range.collapsed) { closePicker(); return; }
    if (!editorRef.current?.contains(range.startContainer)) { closePicker(); return; }

    const node = range.startContainer;
    if (node.nodeType !== Node.TEXT_NODE) {
      // Caret is between elements (e.g. next to a chip) — close any open picker
      // and clear the virtual anchor if it's stale.
      closePicker();
      return;
    }

    const textNode = node as Text;
    const cursor = range.startOffset;

    // Virtual-anchor path first: if a toolbar-button click is in flight, the regex won't
    // match (no trigger char in the document) but the picker should still be open.
    // Skipped when the caller passes <c>allowVirtual: false</c> (mouse-click handlers
    // do this so a leftover virtual anchor can't resurrect the picker on a click that
    // happened to land in a path-legal chunk of text).
    const existing = triggerRangeRef.current;
    if (allowVirtual && existing?.virtual && existing.node === textNode && cursor >= existing.start) {
      const between = textNode.data.slice(existing.start, cursor);
      if (/^[a-zA-Z0-9_.]*$/.test(between)) {
        setFilter(between.trim());
        setOpen(true);
        return;
      }
      // Path-chars rule broken (e.g. user typed a space). Drop the virtual anchor and
      // fall through to the regex path below, which will probably also close the picker.
      triggerRangeRef.current = null;
    }

    const left = textNode.data.slice(0, cursor);
    const match = left.match(TRIGGER_RE);
    if (!match || match.index === undefined) { closePicker(); return; }

    triggerRangeRef.current = { node: textNode, start: match.index, virtual: false };
    setFilter(match[2].trim());
    setOpen(true);
  };

  const insertChipForPick = (suggestion: ScopeSuggestion) => {
    const editor = editorRef.current;
    const trigger = triggerRangeRef.current;
    if (!editor || !trigger) { setOpen(false); return; }
    if (!editor.contains(trigger.node)) { setOpen(false); return; }

    const sel = window.getSelection();
    const cursor = sel && sel.rangeCount > 0 ? sel.getRangeAt(0).startOffset : trigger.node.data.length;

    // Split the text node: keep [0, triggerStart) on the left, drop [triggerStart, cursor),
    // insert chip, keep [cursor, end) on the right.
    const beforeText = trigger.node.data.slice(0, trigger.start);
    const afterText = trigger.node.data.slice(cursor);

    trigger.node.data = beforeText;

    const chip = makeChip(suggestion.path);
    const tail = document.createTextNode(afterText.length > 0 ? afterText : "​");
    // Zero-width space gives us a guaranteed text node to land the caret in after the chip;
    // it strips out of the serialized value so the persisted string stays clean.

    trigger.node.parentNode?.insertBefore(chip, trigger.node.nextSibling);
    chip.parentNode?.insertBefore(tail, chip.nextSibling);

    // Place caret immediately after the chip (start of the tail text node).
    const range = document.createRange();
    range.setStart(tail, 0);
    range.collapse(true);
    sel?.removeAllRanges();
    sel?.addRange(range);

    setOpen(false);
    triggerRangeRef.current = null;
    flush();
  };

  /**
   * Toolbar @-button click. Opens the dropdown WITHOUT writing anything into the
   * document — sets a virtual anchor at the current caret position so a subsequent
   * pick still has a position to insert the chip at, and so any chars the user types
   * before picking can drive the filter.
   *
   * <para>Why virtual instead of inserting a literal `@`: operators kept reporting that
   * clicking the button "left junk in my field". With the virtual anchor the cancel
   * path (Esc / click-outside / blur without pick) leaves the document untouched.</para>
   */
  const openExplicitly = () => {
    const editor = editorRef.current;
    if (!editor) return;
    // The focus call below dispatches onFocus on the editor; we don't want that handler
    // to clear the virtual anchor we're about to set. Raise the suppress flag first.
    suppressNextFocusDetectRef.current = true;
    editor.focus();

    // Idempotent: clicking again while already open just keeps it open.
    if (open && triggerRangeRef.current) return;

    const sel = window.getSelection();

    // Resolve the caret to a text node we can anchor on. Three cases:
    //   (a) Selection inside an existing text node → anchor right there.
    //   (b) Selection at an element boundary (e.g. between two chips) → insert a fresh
    //       empty text node and anchor at offset 0.
    //   (c) No selection at all (focus race or first interaction) → append an empty
    //       text node at the end of the editor.
    let anchorNode: Text;
    let anchorOffset: number;

    if (sel && sel.rangeCount > 0) {
      const range = sel.getRangeAt(0);
      const container = range.startContainer;
      if (container.nodeType === Node.TEXT_NODE && editor.contains(container)) {
        anchorNode = container as Text;
        anchorOffset = range.startOffset;
      } else {
        const empty = document.createTextNode("");
        range.insertNode(empty);
        anchorNode = empty;
        anchorOffset = 0;
      }
    } else {
      const empty = document.createTextNode("");
      editor.appendChild(empty);
      anchorNode = empty;
      anchorOffset = 0;
    }

    placeCaretAtEnd(anchorNode, anchorOffset);
    triggerRangeRef.current = { node: anchorNode, start: anchorOffset, virtual: true };
    setFilter("");
    setOpen(true);
  };

  // ─── Keyboard handling ────────────────────────────────────────────────────

  /** Enter / click on a tree row: a selectable row inserts its {{ref}}; a pure branch toggles open. */
  const activateRow = (row: VisibleRow | undefined) => {
    if (!row) return;
    if (row.node.suggestion) { insertChipForPick(row.node.suggestion); return; }
    if (row.expandable) toggleExpand(row.node.id);
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    // Backspace / Delete adjacent to a chip: remove the WHOLE chip in one keystroke.
    // contenteditable=false handles this in most browsers, but Safari + some Chromium
    // versions leave the chip in place and just move the caret — be explicit so the UX
    // is identical across engines. The caret-position helper handles both "right after
    // the chip" (Backspace) and "right before the chip" (Delete) cases symmetrically.
    if (e.key === "Backspace" || e.key === "Delete") {
      const adjacent = chipAdjacentToCaret(e.key === "Backspace" ? "before" : "after");
      if (adjacent) {
        e.preventDefault();
        adjacent.remove();
        flush();
        setOpen(false);
        return;
      }
    }

    // Multi-line: Enter inserts a real newline. Single-line: Enter selects the highlighted row (a leaf
    // inserts its ref; a branch opens) when the picker is open, otherwise prevent an invisible newline.
    if (e.key === "Enter") {
      if (open && visibleRows[highlightIndex]) {
        e.preventDefault();
        activateRow(visibleRows[highlightIndex]);
        return;
      }
      if (!expanded) {
        e.preventDefault();
        return;
      }
    }

    if (!open) return;

    const row = visibleRows[highlightIndex];
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setHighlightIndex((i) => Math.min(i + 1, visibleRows.length - 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setHighlightIndex((i) => Math.max(i - 1, 0));
    } else if (e.key === "ArrowRight") {
      if (row?.expandable && !row.expanded) { e.preventDefault(); toggleExpand(row.node.id); }
    } else if (e.key === "ArrowLeft") {
      if (row?.expandable && row.expanded) { e.preventDefault(); toggleExpand(row.node.id); }
    } else if (e.key === "Tab") {
      if (row) { e.preventDefault(); activateRow(row); }
    } else if (e.key === "Escape") {
      e.preventDefault();
      closePicker();
    }
  };

  // ─── Toolbar actions ──────────────────────────────────────────────────────

  const copyValue = async () => {
    if (!value) return;
    try {
      await navigator.clipboard?.writeText(value);
      setJustCopied(true);
      window.setTimeout(() => setJustCopied(false), 900);
    } catch {
      // Clipboard unavailable (no HTTPS / no permission). Silently skip.
    }
  };

  const toggleExpanded = () => setExpanded((v) => !v);

  // ─── Render ───────────────────────────────────────────────────────────────

  const editorClass = expanded
    ? "wf-form-textarea wf-picker-input wf-picker-editor wf-picker-editor-multiline"
    : "wf-form-input wf-picker-input wf-picker-editor";

  return (
    <div className="wf-picker-container" ref={containerRef} data-open={open ? "true" : undefined}>
      <div
        ref={editorRef}
        className={editorClass}
        contentEditable
        suppressContentEditableWarning
        spellCheck={false}
        role="textbox"
        aria-multiline={expanded}
        data-placeholder={placeholder ?? ""}
        data-empty={value.length === 0}
        onInput={() => { flush(); detectTriggerAtCaret(); }}
        onKeyDown={onKeyDown}
        onClick={() => {
          // A click is a navigation event — explicitly NOT a continuation of a previous
          // typing session. Pass allowVirtual: false so any stale virtual anchor from
          // an earlier @ button click can't resurrect the picker. The only way clicking
          // opens the dropdown is the regex path: caret lands immediately after `@`,
          // `{`, or `{{`. Clicking on plain text or on a chip is a clean no-op.
          detectTriggerAtCaret(false);
        }}
        onFocus={() => {
          // The @-button click focuses the editor as part of opening the picker. We
          // don't want THAT focus event to immediately re-run detection (which would
          // wipe the virtual anchor we just set). Consume the one-shot suppress flag
          // and bail in that case. Tab-in / programmatic-focus paths leave the flag
          // false and get the normal regex-only re-detect.
          if (suppressNextFocusDetectRef.current) {
            suppressNextFocusDetectRef.current = false;
            return;
          }
          requestAnimationFrame(() => detectTriggerAtCaret(false));
        }}
        onBlur={flush}
      />

      <div className="wf-picker-toolbar">
        {suggestions.length > 0 && (
          <button
            type="button"
            className="wf-picker-tool"
            onClick={openExplicitly}
            title="Insert a variable — same as typing @ or {"
            tabIndex={-1}
            data-active={open ? "true" : undefined}
          ><Ic.At size={12} /></button>
        )}
        <button
          type="button"
          className="wf-picker-tool"
          onClick={copyValue}
          title="Copy value"
          tabIndex={-1}
          disabled={!value}
          data-toast={justCopied ? "true" : undefined}
        ><Ic.Copy size={11} /></button>
        <button
          type="button"
          className="wf-picker-tool"
          onClick={toggleExpanded}
          title={expanded ? "Collapse" : "Expand"}
          tabIndex={-1}
          data-active={expanded ? "true" : undefined}
        >{expanded ? <Ic.Collapse size={11} /> : <Ic.Expand size={11} />}</button>
      </div>

      {open && visibleRows.length > 0 && (
        <div className="wf-picker-popover" role="tree">
          {visibleRows.map((row, i) => {
            const newGroup = i === 0 || visibleRows[i - 1].groupCategory !== row.groupCategory;
            const selectable = row.node.suggestion != null;
            const highlighted = i === highlightIndex;
            const fits = selectable && typeFits(row.node.suggestion!.type, expectType);
            return (
              <div key={row.node.id}>
                {newGroup && <div className="wf-picker-group-h">{row.groupLabel}</div>}
                <div
                  role="treeitem"
                  className="wf-picker-item"
                  data-highlighted={highlighted}
                  data-selectable={selectable}
                  data-fit={fits || undefined}
                  aria-expanded={row.expandable ? row.expanded : undefined}
                  style={{ paddingLeft: `${8 + row.depth * 15}px` }}
                  onMouseEnter={() => setHighlightIndex(i)}
                  onMouseDown={(e) => { e.preventDefault(); activateRow(row); }}
                >
                  <span
                    className="wf-picker-twist"
                    data-on={row.expandable ? "true" : undefined}
                    onMouseDown={(e) => { if (row.expandable) { e.preventDefault(); e.stopPropagation(); toggleExpand(row.node.id); } }}
                  >
                    {row.expandable ? (row.expanded ? "▾" : "▸") : ""}
                  </span>
                  <span className="wf-picker-item-body">
                    <span className="wf-picker-item-path">{row.node.label}</span>
                    {highlighted && selectable && <span className="wf-picker-item-desc">{`{{${row.node.suggestion!.path}}}`}</span>}
                  </span>
                  {row.node.typeHint && <span className="wf-picker-item-type" data-kind={row.node.typeHint}>{row.node.typeHint}</span>}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ─── Helpers ────────────────────────────────────────────────────────────────────

/**
 * Trigger token anchored at the caret. The alternatives are tried left-to-right so
 * `{{` wins over `{` — a typed `{{x` is recognised as the double-brace form, not as a
 * single `{` followed by something. Path-legal characters after the trigger become the
 * running filter; typing any character outside that set (e.g. `}`, space, punctuation)
 * stops the match and closes the picker.
 */
const TRIGGER_RE = /(\{\{|\{|@)([a-zA-Z0-9_.]*)$/;

const TOKEN_RE = /\{\{\s*([a-zA-Z_][a-zA-Z0-9_.]*)\s*\}\}/g;

function valueToHtml(value: string): string {
  let out = "";
  let i = 0;
  TOKEN_RE.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = TOKEN_RE.exec(value)) !== null) {
    out += escapeHtml(value.slice(i, match.index));
    out += renderChipHtml(match[1]);
    i = match.index + match[0].length;
  }
  out += escapeHtml(value.slice(i));
  return out;
}

function renderChipHtml(path: string): string {
  const safe = escapeHtml(path);
  const attr = escapeAttr(path);
  // contenteditable="false" makes the chip a single delete unit and stops the caret
  // from landing inside its text. data-path is what the serializer reads back.
  return `<span class="wf-picker-chip" contenteditable="false" data-path="${attr}" title="${attr}">${safe}</span>`;
}

function makeChip(path: string): HTMLElement {
  const chip = document.createElement("span");
  chip.className = "wf-picker-chip";
  chip.contentEditable = "false";
  chip.dataset.path = path;
  chip.title = path;
  chip.textContent = path;
  return chip;
}

function nodeToValue(node: Node): string {
  let out = "";
  for (const child of Array.from(node.childNodes)) {
    if (child.nodeType === Node.TEXT_NODE) {
      out += (child.textContent ?? "").replace(/\u200B/g, "");
    } else if (child.nodeType === Node.ELEMENT_NODE) {
      const el = child as HTMLElement;
      if (el.classList.contains("wf-picker-chip") && el.dataset.path) {
        out += `{{${el.dataset.path}}}`;
      } else if (el.tagName === "BR") {
        out += "\n";
      } else if (el.tagName === "DIV" || el.tagName === "P") {
        // Some browsers wrap soft newlines in <div>; treat each block as a line break in
        // the serialized value so multi-line round-trips don't collapse onto one line.
        if (out.length > 0 && !out.endsWith("\n")) out += "\n";
        out += nodeToValue(el);
      } else {
        out += nodeToValue(el);
      }
    }
  }
  return out;
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;");
}

function escapeAttr(s: string): string {
  return escapeHtml(s).replace(/'/g, "&#39;");
}

function placeCaretAtEnd(node: Text, offset: number) {
  const sel = window.getSelection();
  if (!sel) return;
  const range = document.createRange();
  range.setStart(node, Math.min(offset, node.data.length));
  range.collapse(true);
  sel.removeAllRanges();
  sel.addRange(range);
}

/**
 * Returns the chip element immediately before (Backspace) or after (Delete) a collapsed
 * caret, or null if there isn't one. Handles both possible DOM shapes for the caret
 * position: inside a text node at offset 0 / end, OR between sibling elements of the
 * editor itself.
 */
function chipAdjacentToCaret(direction: "before" | "after"): HTMLElement | null {
  const sel = window.getSelection();
  if (!sel || sel.rangeCount === 0) return null;
  const range = sel.getRangeAt(0);
  if (!range.collapsed) return null; // selection — let default handle it

  const container = range.startContainer;
  const offset = range.startOffset;

  let candidate: ChildNode | null = null;
  if (container.nodeType === Node.TEXT_NODE) {
    const text = container as Text;
    if (direction === "before" && offset === 0) {
      candidate = text.previousSibling;
    } else if (direction === "after" && offset === text.data.length) {
      candidate = text.nextSibling;
    }
  } else if (container.nodeType === Node.ELEMENT_NODE) {
    const children = (container as Element).childNodes;
    if (direction === "before" && offset > 0) {
      candidate = children[offset - 1] ?? null;
    } else if (direction === "after" && offset < children.length) {
      candidate = children[offset] ?? null;
    }
  }

  if (!candidate || candidate.nodeType !== Node.ELEMENT_NODE) return null;
  const el = candidate as HTMLElement;
  if (!el.classList.contains("wf-picker-chip")) return null;
  return el;
}

function filterSuggestions(all: ScopeSuggestion[], filter: string): ScopeSuggestion[] {
  if (!filter) return all;
  const f = filter.toLowerCase();
  return all.filter((s) => s.path.toLowerCase().includes(f) || s.label.toLowerCase().includes(f));
}

