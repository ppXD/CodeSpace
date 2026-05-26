import {
  Background,
  BackgroundVariant,
  ConnectionLineType,
  ConnectionMode,
  Controls,
  MarkerType,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  useReactFlow,
  type Connection,
  type Edge,
  type EdgeChange,
  type Node,
  type NodeChange,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";

import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useEffect, useMemo, useRef, useState, type DragEvent } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type {
  EdgeDefinition,
  NodeDefinition,
  NodeManifestDto,
  WorkflowActivationInput,
  WorkflowDefinition,
  WorkflowDetail,
} from "@/api/workflows";
import { SchemaForm } from "@/components/workflows/SchemaForm";
import { introspectScope } from "@/components/workflows/scope-introspection";
import { VariableTablePanel } from "@/components/workflows/VariableTablePanel";
import { WorkflowNode, type WorkflowNodeData } from "@/components/workflows/WorkflowNode";
import { WorkflowVariablesPanel } from "@/components/workflows/WorkflowVariablesPanel";

// Five panels covering every scope the engine resolves:
//   - variables  → wf.*    (Variable table, scope=Workflow, persists immediately via API)
//   - team       → team.*  (Variable table, scope=Team — SHARED across every workflow in
//                           this team; the panel's tip surfaces that semantic so authors
//                           don't accidentally treat it as workflow-local)
//   - inputs     → input.*  (declared in workflow JSON, saved with the definition)
//   - outputs    → output.* (declared in workflow JSON, saved with the definition)
//   - system     → sys.*    (engine-injected, read-only documentation)
// Team variables are *also* manageable from Team Settings → Variables; the in-editor panel
// is a convenience so authors don't have to leave the canvas to add a shared value.
type OpenVarsPanel = "variables" | "team" | "inputs" | "outputs" | "system";
import { useNodeManifests, useSystemVariables, useUpdateWorkflow, useWorkflow } from "@/hooks/use-workflows";
// Real per-scope variable lists feed the autocomplete picker + toolbar counts.
import { useTeamVariables, useWorkflowVariables } from "@/hooks/use-variables";

