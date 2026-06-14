using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Write-time gate for every workflow definition. Validates the graph BEFORE the row hits
/// the DB so a malformed workflow can't be saved or run. Catches:
///
///   - unsupported schemaVersion
///   - node type_key not registered (typo, plugin removed)
///   - duplicate node ids
///   - edge endpoints don't exist
///   - exactly one trigger node + at least one terminal
///   - cycles (DAG only)
///   - unreachable nodes (everything must be reachable from trigger)
///   - terminal-reachable: at least one terminal reachable from trigger
///
/// Errors come back as a list of human-readable strings so the UI can render every problem
/// at once instead of one-at-a-time fix-and-retry.
/// </summary>
public sealed class DefinitionValidator : IScopedDependency
{
    private readonly INodeRegistry _nodeRegistry;

    public DefinitionValidator(INodeRegistry nodeRegistry)
    {
        _nodeRegistry = nodeRegistry;
    }

    public ValidationResult Validate(WorkflowDefinition definition)
    {
        var errors = new List<string>();

        CheckSchemaVersion(definition, errors);
        CheckNodeIdsAndTypes(definition, errors);
        CheckEdgeEndpoints(definition, errors);
        CheckEdgeSourceHandles(definition, errors);
        CheckTriggerAndTerminalCounts(definition, errors);
        CheckAcyclic(definition, errors);
        CheckReachability(definition, errors);
        CheckReferencePaths(definition, errors);
        CheckRetryPolicies(definition, errors);
        CheckContainerStructure(definition, errors);

        return new ValidationResult(errors);
    }

    private static void CheckSchemaVersion(WorkflowDefinition definition, List<string> errors)
    {
        if (definition.SchemaVersion != WorkflowDefinition.CurrentSchemaVersion)
            errors.Add($"Unsupported schemaVersion {definition.SchemaVersion}. Expected {WorkflowDefinition.CurrentSchemaVersion}.");
    }

