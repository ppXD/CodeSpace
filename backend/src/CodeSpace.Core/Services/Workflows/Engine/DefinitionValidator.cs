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
///   - container body fully reachable from its *_start marker (map / loop / try)
///   - map resultKey + loop var names: not reserved, valid identifiers
///   - map items binding present (no silent empty-fan-out no-op)
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

            // A reachable container (loop / try / map) makes its whole body reachable — enter it so the
            // body-start + body nodes (which have no top-level incoming edge) aren't flagged unreachable.
            // This is TOP-LEVEL reachability only: it says "the body can be entered", NOT "every body node
            // is reachable from the body's start marker". The latter is CheckBodyReachableFromStart's job
            // (a disconnected body root is acyclic AND top-level-reachable here, yet the engine would run it
            // once per element — that's the gap that check closes). TryGetValue guards the case where
            // `current` is an edge target that doesn't exist (CheckEdgeEndpoints flags it).
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

        // A CONTAINER node's own config/inputs may reference its OWN body nodes — a body node's output is
        // resolved against the just-finished body scope at run time, not the top-level edge graph. This is
        // the loop's documented contract (a loopVariable's `update` reads {{nodes.<bodyNode>.outputs.X}} at
        // pass-end — WorkflowEngine.ApplyLoopVarUpdates), and the symmetric case for a map/try reading a body
        // result. Such a body node is NOT top-level-upstream of the container, so skip the upstream edge
        // check for it — the output-key existence check below still applies. (refNode.ParentId == node.Id =>
        // refNode is a direct child of this container's body.)
        var refIsOwnBodyNode = refNode.ParentId == node.Id && SafeKind(node) is NodeKind.Loop or NodeKind.Try or NodeKind.Map;

        if (!refIsOwnBodyNode && !reachableUpstream[node.Id].Contains(refNodeId))
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

            CheckBodyStartCount(definition, node, kind.Value, errors);

            CheckBodyReachableFromStart(definition, node, kind.Value, errors);

            if (kind == NodeKind.Map)
            {
                CheckMapBodyShape(definition, node, errors);
                CheckMapResultKey(node, errors);
                CheckMapItemsBinding(node, errors);
            }

            if (kind == NodeKind.Loop) CheckLoopVariableNames(node, errors);
        }
    }

    /// <summary>The body-entry marker type each container's body subgraph is rooted at — the source-only node the engine's body walk starts from.</summary>
    private static string BodyStartTypeKey(NodeKind kind) => kind switch
    {
        NodeKind.Map => "flow.map_start",
        NodeKind.Loop => "flow.loop_start",
        NodeKind.Try => "flow.try_start",
        _ => "",
    };

    /// <summary>
    /// A non-empty container body must be rooted at EXACTLY ONE <c>*_start</c> marker — for ALL THREE kinds
    /// (map / loop / try). With zero or two starts the engine's frontier walk
    /// (<c>EnqueueReadyFrontier</c> over the <c>SubgraphView</c>) seeds EVERY zero-incoming body node as a
    /// root and runs that whole component once per element/iteration — silent fan-out amplification. Map's
    /// extra body-shape rules live in <c>CheckMapBodyShape</c>; this single check owns the start-count rule
    /// for every container so loop/try are no longer a false pass (the gap this closes), and
    /// <c>CheckBodyReachableFromStart</c> can safely defer the wrong-count case to it.
    /// </summary>
    private static void CheckBodyStartCount(WorkflowDefinition definition, NodeDefinition container, NodeKind kind, List<string> errors)
    {
        var startTypeKey = BodyStartTypeKey(kind);

        var body = definition.Nodes.Where(n => n.ParentId == container.Id).ToList();
        if (body.Count == 0) return;   // empty body is the container's own concern (map reports it; loop/try tolerate it as a no-op walk)

        var starts = body.Count(n => n.TypeKey == startTypeKey);
        if (starts != 1)
            errors.Add($"Container '{container.Id}' body must have exactly one {startTypeKey} (found {starts}).");
    }

    /// <summary>
    /// Every node in a container body must be reachable from the body's single <c>*_start</c> marker by walking
    /// body-internal edges — the SAME traversal the engine uses to run the body. A body node disconnected from the
    /// start has no incoming body edge, so the engine's frontier walk (<c>EnqueueReadyFrontier</c> over the
    /// <c>SubgraphView</c>) treats it as a ROOT and runs it once per element/iteration — silent fan-out amplification.
    /// This rejects it at save time. Generic across map / loop / try; <c>CheckAcyclic</c> (DAG) does NOT cover this
    /// (a disconnected island is acyclic), nor does top-level <c>CheckReachability</c> (it only enters the body, it
    /// doesn't walk it).
    ///
    /// <para>Engine-fidelity: body-internal edges are exactly the edges whose BOTH endpoints are this container's
    /// direct body nodes (<c>ParentId == container.Id</c>) — mirroring <c>SubgraphView</c>, which keeps only edges
    /// wholly inside the subgraph. SourceHandle is irrelevant to the walk (an in-body <c>error</c> edge is a normal
    /// body edge the engine traverses; a try's <c>catch</c> edge is sourced from the try NODE at the parent level, so
    /// it is never a body-internal edge). A nested container's OWN children (<c>ParentId == nestedId</c>) are NOT this
    /// body's nodes — they're validated by the nested container's own pass — so the walk reaches the nested container
    /// node and stops, exactly as the engine does.</para>
    /// </summary>
    private static void CheckBodyReachableFromStart(WorkflowDefinition definition, NodeDefinition container, NodeKind kind, List<string> errors)
    {
        var startTypeKey = BodyStartTypeKey(kind);

        var body = definition.Nodes.Where(n => n.ParentId == container.Id).ToList();
        if (body.Count == 0) return;   // empty-body / missing-start is reported by the container's own shape check

        var bodyIds = body.Select(n => n.Id).ToHashSet();
        var starts = body.Where(n => n.TypeKey == startTypeKey).Select(n => n.Id).ToList();
        if (starts.Count != 1) return;   // not exactly one start — CheckBodyStartCount reports that (for all three kinds); reachability can't anchor

        var bodyAdjacency = BuildBodyAdjacency(definition, bodyIds);
        var reachable = new HashSet<string>();
        var stack = new Stack<string>();
        stack.Push(starts[0]);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!reachable.Add(current)) continue;
            foreach (var next in bodyAdjacency.GetValueOrDefault(current) ?? new List<string>()) stack.Push(next);
        }

        foreach (var node in body)
            if (!reachable.Contains(node.Id))
                errors.Add($"Container '{container.Id}' body node '{node.Id}' is not reachable from {startTypeKey}. Connect it (directly or transitively) to the start marker, or remove it — otherwise the engine runs it once per element/iteration.");
    }

    /// <summary>Forward adjacency restricted to body-internal edges (both endpoints in <paramref name="bodyIds"/>) — the exact edge set <c>SubgraphView</c> keeps for the container body.</summary>
    private static Dictionary<string, List<string>> BuildBodyAdjacency(WorkflowDefinition definition, HashSet<string> bodyIds)
    {
        var adjacency = bodyIds.ToDictionary(id => id, _ => new List<string>());

        foreach (var edge in definition.Edges)
            if (bodyIds.Contains(edge.From) && bodyIds.Contains(edge.To)) adjacency[edge.From].Add(edge.To);

        return adjacency;
    }

    /// <summary>
    /// A <c>flow.map</c>'s <c>resultKey</c> (the output key the reduced array lands under) must be a usable
    /// <c>{{nodes.&lt;id&gt;.outputs.&lt;key&gt;}}</c> reference AND must not collide with a key the reducer
    /// emits. A collision (<c>count</c> / <c>failed</c>) is silent overwrite — the reducer writes the array
    /// under <c>resultKey</c> first, then unconditionally sets <c>count</c>/<c>failed</c>, so the author's
    /// array is destroyed. A non-identifier key can't be referenced downstream. The reserved set is
    /// <see cref="WorkflowOutputKeys.Map"/> (Rule 8 contract-pin — shared with the engine reducer). A blank
    /// key is fine: the engine defaults it to <c>"results"</c>.
    /// </summary>
    private static void CheckMapResultKey(NodeDefinition mapNode, List<string> errors)
    {
        var resultKey = ReadConfigString(mapNode.Config, "resultKey");
        if (string.IsNullOrWhiteSpace(resultKey)) return;   // blank ⇒ engine default "results" (valid)

        var key = resultKey.Trim();

        if (WorkflowOutputKeys.Map.Contains(key))
            errors.Add($"Map '{mapNode.Id}' resultKey '{key}' is reserved — the map always emits '{string.Join("', '", WorkflowOutputKeys.Map)}', so this would silently overwrite the result array. Choose another name.");
        else if (!IdentifierPattern.IsMatch(key))
            errors.Add($"Map '{mapNode.Id}' resultKey '{key}' is not a valid output key. Use letters, digits and underscores (starting with a letter or underscore) so it can be referenced as {{{{nodes.{mapNode.Id}.outputs.{key}}}}}.");
    }

    /// <summary>
    /// Each <c>flow.loop</c> variable name must be a usable reference AND must not collide with a key the loop
    /// emits or the iteration-scope index. The loop reducer writes <c>iterations</c> / <c>failedIterations</c> /
    /// <c>terminationReason</c> AFTER the loop-var spread (so a same-named var is clobbered in the output bag),
    /// and the engine injects <c>index</c> into the per-pass <c>loop.*</c> scope (so a var named <c>index</c> is
    /// clobbered every iteration). The reserved set is <see cref="WorkflowOutputKeys.Loop"/> (Rule 8 contract-pin).
    /// </summary>
    private static void CheckLoopVariableNames(NodeDefinition loopNode, List<string> errors)
    {
        foreach (var name in ReadLoopVariableNames(loopNode.Config))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;   // a blank/absent name is a malformed config the parser tolerates; not this check's concern

            var trimmed = name.Trim();

            if (WorkflowOutputKeys.Loop.Contains(trimmed))
                errors.Add($"Loop '{loopNode.Id}' variable '{trimmed}' is reserved — the loop emits '{string.Join("', '", WorkflowOutputKeys.Loop)}', so this name would be silently clobbered at run time. Rename it.");
            else if (!IdentifierPattern.IsMatch(trimmed))
                errors.Add($"Loop '{loopNode.Id}' variable '{trimmed}' is not a valid name. Use letters, digits and underscores (starting with a letter or underscore) so it can be referenced as {{{{loop.{trimmed}}}}}.");
        }
    }

    /// <summary>
    /// A <c>flow.map</c> must bind a non-empty <c>items</c> collection (its INPUT — resolved at runtime, like
    /// flow.iterate's). A genuinely-absent binding silently fans out ZERO branches: the map completes
    /// <c>count: 0</c> and any downstream synthesizer runs on nothing — a green no-op that looks like success.
    /// This rejects ONLY an absent or empty binding; ANY present non-empty value (a <c>{{...}}</c> ref, a
    /// <c>{"$ref": "..."}</c> object, or an inline array) is accepted — the engine resolves it at run time and a
    /// non-array failure is its own clean error. Every valid authored map binds <c>items</c>, so this rejects
    /// only the no-op case.
    /// </summary>
    private static void CheckMapItemsBinding(NodeDefinition mapNode, List<string> errors)
    {
        if (!HasNonEmptyInput(mapNode.Inputs, "items"))
            errors.Add($"Map '{mapNode.Id}' has no 'items' binding. Bind a collection (e.g. items = {{{{nodes.planner.outputs.json.subtasks}}}}) — without it the map fans out zero branches and silently completes as an empty no-op.");
    }

    /// <summary>True iff the inputs object has an <paramref name="key"/> property whose value is present and non-empty (a non-blank string, a non-empty array/object, or any number/bool — anything but null/undefined/blank-string/empty-collection).</summary>
    private static bool HasNonEmptyInput(System.Text.Json.JsonElement inputs, string key)
    {
        if (inputs.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        if (!inputs.TryGetProperty(key, out var value)) return false;

        return value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Null => false,
            System.Text.Json.JsonValueKind.Undefined => false,
            System.Text.Json.JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            System.Text.Json.JsonValueKind.Array => value.GetArrayLength() > 0,
            System.Text.Json.JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true,
        };
    }

    /// <summary>Reads a string property from a node's Config object; null when absent or not an object/string. Case-insensitive match mirrors the engine's <c>MapConfig</c> deserialisation, so a non-canonical spelling (e.g. <c>ResultKey</c>) the engine still honours cannot slip a reserved/invalid key past validation.</summary>
    private static string? ReadConfigString(System.Text.Json.JsonElement config, string key)
    {
        if (config.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!TryGetPropertyIgnoreCase(config, key, out var value)) return null;
        return value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary>Reads the declared loop-variable names from a loop node's Config (the <c>loopVariables[].name</c> list); empty when absent or malformed. Case-insensitive property match mirrors the engine's <c>LoopConfig</c> deserialisation.</summary>
    private static IEnumerable<string> ReadLoopVariableNames(System.Text.Json.JsonElement config)
    {
        if (config.ValueKind != System.Text.Json.JsonValueKind.Object) yield break;
        if (!TryGetPropertyIgnoreCase(config, "loopVariables", out var vars) || vars.ValueKind != System.Text.Json.JsonValueKind.Array) yield break;

        foreach (var v in vars.EnumerateArray())
            if (v.ValueKind == System.Text.Json.JsonValueKind.Object && TryGetPropertyIgnoreCase(v, "name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String)
                yield return name.GetString() ?? "";
    }

    /// <summary>Case-insensitive single-property lookup on a JSON object — matches System.Text.Json's PropertyNameCaseInsensitive deserialisation the engine uses for LoopConfig.</summary>
    private static bool TryGetPropertyIgnoreCase(System.Text.Json.JsonElement obj, string name, out System.Text.Json.JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) { value = prop.Value; return true; }

        value = default;
        return false;
    }

    /// <summary>A usable output/variable key: a JS-style identifier so it round-trips through a <c>{{...}}</c> reference path (which splits on '.'). Shared by map resultKey + loop var name checks; mirrored in MapEditor.tsx for author-time feedback.</summary>
    private static readonly System.Text.RegularExpressions.Regex IdentifierPattern = new("^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
    /// <para>PR2: a body node MAY now SUSPEND (park the run on a durable wait). Each branch parks under its
    /// own iteration key <c>"&lt;mapId&gt;#&lt;i&gt;"</c>, the run stays Suspended until every branch wait
    /// resolves (the wait-for-all barrier), and a re-walk replays settled branches + re-runs only the
    /// suspended ones — so an <c>agent.code</c> (or any CanSuspend node) is a first-class map body element.
    /// The earlier PR1 fail-closed guard that rejected such a body at save time is intentionally gone.</para>
    /// </summary>
    private void CheckMapBodyShape(WorkflowDefinition definition, NodeDefinition mapNode, List<string> errors)
    {
        var body = definition.Nodes.Where(n => n.ParentId == mapNode.Id).ToList();

        if (body.Count == 0)
        {
            errors.Add($"Map '{mapNode.Id}' has an empty body. Add a flow.map_start and at least one body node.");
            return;
        }

        // The exactly-one-flow.map_start rule is enforced generically for every container by
        // CheckBodyStartCount — this method keeps only the map-specific single-terminal rule.
        var bodyIds = body.Select(n => n.Id).ToHashSet();
        var withOutgoing = definition.Edges.Where(e => bodyIds.Contains(e.From)).Select(e => e.From).ToHashSet();
        var terminals = body.Where(n => !withOutgoing.Contains(n.Id)).ToList();

        if (terminals.Count != 1)
            errors.Add($"Map '{mapNode.Id}' body must end in exactly one terminal node (the per-element result); found {terminals.Count}.");
        else if (terminals[0].TypeKey == "flow.map_start")
            errors.Add($"Map '{mapNode.Id}' body must contain at least one node after flow.map_start (the start marker can't be the per-element result).");
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