/**
 * Dify-style canvas editor. Three-pane layout:
 *
 *   [palette]        [ ───────── canvas ───────── ]        [inspector]
 *      ↓                  React Flow                          schema-driven form
 *      drag a node onto the canvas                            for the selected node
 *      to add it
 *
 * Generic by design — the palette + inspector are driven entirely by
 * `GET /api/workflows/node-manifests`. Any new node a plugin registers shows up
 * in the palette automatically, and its config + inputs forms come for free from
 * its JSON schemas. No editor code change required for new node types.
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/workflows/$workflowId/")({
  component: WorkflowEditorPage,
});

function WorkflowEditorPage() {
  return (
    // ReactFlowProvider must wrap any component that calls useReactFlow().
    <ReactFlowProvider>
      <EditorShell />
    </ReactFlowProvider>
  );
}

function EditorShell() {
  const { teamSlug, workflowId } = Route.useParams();
  const navigate = useNavigate();
  const workflow = useWorkflow(workflowId);
  const manifests = useNodeManifests();
  const update = useUpdateWorkflow();

  if (workflow.isLoading || manifests.isLoading) {
    return (
      <section className="ct">
        <div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading editor…</div></div></div>
      </section>
    );
  }

  if (workflow.error instanceof ApiError || !workflow.data) {
    return (
      <section className="ct">
        <div className="ct-body">
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Workflow not found</div>
            <div className="cn-banner-p">{workflow.error?.message ?? "It may have been deleted."}</div>
          </div>
        </div>
      </section>
    );
  }

  return (
    <Editor
      workflow={workflow.data}
      manifests={manifests.data ?? []}
      onBackToList={() => navigate({ to: "/teams/$teamSlug/workflows", params: { teamSlug } })}
      onOpenRuns={() => navigate({ to: "/teams/$teamSlug/workflows/$workflowId/runs", params: { teamSlug, workflowId } })}
      saving={update.isPending}
      onSave={(input) => update.mutateAsync({ workflowId, input })}
    />
  );
}

interface EditorProps {
  workflow: WorkflowDetail;
  manifests: NodeManifestDto[];
  onBackToList: () => void;
  onOpenRuns: () => void;
  saving: boolean;
  onSave: (input: {
    name: string;
    description: string | null;
    definition: WorkflowDefinition;
    activations: WorkflowActivationInput[];
  }) => Promise<unknown>;
}

function Editor({ workflow, manifests, onBackToList, onOpenRuns, saving, onSave }: EditorProps) {
  // Editable workflow header — start as a copy of the persisted name; the user can rename
  // in place via the input above the canvas.
  const [name, setName] = useState(workflow.name);

  // React Flow state derived from the workflow definition on first mount + every time the
  // user re-enters the route. Subsequent changes are owned by React Flow itself.
  const manifestByType = useMemo(() => new Map(manifests.map((m) => [m.typeKey, m])), [manifests]);

  // Live variable lists drive (a) the toolbar count badges on Variables/Team and (b) the
  // per-name autocomplete in the {{}} picker so {{team.test}} appears the moment the
  // operator saves it via the Team panel. React-Query's cache invalidation in
  // useSetTeamVariable/useSetWorkflowVariable means we don't need an explicit refresh here.
  const workflowVariablesQuery = useWorkflowVariables(workflow.id);
  const teamVariablesQuery = useTeamVariables();
  const systemVariablesQuery = useSystemVariables();
  const workflowVariablesList = workflowVariablesQuery.data ?? [];
  const teamVariablesList = teamVariablesQuery.data ?? [];
  const systemVariablesList = systemVariablesQuery.data ?? [];

  const [nodes, setNodes] = useState<Node<WorkflowNodeData>[]>(() =>
    definitionToRfNodes(workflow.definition, manifestByType)
  );
  const [edges, setEdges] = useState<Edge[]>(() => definitionToRfEdges(workflow.definition));
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [unsaved, setUnsaved] = useState(false);

  // Lookup the selected node's definition (config/inputs come from React Flow node data).
  const selectedNode = nodes.find((n) => n.id === selectedId) ?? null;
  const selectedManifest = selectedNode ? manifestByType.get(selectedNode.data.typeKey) ?? null : null;

  // Per-node config + inputs state. The React Flow node holds the lightweight visual
  // data; the full config/inputs JSON lives here, keyed by node id, and gets folded
  // back into the WorkflowDefinition at save time.
  const [configs, setConfigs] = useState<Record<string, Record<string, unknown>>>(() => initialBag(workflow.definition, "config"));
  const [inputs, setInputs] = useState<Record<string, Record<string, unknown>>>(() => initialBag(workflow.definition, "inputs"));
  const [nodeLabels, setNodeLabels] = useState<Record<string, string>>(() =>
    Object.fromEntries(workflow.definition.nodes.map((n) => [n.id, n.label ?? ""]))
  );

  // Workflow IO contract — inputs/outputs are part of the workflow JSON because they
  // define the workflow's external API. Persisted via the Save button. The wf.* and
  // team.* scopes live in the unified `variable` table; VariableTablePanel persists
  // every change immediately via API, so no editor-side state is needed for them.
  const [workflowInputs, setWorkflowInputs] = useState<import("@/api/workflows").WorkflowVariable[]>(workflow.definition.inputs ?? []);
  const [workflowOutputs, setWorkflowOutputs] = useState<import("@/api/workflows").WorkflowVariable[]>(workflow.definition.outputs ?? []);

  // Re-sync the variable scopes from props whenever the workflow refetches AND the user
  // has no unsaved local changes. Reason: useState's initializer only fires on first
  // mount, so a fresh fetch (after Save → cache invalidate → refetch) would otherwise
  // leave local state stuck at the original load. We compare against the previous
  // workflow snapshot so we only re-sync on a real prop change, never on every render.
  const lastSyncedVersionRef = useRef<{ id: string; lastModifiedDate: string }>({
    id: workflow.id,
    lastModifiedDate: workflow.lastModifiedDate,
  });
  const [openVarsPanel, setOpenVarsPanel] = useState<OpenVarsPanel | null>(null);
  const markVarsDirty = () => setUnsaved(true);

  const { screenToFlowPosition, getViewport } = useReactFlow();
  const canvasRef = useRef<HTMLDivElement | null>(null);

  // ─── Canvas events ──────────────────────────────────────────────────────────
  const onNodesChange = (changes: NodeChange[]) => {
    setNodes((nds) => applyNodeChanges(changes, nds) as Node<WorkflowNodeData>[]);
    if (changes.some((c) => c.type === "position" || c.type === "remove")) setUnsaved(true);
  };
  const onEdgesChange = (changes: EdgeChange[]) => {
    setEdges((eds) => applyEdgeChanges(changes, eds));
    if (changes.some((c) => c.type === "remove")) setUnsaved(true);
  };
  const onConnect = (params: Connection) => {
    // De-dupe — React Flow itself doesn't prevent duplicate edges; we do.
    setEdges((eds) => {
      const exists = eds.some((e) => e.source === params.source && e.target === params.target);
      if (exists) return eds;
      return addEdge({ ...params, type: "smoothstep", animated: true }, eds);
    });
    setUnsaved(true);
  };

  /**
   * Live rule check fired by React Flow while the user drags a connection line. Returning
   * false makes the line render in the "invalid" state (no snap, dim colour) so the user
   * gets immediate feedback BEFORE they release the mouse. Same checks run again on
   * onConnect for safety. Rules — match what the backend DefinitionValidator enforces:
   *
   *   1. Source ≠ target (no self-loop)
   *   2. Edge doesn't already exist
   *   3. Target is not a Trigger (Triggers have no inputs)
   *   4. Source is not a Terminal (Terminals have no outputs)
   *   5. Adding the edge wouldn't create a cycle (DAG-only)
   */
  const isValidConnection = (params: Connection | Edge) => {
    const sourceId = "source" in params ? params.source : null;
    const targetId = "target" in params ? params.target : null;
    if (!sourceId || !targetId) return false;
    if (sourceId === targetId) return false;

    const exists = edges.some((e) => e.source === sourceId && e.target === targetId);
    if (exists) return false;

    const sourceNode = nodes.find((n) => n.id === sourceId);
    const targetNode = nodes.find((n) => n.id === targetId);
    if (!sourceNode || !targetNode) return false;
    if (targetNode.data.kind === "Trigger") return false;
    if (sourceNode.data.kind === "Terminal") return false;

    // Cycle check: would the new edge create source → … → source? Walk forward from
    // the target; if we reach the source, adding the edge closes a loop.
    if (createsCycle(sourceId, targetId, edges)) return false;

    return true;
  };

  // ─── Trigger-count guard ────────────────────────────────────────────────────
  // Engine assumes exactly one Trigger per workflow (the entry point — the engine
  // starts walking from it). The UI mirrors that constraint by disabling the trigger
  // palette items once one is on the canvas, so the user can't add a second.
  const hasTrigger = useMemo(() => nodes.some((n) => n.data.kind === "Trigger"), [nodes]);
  const isPaletteItemDisabled = (m: NodeManifestDto): { disabled: boolean; reason?: string } => {
    if (m.kind === "Trigger" && hasTrigger) {
      return { disabled: true, reason: "Only one trigger per workflow. Delete the existing trigger first." };
    }
    return { disabled: false };
  };

  // ─── Adding nodes — shared by drag-drop AND click-to-add ────────────────────
  // Position strategy:
  //   1. Explicit screen coords (drag-drop): convert via screenToFlowPosition
  //   2. With a selected node: 200px below the selected node (auto-link source → new)
  //   3. Otherwise: visible centre of the canvas (in flow coords)
  // Auto-link only happens for click-to-add AND only when the new node has a target
  // handle (Trigger nodes have no input, so we never auto-link INTO them).
  const addNodeFromManifest = (
    manifest: NodeManifestDto,
    options: { screen?: { x: number; y: number }; autoLink?: boolean } = {}
  ) => {
    const id = uniqueNodeId(manifest.typeKey, nodes);
    const position = computeAddPosition(manifest, options.screen);

    setNodes((nds) => [
      ...nds,
      {
        id,
        type: "wf",
        position,
        data: {
          nodeId: id,
          typeKey: manifest.typeKey,
          displayName: manifest.displayName,
          iconKey: manifest.iconKey,
          kind: manifest.kind,
          category: manifest.category,
          label: null,
        },
      },
    ]);
    setConfigs((c) => ({ ...c, [id]: defaultsFromSchema(manifest.configSchema) }));
    setInputs((c) => ({ ...c, [id]: defaultsFromSchema(manifest.inputSchema) }));
    setNodeLabels((l) => ({ ...l, [id]: "" }));
    setSelectedId(id);
    setUnsaved(true);

    // Auto-link from the previously-selected node into the new one. Only when:
    //   - this was a click-to-add (not a free-form drop)
    //   - there IS a previously-selected node
    //   - the new node accepts incoming edges (Trigger doesn't)
    //   - the source node can emit edges (Terminal can't)
    if (options.autoLink && selectedId && selectedId !== id && manifest.kind !== "Trigger") {
      const sourceNode = nodes.find((n) => n.id === selectedId);
      if (sourceNode && sourceNode.data.kind !== "Terminal") {
        // addEdge accepts a Connection (no id required) and returns the edge with a
        // generated id. Cast to Connection to satisfy the overload — sourceHandle /
        // targetHandle null is fine because our nodes only have one handle per side.
        setEdges((eds) => addEdge(
          { source: selectedId, target: id, sourceHandle: null, targetHandle: null, type: "smoothstep", animated: true } as Connection,
          eds,
        ));
      }
    }
  };

  const computeAddPosition = (_manifest: NodeManifestDto, screen?: { x: number; y: number }) => {
    if (screen) return screenToFlowPosition(screen);

    // Place below the currently-selected node when one exists — natural "next step" UX.
    if (selectedId) {
      const src = nodes.find((n) => n.id === selectedId);
      if (src) return { x: src.position.x, y: src.position.y + 200 };
    }

    // Otherwise drop into the visible centre of the canvas. Convert by un-applying the
    // current viewport transform manually — useReactFlow().screenToFlowPosition needs
    // a real screen coord, which we don't have for a click-to-add from the palette.
    const rect = canvasRef.current?.getBoundingClientRect();
    if (!rect) return { x: 200, y: 200 };

    const { x, y, zoom } = getViewport();
    const offsetForNodeSize = 100;   // half of approximate node width so it lands centred
    return {
      x: (rect.width / 2 - x) / zoom - offsetForNodeSize,
      y: (rect.height / 2 - y) / zoom - 30,
    };
  };

  // ─── Palette drag-and-drop ──────────────────────────────────────────────────
  const onDragOver = (e: DragEvent) => {
    e.preventDefault();
    e.dataTransfer.dropEffect = "move";
  };
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    const typeKey = e.dataTransfer.getData("application/x-workflow-node");
    if (!typeKey) return;
    const manifest = manifestByType.get(typeKey);
    if (!manifest) return;

    // Same trigger-limit guard as click-to-add.
    if (isPaletteItemDisabled(manifest).disabled) return;

    addNodeFromManifest(manifest, { screen: { x: e.clientX, y: e.clientY } });
  };

  // ─── Click-to-add from palette ──────────────────────────────────────────────
  const onPaletteClick = (manifest: NodeManifestDto) => {
    // Defense-in-depth — the palette item is already disabled when the limit's hit,
    // but a keyboard-driven activate could still fire. Silently no-op rather than
    // creating an invalid second trigger.
    const { disabled } = isPaletteItemDisabled(manifest);
    if (disabled) return;

    addNodeFromManifest(manifest, { autoLink: true });
  };

  // ─── Inspector ──────────────────────────────────────────────────────────────
  const updateConfig = (id: string, next: Record<string, unknown>) => {
    setConfigs((c) => ({ ...c, [id]: next }));
    setUnsaved(true);
  };
  const updateInputs = (id: string, next: Record<string, unknown>) => {
    setInputs((c) => ({ ...c, [id]: next }));
    setUnsaved(true);
  };
  const updateLabel = (id: string, label: string) => {
    setNodeLabels((l) => ({ ...l, [id]: label }));
    setNodes((nds) => nds.map((n) => n.id === id ? { ...n, data: { ...n.data, label: label || null } } : n));
    setUnsaved(true);
  };

  // ─── Save ───────────────────────────────────────────────────────────────────
  const handleSave = async () => {
    const definition = rfToDefinition(nodes, edges, configs, inputs, nodeLabels, workflowInputs, workflowOutputs);
    const activations = deriveActivations(definition, workflow.activations, manifestByType);
    await onSave({ name, description: workflow.description, definition, activations });
    setUnsaved(false);
    // No navigation on save — keep the user on the canvas. The "Unsaved changes" pill
    // disappears and the user can immediately keep editing. Matches Dify behaviour.
  };

  // Block accidental navigation when there are unsaved changes.
  useEffect(() => {
    if (!unsaved) return;
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = ""; };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [unsaved]);

  // Re-hydrate the variable-scope state when the workflow refetches with a fresher
  // lastModifiedDate (e.g. after Save → cache invalidate → refetch). useState's
  // initializer only fires on first mount, so without this effect the local scope
  // arrays stay locked to whatever they were on initial load. Guarded by `unsaved`:
  // if the user has pending local edits, we do NOT clobber them. We also skip the
  // very first match (same lastModifiedDate as the original mount).
  useEffect(() => {
    if (unsaved) return;
    const last = lastSyncedVersionRef.current;
    if (last.id === workflow.id && last.lastModifiedDate === workflow.lastModifiedDate) return;

    setWorkflowInputs(workflow.definition.inputs ?? []);
    setWorkflowOutputs(workflow.definition.outputs ?? []);
    lastSyncedVersionRef.current = { id: workflow.id, lastModifiedDate: workflow.lastModifiedDate };
  }, [workflow, unsaved]);

  return (
    <div className="wf-editor">
      {/* Top bar */}
      <header className="wf-editor-bar">
        <button className="btn btn-ghost" onClick={onBackToList} title="Back to workflows list">
          <Ic.ArrowLeft size={13} />
        </button>
        <input
          className="wf-editor-name"
          value={name}
          onChange={(e) => { setName(e.target.value); setUnsaved(true); }}
          placeholder="Workflow name"
        />

        <div className="wf-editor-bar-spacer" />
        {unsaved && <span className="wf-editor-unsaved">Unsaved changes</span>}

        {/* Right-side toolbar — three button groups separated by dividers so the operator
            can scan by purpose at a glance:
              · Group 1: scope-reading data (Variables / System / Environment)
              · Group 2: workflow IO contract (Inputs / Outputs)
              · Group 3: run actions (history / Save)
            Each group lives inside its own .wf-editor-toolbar-group; the .sep span draws
            the divider between them. Tooltips spell out the {{ref}} prefix for each scope. */}
        <div className="wf-editor-toolbar" role="toolbar" aria-label="Workflow scopes + actions">
          <div className="wf-editor-toolbar-group" aria-label="Scope variables">
            {/* Left-to-right = scope widens: workflow → team → engine. */}
            <ToolbarButton
              icon={<Ic.Workflow size={14} />}
              label="Variables"
              count={workflowVariablesList.length}
              active={openVarsPanel === "variables"}
              onClick={() => setOpenVarsPanel((p) => p === "variables" ? null : "variables")}
              tooltip="This workflow only. {{wf.X}}"
            />
            <ToolbarButton
              icon={<Ic.Key size={14} />}
              label="Team"
              count={teamVariablesList.length}
              active={openVarsPanel === "team"}
              onClick={() => setOpenVarsPanel((p) => p === "team" ? null : "team")}
              tooltip="Shared across this team. {{team.X}}"
            />
            <ToolbarButton
              icon={<Ic.Box size={14} />}
              label="System"
              count={systemVariablesList.length}
              active={openVarsPanel === "system"}
              onClick={() => setOpenVarsPanel((p) => p === "system" ? null : "system")}
              tooltip="Engine-injected per run. {{sys.X}}"
            />
          </div>

          <span className="wf-editor-toolbar-sep" aria-hidden="true" />

          <div className="wf-editor-toolbar-group" aria-label="Workflow IO">
            <ToolbarButton
              icon={<Ic.Zap size={14} />}
              label="Inputs"
              count={workflowInputs.length}
              active={openVarsPanel === "inputs"}
              onClick={() => setOpenVarsPanel((p) => p === "inputs" ? null : "inputs")}
              tooltip="Workflow Inputs — per-run parameters the caller passes in. Reference via {{input.X}}."
            />
            <ToolbarButton
              icon={<Ic.CircleStop size={14} />}
              label="Outputs"
              count={workflowOutputs.length}
              active={openVarsPanel === "outputs"}
              onClick={() => setOpenVarsPanel((p) => p === "outputs" ? null : "outputs")}
              tooltip="Workflow Outputs — what this workflow returns on Success. Filled by the Terminal node's Inputs map."
            />
          </div>

          <span className="wf-editor-toolbar-sep" aria-hidden="true" />

          <div className="wf-editor-toolbar-group" aria-label="Run actions">
            <button className="wf-editor-toolbar-btn" onClick={onOpenRuns} title="Run history">
              <Ic.Clock size={14} />
            </button>
            <button
              className={`wf-editor-toolbar-publish ${unsaved ? "wf-editor-toolbar-publish-dirty" : ""}`}
              onClick={handleSave}
              disabled={saving}
              title={unsaved ? "Save the current draft" : "All changes saved"}
            >
              {saving ? "Saving…" : "Save"}
            </button>
          </div>
        </div>
      </header>

      {/* Slide-over for the Variables / Inputs / Outputs / System panels. Closes on
          outside-click of the panel via the mask. The current toolbar selection drives
          which one is shown. System is read-only — onChange is still required by the
          component contract but the panel never invokes it. */}
      {openVarsPanel && (
        <div className="wf-editor-vars-mask" onClick={() => setOpenVarsPanel(null)}>
          <aside className="wf-editor-vars-drawer" onClick={(e) => e.stopPropagation()}>
            {/* wf + team scopes live in the unified variable table and persist immediately
                via API (the same data the Team Settings page edits — no two-source-of-truth
                risk). Inputs/Outputs/System flow through the workflow definition JSON + the
                editor's Save button. */}
            {openVarsPanel === "variables" && (
              <VariableTablePanel
                scope="Workflow"
                workflowId={workflow.id}
                refPrefix="wf"
                title="Variables"
                subtitle="This workflow only. {{wf.<name>}}"
                tip="Use Secret type for sensitive values."
                emptyHint="No variables yet."
              />
            )}
            {openVarsPanel === "team" && (
              <VariableTablePanel
                scope="Team"
                refPrefix="team"
                title="Team variables"
                subtitle="Shared across every workflow in this team. {{team.<name>}}"
                tip="Edits here affect every workflow in this team."
                emptyHint="No team variables yet."
              />
            )}
            {(openVarsPanel === "inputs" || openVarsPanel === "outputs" || openVarsPanel === "system") && (
              <WorkflowVariablesPanel
                kind={openVarsPanel}
                items={openVarsPanel === "inputs"  ? workflowInputs
                     : openVarsPanel === "outputs" ? workflowOutputs
                                                    : []}
                systemVariables={openVarsPanel === "system" ? systemVariablesList : undefined}
                onChange={(next) => {
                  if (openVarsPanel === "inputs") setWorkflowInputs(next);
                  else if (openVarsPanel === "outputs") setWorkflowOutputs(next);
                  // System: no-op — engine populates these at runtime, not edited here.
                  markVarsDirty();
                }}
              />
            )}
          </aside>
        </div>
      )}

      {/* Three-pane body */}
      <div className="wf-editor-body">
        {/* Left palette */}
        <aside className="wf-palette">
          <div className="wf-palette-h">Nodes</div>
          <div className="wf-palette-hint">Click to add · Drag for precise placement</div>
          {manifests.length === 0 && <div className="wf-palette-empty">No node types loaded.</div>}
          <PaletteSection title="Triggers"  manifests={manifests.filter((m) => m.kind === "Trigger")}  onAdd={onPaletteClick} disabledOf={isPaletteItemDisabled} />
          <PaletteSection title="Steps"     manifests={manifests.filter((m) => m.kind === "Regular")}  onAdd={onPaletteClick} disabledOf={isPaletteItemDisabled} />
          <PaletteSection title="Endpoints" manifests={manifests.filter((m) => m.kind === "Terminal")} onAdd={onPaletteClick} disabledOf={isPaletteItemDisabled} />
        </aside>

        {/* Canvas */}
        <div ref={canvasRef} className="wf-canvas" onDragOver={onDragOver} onDrop={onDrop}>
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={NODE_TYPES}
            onNodesChange={onNodesChange}
            onEdgesChange={onEdgesChange}
            onConnect={onConnect}
            onNodeClick={(_, n) => setSelectedId(n.id)}
            onPaneClick={() => setSelectedId(null)}
            fitView
            // Live rule check: as the user drags a connection line, this runs every frame
            // and dims the line when the would-be edge violates a rule (self-loop, dup,
            // wrong handle direction, cycle). Snap-to-target is suppressed for invalid drops.
            isValidConnection={isValidConnection}
            // Connection ergonomics: larger snap radius so the user can drop the line
            // anywhere near a handle (not just exactly on it). Loose mode lets a source
            // handle connect to a target handle regardless of which side it's on.
            connectionRadius={40}
            connectionMode={ConnectionMode.Loose}
            connectionLineType={ConnectionLineType.SmoothStep}
            connectionLineStyle={{ stroke: "#D97757", strokeWidth: 2 }}
            defaultEdgeOptions={{
              type: "smoothstep",
              animated: true,
              markerEnd: { type: MarkerType.ArrowClosed, color: "#B5B0A2", width: 16, height: 16 },
            }}
            deleteKeyCode={["Backspace", "Delete"]}
            multiSelectionKeyCode={["Meta", "Control"]}
            proOptions={{ hideAttribution: true }}
          >
            <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="#E6E1D5" />
            <Controls showInteractive={false} />
            <MiniMap pannable zoomable maskColor="rgba(248,246,238,0.7)" />
          </ReactFlow>

          {nodes.length === 0 && <CanvasEmptyHint />}
        </div>

        {/* Right inspector */}
        <aside className="wf-inspector">
          {!selectedNode && <InspectorEmptyHint />}
          {selectedNode && selectedManifest && (
            <NodeInspector
              nodeId={selectedNode.id}
              manifest={selectedManifest}
              label={nodeLabels[selectedNode.id] ?? ""}
              config={configs[selectedNode.id] ?? {}}
              inputs={inputs[selectedNode.id] ?? {}}
              // Live definition snapshot — recomputed every render from React Flow state +
              // workflow Variables/Inputs/Outputs. Drives the autocomplete picker and the
              // upstream outputs hint card so suggestions stay accurate as the user edits.
              liveDefinition={rfToDefinition(nodes, edges, configs, inputs, nodeLabels, workflowInputs, workflowOutputs)}
              manifestByType={manifestByType}
              // Live variable rows from the unified `variable` table. Threaded through to
              // introspectScope so the {{}} picker autocompletes real names (e.g. {{team.test}})
              // the moment the operator saves them via the side panel.
              workflowVariables={workflowVariablesList}
              teamVariables={teamVariablesList}
              // sys.* descriptors fetched from /api/workflows/system-variables — single source
              // of truth lives on the backend (SystemScopeKeys.Descriptors).
              systemVariables={systemVariablesList}
              onLabelChange={(v) => updateLabel(selectedNode.id, v)}
              onConfigChange={(v) => updateConfig(selectedNode.id, v)}
              onInputsChange={(v) => updateInputs(selectedNode.id, v)}
            />
          )}
        </aside>
      </div>
    </div>
  );
}