    private void CheckNodeIdsAndTypes(WorkflowDefinition definition, List<string> errors)
    {
        var seen = new HashSet<string>();

        foreach (var node in definition.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                errors.Add("Node with blank Id.");
                continue;
            }

            if (!seen.Add(node.Id))
                errors.Add($"Duplicate node id '{node.Id}'.");

            if (!_nodeRegistry.Contains(node.TypeKey))
                errors.Add($"Node '{node.Id}' references unregistered TypeKey '{node.TypeKey}'.");
        }
    }

    private static void CheckEdgeEndpoints(WorkflowDefinition definition, List<string> errors)
    {
        var ids = definition.Nodes.Select(n => n.Id).ToHashSet();

        foreach (var edge in definition.Edges)
        {
            if (!ids.Contains(edge.From))
                errors.Add($"Edge.From '{edge.From}' references unknown node.");

            if (!ids.Contains(edge.To))
                errors.Add($"Edge.To '{edge.To}' references unknown node.");
        }
    }

    private void CheckTriggerAndTerminalCounts(WorkflowDefinition definition, List<string> errors)
    {
        var triggers = definition.Nodes.Where(n => SafeKind(n) == NodeKind.Trigger).ToList();
        var terminals = definition.Nodes.Where(n => SafeKind(n) == NodeKind.Terminal).ToList();

        if (triggers.Count == 0) errors.Add("Definition has no Trigger node. Exactly one is required.");
        if (triggers.Count > 1) errors.Add($"Definition has {triggers.Count} Trigger nodes. Exactly one is required.");
        if (terminals.Count == 0) errors.Add("Definition has no Terminal node. At least one is required.");
    }

    private NodeKind? SafeKind(NodeDefinition node)
    {
        if (!_nodeRegistry.Contains(node.TypeKey)) return null;

        return _nodeRegistry.Resolve(node.TypeKey).Manifest.Kind;
    }

    private static void CheckAcyclic(WorkflowDefinition definition, List<string> errors)
    {
        // Standard 3-colour DFS. White = unvisited, Grey = on stack, Black = done.
        // Skip if duplicate ids — CheckNodeIdsAndTypes already reported that error and a
        // dictionary keyed by node.Id would crash on the duplicate.
        if (HasDuplicateIds(definition)) return;

        var colour = definition.Nodes.ToDictionary(n => n.Id, _ => 0);
        var adjacency = BuildAdjacency(definition);

        foreach (var node in definition.Nodes)
        {
            if (colour[node.Id] == 0 && HasCycleDfs(node.Id, adjacency, colour))
            {
                errors.Add("Definition contains a cycle. Workflows must be DAGs.");
                return;
            }
        }
    }

    private static bool HasCycleDfs(string nodeId, Dictionary<string, List<string>> adjacency, Dictionary<string, int> colour)
    {
        colour[nodeId] = 1;

        foreach (var next in adjacency.GetValueOrDefault(nodeId) ?? new List<string>())
        {
            if (!colour.ContainsKey(next)) continue;
            if (colour[next] == 1) return true;
            if (colour[next] == 0 && HasCycleDfs(next, adjacency, colour)) return true;
        }

        colour[nodeId] = 2;
        return false;
    }

    private void CheckReachability(WorkflowDefinition definition, List<string> errors)
    {
        var trigger = definition.Nodes.FirstOrDefault(n => SafeKind(n) == NodeKind.Trigger);

        if (trigger == null) return;   // already flagged by CheckTriggerAndTerminalCounts

        // Skip if duplicate ids — BuildAdjacency would crash on its ToDictionary call.
        if (HasDuplicateIds(definition)) return;

        var adjacency = BuildAdjacency(definition);
        var nodeById = definition.Nodes.ToDictionary(n => n.Id);   // safe: duplicate ids returned above
        var reachable = new HashSet<string>();
        var stack = new Stack<string>();

        stack.Push(trigger.Id);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (!reachable.Add(current)) continue;

            foreach (var next in adjacency.GetValueOrDefault(current) ?? new List<string>()) stack.Push(next);

            // A reachable container (loop / try) makes its whole body reachable — enter it so the
            // body-start + body nodes (which have no top-level incoming edge) aren't flagged
            // unreachable. The body's own internal connectivity is covered by CheckAcyclic. TryGetValue
            // guards the case where `current` is an edge target that doesn't exist (CheckEdgeEndpoints flags it).
            if (nodeById.TryGetValue(current, out var currentNode) && SafeKind(currentNode) is NodeKind.Loop or NodeKind.Try or NodeKind.Map)
                foreach (var bodyNode in definition.Nodes.Where(n => n.ParentId == current)) stack.Push(bodyNode.Id);
        }

        foreach (var node in definition.Nodes)
        {
            if (!reachable.Contains(node.Id))
                errors.Add($"Node '{node.Id}' is not reachable from the Trigger.");
        }

        var terminalsReached = definition.Nodes.Any(n => SafeKind(n) == NodeKind.Terminal && reachable.Contains(n.Id));

        if (!terminalsReached)
            errors.Add("No Terminal node is reachable from the Trigger.");
    }

    private static Dictionary<string, List<string>> BuildAdjacency(WorkflowDefinition definition)
    {
        var adjacency = definition.Nodes.ToDictionary(n => n.Id, _ => new List<string>());

        foreach (var edge in definition.Edges)
        {
            if (adjacency.TryGetValue(edge.From, out var list)) list.Add(edge.To);
        }

        return adjacency;
    }

    private static bool HasDuplicateIds(WorkflowDefinition definition) =>
        definition.Nodes.Select(n => n.Id).Distinct().Count() != definition.Nodes.Count;

    /// <summary>
    /// Each edge's SourceHandle must match an output handle declared by the source node's
    /// manifest. Default null = the implicit "out" handle (single-output nodes). Catches
    /// the typo "true" → "ture" before the run silently follows zero edges.
    /// </summary>
    private void CheckEdgeSourceHandles(WorkflowDefinition definition, List<string> errors)
    {
        if (HasDuplicateIds(definition)) return;

        var nodeById = definition.Nodes.ToDictionary(n => n.Id);

        foreach (var edge in definition.Edges)
        {
            if (edge.SourceHandle == null) continue;
            // `error` is the universal failure handle every node implicitly exposes — always valid,
            // regardless of the node's declared manifest outputs.
            if (edge.SourceHandle == WorkflowHandles.Error) continue;
            if (!nodeById.TryGetValue(edge.From, out var sourceNode)) continue;
            if (!_nodeRegistry.Contains(sourceNode.TypeKey)) continue;

            var manifest = _nodeRegistry.Resolve(sourceNode.TypeKey).Manifest;
            var declared = manifest.Outputs;

            // A node with no declared Outputs (single default handle) shouldn't have edges
            // tagged with a non-null SourceHandle — that's a config-time bug.
            if (declared == null)
            {
                errors.Add($"Edge from '{edge.From}' specifies SourceHandle '{edge.SourceHandle}', but node type '{sourceNode.TypeKey}' has only a default output.");
                continue;
            }

            if (declared.All(h => h.Name != edge.SourceHandle))
            {
                var available = string.Join(", ", declared.Select(h => $"'{h.Name}'"));
                errors.Add($"Edge from '{edge.From}' uses unknown SourceHandle '{edge.SourceHandle}'. Available: {available}.");
            }
        }
    }

    /// <summary>
    /// Walks every Config and Inputs value looking for {{ref}} templates and {"$ref": "..."}
    /// objects. For each reference, verifies the head scope (trigger/nodes/env) and, for
    /// nodes.X.outputs.Y, that node X exists, is upstream of the current node, and (when
    /// node X declares an OutputSchema) that Y is one of its declared output properties.
    /// Catches typos before runtime — operator sees the error at Save, not at first Run.
    /// </summary>
    private void CheckReferencePaths(WorkflowDefinition definition, List<string> errors)
    {
        if (HasDuplicateIds(definition)) return;

        var nodeById = definition.Nodes.ToDictionary(n => n.Id);
        var reachableUpstream = BuildUpstreamReachability(definition);

        foreach (var node in definition.Nodes)
        {
            ExtractRefPaths(node.Config).ToList().ForEach(p => CheckOneRef(node, p, "config", nodeById, reachableUpstream, errors));
            ExtractRefPaths(node.Inputs).ToList().ForEach(p => CheckOneRef(node, p, "inputs", nodeById, reachableUpstream, errors));
        }
    }

    private void CheckOneRef(NodeDefinition node, string path, string source, IReadOnlyDictionary<string, NodeDefinition> nodeById, IReadOnlyDictionary<string, ISet<string>> reachableUpstream, List<string> errors)
    {
        var segments = path.Split('.');
        if (segments.Length == 0) return;

        var head = segments[0];

        // Opaque scope heads — accepted without further validation. trigger.* / team.* / sys.*
        // are dynamic (webhook payload / per-team variable storage / engine-injected context).
        // wf.* and input.* are accepted without strict membership check below to give clearer
        // errors than "unknown head".
        if (head is "trigger" or "team" or "sys") return;

        if (head is "wf" or "input")
        {
            if (segments.Length < 2) return;   // bare {{wf}} / {{input}} — unusual but not invalid
            // We can't see the WorkflowDefinition.Variables list from here without plumbing
            // it through. Leave dynamic membership to runtime (null-resolution handles typos
            // gracefully).
            return;
        }

        // project.<slug>.<name> — Phase 3.0 project-scoped variables. Validate shape only
        // (need 3+ segments: head, slug, then at least one name component). Slug existence
        // is NOT verified here: a DB lookup at save time would cost an extra query per
        // workflow save AND would race with concurrent project deletes. Runtime gracefully
        // resolves missing slugs to null via WalkProjectsScope.
        if (head == "project")
        {
            if (segments.Length < 3)
                errors.Add($"Node '{node.Id}' {source} reference '{path}' is malformed (project reference must be 'project.<slug>.<name>').");
            return;
        }

        // loop.<name> — flow.loop body variables (+ loop.index), populated by the enclosing container
        // at runtime. Membership is dynamic (depends on which loop body this node sits in), so accept
        // the shape and let runtime null-resolve a stray ref — mirrors trigger / team / wf / input.
        if (head == "loop") return;

        // Iteration scope keys (item / index / etc) are populated at runtime by container
        // nodes like flow.iterate and are only valid inside that container's config/inputs.
        // We can't determine "is this node inside an iterate?" without a parent-pointer in
        // the definition; for now we accept these heads when the head is one of the
        // well-known iteration variable names — better than false-flagging valid templates.
        // Runtime null-resolution catches mis-used refs.
        if (IsImplicitIterationHead(head)) return;

        if (head != "nodes")
        {
            errors.Add($"Node '{node.Id}' {source} references unknown scope head '{head}' (must be one of: trigger, nodes, wf, input, team, sys, project, or an iteration variable like 'item' / 'index').");
            return;
        }

        if (segments.Length < 4 || segments[2] != "outputs")
        {
            errors.Add($"Node '{node.Id}' {source} reference '{path}' is malformed (expected nodes.<id>.outputs.<key>).");
            return;
        }

        var refNodeId = segments[1];

        // The output-key segment may carry array indices (files[2]) — validate the declared key, not
        // the accessor. A trailing '.length' lands in a later segment, so segments[3] is already clean
        // for it (e.g. nodes.x.outputs.subtasks.length → segments[3]=='subtasks').
        var outputKey = StripIndices(segments[3]);

        if (!nodeById.TryGetValue(refNodeId, out var refNode))
        {
            errors.Add($"Node '{node.Id}' {source} references unknown upstream node '{refNodeId}'.");
            return;
        }

        if (!reachableUpstream[node.Id].Contains(refNodeId))
        {
            errors.Add($"Node '{node.Id}' {source} references '{refNodeId}', which is not upstream of it. Add an edge or restructure.");
            return;
        }

        if (!_nodeRegistry.Contains(refNode.TypeKey)) return;

        // `error` is the universal failure output — any node can emit it on a handled failure, so
        // it's always a valid reference regardless of the manifest's declared outputs.
        if (outputKey == WorkflowHandles.Error) return;

        // Output-key existence check: only when the upstream's manifest declares a typed
        // OutputSchema with explicit properties. For nodes that emit dynamic-shape output
        // (e.g. http.request.body is whatever the API returns) we'd false-positive — so
        // we only check when the schema is properly typed.
        var manifest = _nodeRegistry.Resolve(refNode.TypeKey).Manifest;
        if (!TryGetDeclaredOutputKeys(manifest, out var declared)) return;
        if (declared.Count == 0) return;

        if (!declared.Contains(outputKey))
        {
            var available = string.Join(", ", declared.Select(k => $"'{k}'"));
            errors.Add($"Node '{node.Id}' {source} references '{path}', but '{refNodeId}' ({refNode.TypeKey}) doesn't declare output '{outputKey}'. Available: {available}.");
            return;
        }

        // Wait-only output: a card's action/by/comment/values are EMPTY at run time unless the producing node
        // actually waits for a response. Generic — driven by the manifest's WaitOutputsSpec, no node-type
        // knowledge — so any node with config-gated wait outputs is covered. (This catches the classic footgun:
        // wiring git.pr_review.verdict = nodes.post_message.outputs.action while post_message isn't waiting.)
        if (manifest.WaitOutputs is { } waitSpec && waitSpec.OutputKeys.Contains(outputKey) && !NodeWaits(refNode, waitSpec))
            errors.Add($"Node '{node.Id}' {source} references '{path}', but '{refNodeId}' isn't waiting for a response — '{outputKey}' will be empty at run time. Turn on '{waitSpec.WaitConfigLabel}' on '{refNodeId}' (or wire it to a wait), or remove the reference.");
    }

    /// <summary>
    /// Does this node instance wait, per its <see cref="WaitOutputsSpec"/>? Reads the boolean wait-config key
    /// from the node's Config; an absent key falls back to the spec's declared schema default.
    /// </summary>
    private static bool NodeWaits(NodeDefinition node, WaitOutputsSpec spec)
    {
        if (node.Config.ValueKind == System.Text.Json.JsonValueKind.Object && node.Config.TryGetProperty(spec.WaitConfigKey, out var value))
        {
            if (value.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (value.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        }

        return spec.WaitConfigDefault;
    }

    /// <summary>
    /// Validate each node's optional retry policy. The engine clamps out-of-range values at run
    /// time (<see cref="RetryPlan.From"/>), but a save-time error is clearer than silent clamping:
    /// the operator sees "maxAttempts must be 1..10" immediately instead of wondering why their
    /// 50 became 10. Caps come from <see cref="RetryPlan"/> so the two never drift.
    /// </summary>
    private static void CheckRetryPolicies(WorkflowDefinition definition, List<string> errors)
    {
        foreach (var node in definition.Nodes)
        {
            var retry = node.Retry;
            if (retry == null) continue;

            if (retry.MaxAttempts < 1 || retry.MaxAttempts > RetryPlan.MaxAttemptsCap)
                errors.Add($"Node '{node.Id}' retry.maxAttempts must be between 1 and {RetryPlan.MaxAttemptsCap} (got {retry.MaxAttempts}).");

            if (retry.BackoffSeconds < 0 || retry.BackoffSeconds > RetryPlan.MaxBackoffSeconds)
                errors.Add($"Node '{node.Id}' retry.backoffSeconds must be between 0 and {RetryPlan.MaxBackoffSeconds} (got {retry.BackoffSeconds}).");
        }
    }

    /// <summary>
    /// Structural rules for engine-driven container nodes (Loop / Try / Map). Two checks apply to EVERY
    /// container — no edge may cross the container boundary (the engine's <c>SubgraphView</c> silently
    /// DROPS such an edge today, so a crossing wire would vanish at run time), and a container may not be
    /// nested deeper than the engine's runaway cap. A <c>flow.map</c> additionally must have a non-empty
    /// body rooted at exactly one <c>flow.map_start</c> and ending in exactly one terminal node (the
    /// per-element result source the reduce reads). Skipped on duplicate ids (the ToDictionary would crash;
    /// that error is already reported).
    /// </summary>
    private void CheckContainerStructure(WorkflowDefinition definition, List<string> errors)
    {
        if (HasDuplicateIds(definition)) return;

        var ownerByNodeId = definition.Nodes.ToDictionary(n => n.Id, n => n.ParentId);

        CheckNoEdgeCrossesContainerBoundary(definition, ownerByNodeId, errors);

        foreach (var node in definition.Nodes)
        {
            var kind = SafeKind(node);
            if (kind is not (NodeKind.Loop or NodeKind.Try or NodeKind.Map)) continue;

            CheckContainerNestingDepth(node, ownerByNodeId, errors);

            if (kind == NodeKind.Map) CheckMapBodyShape(definition, node, errors);
        }
    }

    /// <summary>
    /// Every edge must connect two nodes with the SAME container owner (both top-level, or both in the
    /// same container body). A crossing edge — a body node wired to an outside node, or vice-versa — is
    /// silently dropped by <c>SubgraphView</c> (which keeps only edges wholly inside a subgraph), so the
    /// author's intended connection would never fire. Surface it at save time instead of at a confusing run.
    /// </summary>
    private static void CheckNoEdgeCrossesContainerBoundary(WorkflowDefinition definition, IReadOnlyDictionary<string, string?> ownerByNodeId, List<string> errors)
    {
        foreach (var edge in definition.Edges)
        {
            if (!ownerByNodeId.TryGetValue(edge.From, out var fromOwner)) continue;   // unknown endpoint — CheckEdgeEndpoints flags it
            if (!ownerByNodeId.TryGetValue(edge.To, out var toOwner)) continue;

            if (fromOwner != toOwner)
                errors.Add($"Edge '{edge.From}' → '{edge.To}' crosses a container boundary. A container body connects to the rest of the graph only through the container node itself, not through its body nodes.");
        }
    }

    /// <summary>A container may not be nested deeper than <see cref="MapPlan.MaxNestingDepth"/> containers — the save-time mirror of the engine's run-time nesting guard. Depth = the number of container ancestors via the ParentId chain.</summary>
    private void CheckContainerNestingDepth(NodeDefinition node, IReadOnlyDictionary<string, string?> ownerByNodeId, List<string> errors)
    {
        // depth = the number of container ancestors via the ParentId chain. The engine refuses a container
        // whose incoming iteration key already has MaxNestingDepth segments (one per enclosing container),
        // so a container with >= MaxNestingDepth ancestors is over the limit — mirror that exactly here.
        var depth = 0;
        var ancestor = node.ParentId;
        while (ancestor != null && ownerByNodeId.TryGetValue(ancestor, out var grandparent))
        {
            depth++;
            if (depth >= MapPlan.MaxNestingDepth)
            {
                errors.Add($"Container '{node.Id}' is nested deeper than the {MapPlan.MaxNestingDepth}-level limit.");
                return;
            }
            ancestor = grandparent;
        }
    }

    /// <summary>
    /// A <c>flow.map</c> body (nodes whose <c>ParentId</c> is the map) must be non-empty, rooted at exactly
    /// one <c>flow.map_start</c>, and end in exactly one TERMINAL node — the single body node with no
    /// in-body outgoing edge, whose output becomes each element's result. The single-terminal rule is what
    /// lets the reduce pick a per-element result unambiguously (PR1 design lock).
    ///
    /// <para>PR1 fail-closed guard: NO body node may SUSPEND (park the run on a durable wait). PR1 runs map
    /// branches SYNCHRONOUSLY — durable parallel-branch resume ships in PR2 — so a body suspend would commit
    /// a wait row + schedule/stage an external job behind a map the engine cannot resume. Rejecting it at save
    /// time keeps the runtime suspend branch in <c>AdvanceMapBranchNodeOrAbandon</c> genuinely unreachable.
    /// The check spans the ENTIRE recursive body (nested loop / try / map bodies extend it) and is generic —
    /// driven by the manifest's <see cref="NodeManifest.CanSuspend"/> marker, no node-type knowledge.</para>
    /// </summary>
    private void CheckMapBodyShape(WorkflowDefinition definition, NodeDefinition mapNode, List<string> errors)
    {
        var body = definition.Nodes.Where(n => n.ParentId == mapNode.Id).ToList();

        if (body.Count == 0)
        {
            errors.Add($"Map '{mapNode.Id}' has an empty body. Add a flow.map_start and at least one body node.");
            return;
        }

        var starts = body.Count(n => n.TypeKey == "flow.map_start");
        if (starts != 1)
            errors.Add($"Map '{mapNode.Id}' body must have exactly one flow.map_start (found {starts}).");

        var bodyIds = body.Select(n => n.Id).ToHashSet();
        var withOutgoing = definition.Edges.Where(e => bodyIds.Contains(e.From)).Select(e => e.From).ToHashSet();
        var terminals = body.Where(n => !withOutgoing.Contains(n.Id)).ToList();

        if (terminals.Count != 1)
            errors.Add($"Map '{mapNode.Id}' body must end in exactly one terminal node (the per-element result); found {terminals.Count}.");

        CheckNoSuspendingNodeInMapBody(definition, mapNode, errors);
    }

    /// <summary>
    /// Reject any node in the map's recursive body (nested loop / try / map bodies included) whose manifest
    /// declares <see cref="NodeManifest.CanSuspend"/> — PR1 has no durable parallel-branch resume, so a body
    /// suspend is unsupported and must fail at save time, not leak a parked-then-failed wait at run time.
    /// </summary>
    private void CheckNoSuspendingNodeInMapBody(WorkflowDefinition definition, NodeDefinition mapNode, List<string> errors)
    {
        foreach (var descendant in DescendantsOf(definition, mapNode.Id))
        {
            if (!_nodeRegistry.Contains(descendant.TypeKey)) continue;   // unknown type — CheckNodeIdsAndTypes flags it

            if (_nodeRegistry.Resolve(descendant.TypeKey).Manifest.CanSuspend)
                errors.Add($"Map '{mapNode.Id}' body cannot contain a node that waits ('{descendant.Id}' is {descendant.TypeKey}) — durable parallel-branch resume ships in PR2; remove it or move it outside the map.");
        }
    }

    /// <summary>Every node transitively under <paramref name="containerId"/> via the <c>ParentId</c> chain — the container's full recursive body, nested containers and their bodies included.</summary>
    private static IEnumerable<NodeDefinition> DescendantsOf(WorkflowDefinition definition, string containerId)
    {
        var childrenByParent = definition.Nodes.Where(n => n.ParentId != null).ToLookup(n => n.ParentId!);

        var stack = new Stack<string>();
        stack.Push(containerId);
        while (stack.Count > 0)
        {
            foreach (var child in childrenByParent[stack.Pop()])
            {
                yield return child;
                stack.Push(child.Id);
            }
        }
    }

    private static IEnumerable<string> ExtractRefPaths(System.Text.Json.JsonElement element)
    {
        var paths = new List<string>();
        Walk(element);
        return paths;

        void Walk(System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    if (TryGetSingleRefPath(e, out var refPath)) { paths.Add(refPath); return; }
                    foreach (var prop in e.EnumerateObject()) Walk(prop.Value);
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray()) Walk(item);
                    break;
                case System.Text.Json.JsonValueKind.String:
                    foreach (System.Text.RegularExpressions.Match m in TemplatePattern.Matches(e.GetString() ?? ""))
                        paths.Add(m.Groups[1].Value);
                    break;
            }
        }
    }

    private static bool TryGetSingleRefPath(System.Text.Json.JsonElement element, out string path)
    {
        path = "";
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        var props = element.EnumerateObject().ToList();
        if (props.Count != 1 || props[0].Name != "$ref") return false;
        if (props[0].Value.ValueKind != System.Text.Json.JsonValueKind.String) return false;
        path = props[0].Value.GetString() ?? "";
        return path.Length > 0;
    }

    // Single source of truth — reuse the resolver's inline-template grammar so save-time validation and
    // run-time resolution can never recognize a different set of {{refs}} (including array index / .length).
    private static readonly System.Text.RegularExpressions.Regex TemplatePattern = VariableResolver.TemplatePattern;

    /// <summary>Drops a segment's array-index suffix (<c>files[2]</c> → <c>files</c>) so the declared-output check sees the key, not the accessor.</summary>
    private static string StripIndices(string segment)
    {
        var bracket = segment.IndexOf('[');
        return bracket < 0 ? segment : segment[..bracket];
    }

    private static IReadOnlyDictionary<string, ISet<string>> BuildUpstreamReachability(WorkflowDefinition definition)
    {
        // For each node, the set of nodes that can reach it via directed edges.
        var result = new Dictionary<string, ISet<string>>();
        foreach (var node in definition.Nodes) result[node.Id] = new HashSet<string>();

        // Simple BFS from each node backwards along incoming edges.
        foreach (var node in definition.Nodes)
        {
            var visited = result[node.Id];
            var stack = new Stack<string>();
            foreach (var inbound in definition.Edges.Where(e => e.To == node.Id)) stack.Push(inbound.From);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!visited.Add(current)) continue;
                foreach (var inbound in definition.Edges.Where(e => e.To == current)) stack.Push(inbound.From);
            }
        }

        return result;
    }

    /// <summary>
    /// Heads that are populated by iteration-container nodes at runtime and are not bound to
    /// the static scope buckets (trigger/nodes/env). Accepted at validate time everywhere —
    /// running a workflow that uses {{item}} outside an iterate container resolves to null,
    /// which the receiving node either handles or fails at runtime. Better than blocking
    /// valid templates at save time.
    /// </summary>
    private static bool IsImplicitIterationHead(string head) =>
        head is "item" or "index" or "iteration";

    private static bool TryGetDeclaredOutputKeys(NodeManifest manifest, out HashSet<string> keys)
    {
        keys = new HashSet<string>();
        if (manifest.OutputSchema.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        if (!manifest.OutputSchema.TryGetProperty("properties", out var props)) return false;
        if (props.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

        foreach (var prop in props.EnumerateObject()) keys.Add(prop.Name);
        return true;
    }
}

public sealed record ValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