// ─── Guidance overlays ────────────────────────────────────────────────────────

function CanvasEmptyHint() {
  return (
    <div className="wf-canvas-hint">
      <div className="wf-canvas-hint-card">
        <Ic.Workflow size={20} />
        <div className="wf-canvas-hint-h">Build your workflow</div>
        <ol className="wf-canvas-hint-steps">
          <li><strong>Click</strong> a node in the left palette to add it</li>
          <li><strong>Drag</strong> from the bottom dot of one node to the top dot of another to connect</li>
          <li><strong>Click</strong> any node to edit its config in the right panel</li>
        </ol>
        <div className="wf-canvas-hint-rules">
          <strong>Rules:</strong> one trigger per workflow · no cycles · triggers have no input · terminals have no output
        </div>
      </div>
    </div>
  );
}

function InspectorEmptyHint() {
  return (
    <div className="wf-inspector-empty">
      <Ic.Settings size={18} />
      <div className="wf-inspector-empty-h">No node selected</div>
      <ul className="wf-inspector-empty-list">
        <li>Click a node on the canvas to edit it</li>
        <li>Click a palette item to add a node</li>
        <li>Press <kbd>Delete</kbd> to remove a selected node or edge</li>
      </ul>
    </div>
  );
}

// ─── Palette ──────────────────────────────────────────────────────────────────

function PaletteSection({ title, manifests, onAdd, disabledOf }: {
  title: string;
  manifests: NodeManifestDto[];
  onAdd: (m: NodeManifestDto) => void;
  disabledOf: (m: NodeManifestDto) => { disabled: boolean; reason?: string };
}) {
  if (manifests.length === 0) return null;
  return (
    <div className="wf-palette-section">
      <div className="wf-palette-section-h">{title}</div>
      {manifests.map((m) => {
        const { disabled, reason } = disabledOf(m);
        return <PaletteItem key={m.typeKey} manifest={m} onAdd={onAdd} disabled={disabled} disabledReason={reason} />;
      })}
    </div>
  );
}

function PaletteItem({ manifest, onAdd, disabled, disabledReason }: {
  manifest: NodeManifestDto;
  onAdd: (m: NodeManifestDto) => void;
  disabled: boolean;
  disabledReason?: string;
}) {
  const baseTitle = manifest.description ?? `Click to add  ·  Drag to position\n${manifest.typeKey}`;
  const title = disabled && disabledReason ? disabledReason : baseTitle;

  return (
    <button
      type="button"
      className="wf-palette-item"
      data-disabled={disabled}
      // draggable=false on disabled items so the user can't bypass the click guard via drag.
      draggable={!disabled}
      disabled={disabled}
      onClick={() => onAdd(manifest)}
      onDragStart={(e) => {
        if (disabled) { e.preventDefault(); return; }
        e.dataTransfer.setData("application/x-workflow-node", manifest.typeKey);
        e.dataTransfer.effectAllowed = "move";
      }}
      title={title}
    >
      <span className="wf-palette-item-icon">{paletteIconFor(manifest)}</span>
      <span className="wf-palette-item-body">
        <span className="wf-palette-item-name">{manifest.displayName}</span>
        <span className="wf-palette-item-key">{manifest.typeKey}</span>
      </span>
      <span className="wf-palette-item-add" aria-hidden>{disabled ? "—" : "+"}</span>
    </button>
  );
}

function paletteIconFor(m: NodeManifestDto) {
  // Kind-then-Category cascade. Reading the manifest's declared Category is the source of
  // truth — a typeKey-prefix probe would silently mis-icon plugin authors who name their
  // LLM node "anthropic.chat" or "openai.complete".
  if (m.kind === "Trigger") return <Ic.Zap size={12} />;
  if (m.kind === "Terminal") return <Ic.CircleStop size={12} />;
  if (m.category === "AI") return <Ic.Sparkles size={12} />;
  if (m.category === "Git") return <Ic.Branch size={12} />;
  return <Ic.Box size={12} />;
}

// ─── Top-bar toolbar button ───────────────────────────────────────────────────

/**
 * Icon-first button for the top-bar workflow-scope toolbar. Counts render as a small
 * badge on the icon so the icon stays the primary affordance and the user can spot at a
 * glance which scopes have content without reading text labels. Tooltip carries the
 * concept explanation (and the Dify equivalence note for users coming from there).
 */
function ToolbarButton({ icon, label, count, active, onClick, tooltip }: {
  icon: React.ReactNode;
  label: string;
  // Count is optional — wf + team scopes pull from the API on-demand inside their panels,
  // so the editor doesn't know the count up-front to badge it.
  count?: number;
  active: boolean;
  onClick: () => void;
  tooltip: string;
}) {
  return (
    <button
      type="button"
      className="wf-editor-toolbar-btn wf-editor-toolbar-btn-scope"
      data-active={active}
      onClick={onClick}
      title={tooltip}
      aria-label={label}
      aria-pressed={active}
    >
      {icon}
      {count != null && count > 0 && <span className="wf-editor-toolbar-badge">{count}</span>}
    </button>
  );
}

// ─── Inspector ────────────────────────────────────────────────────────────────

function NodeInspector({
  nodeId,
  manifest,
  label,
  config,
  inputs,
  liveDefinition,
  manifestByType,
  workflowVariables,
  teamVariables,
  systemVariables,
  onLabelChange,
  onConfigChange,
  onInputsChange,
}: {
  nodeId: string;
  manifest: NodeManifestDto;
  label: string;
  config: Record<string, unknown>;
  inputs: Record<string, unknown>;
  liveDefinition: WorkflowDefinition;
  manifestByType: Map<string, NodeManifestDto>;
  workflowVariables: import("@/api/variables").VariableSummary[];
  teamVariables: import("@/api/variables").VariableSummary[];
  systemVariables: import("@/api/workflows").SystemVariableDto[];
  onLabelChange: (v: string) => void;
  onConfigChange: (v: Record<string, unknown>) => void;
  onInputsChange: (v: Record<string, unknown>) => void;
}) {
  // Compute scope at THIS node's position. Re-runs every render — cheap, definition is
  // already in memory. Feeds the autocomplete picker + the "Provides" hint card.
  const suggestions = useMemo(() => introspectScope({
    definition: liveDefinition,
    currentNodeId: nodeId,
    manifestByType,
    workflowVariables,
    teamVariables,
    systemVariables,
  }), [liveDefinition, nodeId, manifestByType, workflowVariables, teamVariables, systemVariables]);

  const ownOutputs = useMemo(() => {
    const schema = manifest.outputSchema as { properties?: Record<string, { type?: string | string[]; description?: string }> } | undefined;
    if (!schema?.properties) return [];
    return Object.entries(schema.properties).map(([name, prop]) => ({
      name,
      type: Array.isArray(prop.type) ? prop.type.join("|") : prop.type,
      description: prop.description,
      refPath: `nodes.${nodeId}.outputs.${name}`,
    }));
  }, [manifest, nodeId]);

  // Outputs section is collapsed by default — operator opens it on demand when wiring
  // a downstream {{ref}}. Most of the time the author is editing Config / Inputs and
  // doesn't need the output list cluttering the panel.
  const [outputsOpen, setOutputsOpen] = useState(false);

  return (
    <div className="wf-inspector-body">
      <header className="wf-inspector-h">
        <div className="wf-inspector-h-id">{nodeId}</div>
        <div className="wf-inspector-h-type">{manifest.displayName}</div>
        <code className="wf-inspector-h-key">{manifest.typeKey}</code>
      </header>

      <section className="wf-inspector-section">
        <label className="wf-form-row">
          <span className="wf-form-label">Label</span>
          <input
            className="wf-form-input"
            value={label}
            onChange={(e) => onLabelChange(e.target.value)}
            placeholder={manifest.displayName}
          />
        </label>
      </section>

      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Config</div>
        <SchemaForm
          schema={manifest.configSchema}
          value={config}
          onChange={onConfigChange}
          variableSuggestions={suggestions}
        />
      </section>

      <section className="wf-inspector-section">
        <div className="wf-inspector-section-h">Inputs</div>
        <SchemaForm
          schema={manifest.inputSchema}
          value={inputs}
          onChange={onInputsChange}
          templateHint
          variableSuggestions={suggestions}
        />
      </section>

      {/* Output section — what THIS node emits for downstream nodes to reference. Header
          acts as a toggle; the body (intro + per-output {{ref}} pill list) renders only
          when the operator opens it. Default-collapsed keeps the inspector quiet for the
          common case of editing Config / Inputs. */}
      {ownOutputs.length > 0 && (
        <section className="wf-inspector-section wf-inspector-section-collapsible" data-open={outputsOpen}>
          <button
            type="button"
            className="wf-inspector-section-toggle"
            onClick={() => setOutputsOpen((v) => !v)}
            aria-expanded={outputsOpen}
          >
            {outputsOpen ? <Ic.ChevronDown size={11} /> : <Ic.ChevronRight size={11} />}
            <span className="wf-inspector-section-h">Output</span>
            <span className="wf-inspector-section-count">{ownOutputs.length}</span>
          </button>

          {outputsOpen && (
            <div className="wf-outputs-hint">
              <div className="wf-outputs-hint-intro">Downstream nodes reference this node's outputs via:</div>
              <ul className="wf-outputs-hint-list">
                {ownOutputs.map((o) => (
                  <li key={o.name} className="wf-outputs-hint-row">
                    <code
                      className="wf-outputs-hint-path"
                      title="Click to copy"
                      onClick={() => navigator.clipboard?.writeText(`{{${o.refPath}}}`)}
                    >{`{{${o.refPath}}}`}</code>
                    {o.type && <span className="wf-outputs-hint-type">{o.type}</span>}
                    {o.description && <span className="wf-outputs-hint-desc">{o.description}</span>}
                  </li>
                ))}
              </ul>
            </div>
          )}
        </section>
      )}
    </div>
  );
}

// ─── React Flow ↔ WorkflowDefinition glue ─────────────────────────────────────

const NODE_TYPES = { wf: WorkflowNode };

function definitionToRfNodes(
  def: WorkflowDefinition,
  manifestByType: Map<string, NodeManifestDto>
): Node<WorkflowNodeData>[] {
  // Auto-layout: vertical stack, 180px row spacing, when position is missing. Workflows
  // are typically <20 nodes and the user immediately fixes positions on first drag, so no
  // real layout engine is needed.
  let fallbackY = 80;
  return def.nodes.map((n) => {
    const manifest = manifestByType.get(n.typeKey);
    const position = n.position ?? { x: 80, y: fallbackY };
    if (!n.position) fallbackY += 180;

    return {
      id: n.id,
      type: "wf",
      position,
      data: {
        nodeId: n.id,
        typeKey: n.typeKey,
        displayName: manifest?.displayName ?? n.typeKey,
        iconKey: manifest?.iconKey ?? null,
        kind: manifest?.kind ?? "Regular",
        category: manifest?.category ?? "",
        label: n.label ?? null,
      },
    };
  });
}

function definitionToRfEdges(def: WorkflowDefinition): Edge[] {
  return def.edges.map((e, idx) => ({
    id: `e${idx}-${e.from}-${e.to}`,
    source: e.from,
    target: e.to,
    type: "smoothstep",
    label: e.condition ?? undefined,
  }));
}

function rfToDefinition(
  nodes: Node<WorkflowNodeData>[],
  edges: Edge[],
  configs: Record<string, Record<string, unknown>>,
  inputs: Record<string, Record<string, unknown>>,
  labels: Record<string, string>,
  workflowInputs: import("@/api/workflows").WorkflowVariable[] = [],
  workflowOutputs: import("@/api/workflows").WorkflowVariable[] = []
): WorkflowDefinition {
  const nodeDefs: NodeDefinition[] = nodes.map((n) => ({
    id: n.id,
    typeKey: n.data.typeKey,
    label: labels[n.id]?.length ? labels[n.id] : null,
    config: configs[n.id] ?? {},
    inputs: inputs[n.id] ?? {},
    position: { x: Math.round(n.position.x), y: Math.round(n.position.y) },
  }));

  const edgeDefs: EdgeDefinition[] = edges.map((e) => ({
    from: e.source,
    to: e.target,
    sourceHandle: e.sourceHandle ?? null,
    targetHandle: e.targetHandle ?? null,
    condition: typeof e.label === "string" && e.label.length > 0 ? e.label : null,
  }));

  return {
    // wf + team scope variables live in the unified `variable` table — their values are
    // not part of the workflow JSON.
    schemaVersion: 1,
    nodes: nodeDefs,
    edges: edgeDefs,
    inputs: workflowInputs,
    outputs: workflowOutputs,
  };
}

function deriveActivations(
  definition: WorkflowDefinition,
  existing: WorkflowDetail["activations"],
  manifestByType: Map<string, NodeManifestDto>,
): WorkflowActivationInput[] {
  // Every Trigger node in the graph maps to a workflow_activation row. Carry over the
  // existing rows' config (matching by typeKey) so an operator can rename an activation
  // node without losing its repo filter, label filter, etc. New activation nodes get an
  // empty config; the operator can edit it via the inspector.
  //
  // Classify nodes via manifest Kind, not by a hardcoded "trigger." prefix — the manifest's
  // declared Kind is the single source of truth and covers plugin authors who name their
  // trigger "schedule.cron" or "slack.mention".
  const triggerNodes = definition.nodes.filter((n) => manifestByType.get(n.typeKey)?.kind === "Trigger");
  return triggerNodes.map((n) => {
    const existingMatch = existing.find((a) => a.typeKey === n.typeKey);
    return {
      typeKey: n.typeKey,
      enabled: existingMatch?.enabled ?? true,
      config: existingMatch?.config ?? {},
    };
  });
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function initialBag(def: WorkflowDefinition, key: "config" | "inputs"): Record<string, Record<string, unknown>> {
  const out: Record<string, Record<string, unknown>> = {};
  for (const n of def.nodes) {
    const v = key === "config" ? n.config : n.inputs;
    out[n.id] = (typeof v === "object" && v !== null) ? v as Record<string, unknown> : {};
  }
  return out;
}

function uniqueNodeId(typeKey: string, existing: Node[]): string {
  // Generate "<segment>" or "<segment>_2" etc. Segment = the last dot-separated piece
  // of typeKey (e.g. trigger.pr.opened → opened), lowercased + de-collided.
  const base = typeKey.split(".").pop() ?? "node";
  const used = new Set(existing.map((n) => n.id));
  if (!used.has(base)) return base;
  let i = 2;
  while (used.has(`${base}_${i}`)) i++;
  return `${base}_${i}`;
}

function defaultsFromSchema(schema: unknown): Record<string, unknown> {
  if (typeof schema !== "object" || schema == null) return {};
  const s = schema as { properties?: Record<string, { default?: unknown }> };
  const out: Record<string, unknown> = {};
  if (!s.properties) return out;
  for (const [k, v] of Object.entries(s.properties)) {
    if (v && "default" in v && v.default !== undefined) out[k] = v.default;
  }
  return out;
}

/**
 * Would adding the edge (source → target) create a cycle? Cycle iff target can already
 * reach source via existing edges. Simple BFS forward from target — terminates because the
 * existing graph is a DAG (every previous addition went through the same check).
 */
function createsCycle(source: string, target: string, edges: Edge[]): boolean {
  const adjacency = new Map<string, string[]>();
  for (const e of edges) {
    const list = adjacency.get(e.source);
    if (list) list.push(e.target); else adjacency.set(e.source, [e.target]);
  }

  const visited = new Set<string>();
  const stack: string[] = [target];

  while (stack.length > 0) {
    const current = stack.pop()!;
    if (current === source) return true;
    if (visited.has(current)) continue;
    visited.add(current);

    const next = adjacency.get(current);
    if (next) for (const n of next) stack.push(n);
  }

  return false;
}
