# Real-model cassettes

Committed transcripts of a REAL model authoring the plan-map-synth subtask decomposition, captured at the
`IStructuredLLMClient` seam by `RecordReplayStructuredLLMClient`.

These are the ground truth the REPLAY half of `RealModelPhaseAuthorshipFlowTests` runs from in CI (where no
API key is set). They make the kill-gate honest: the fan-out width + subtask content in a replay run came from
a recorded real-model call, NOT a hand-written deterministic fake.

## How a cassette gets here

It is captured by a **human**, never by an automated agent (the CI/sandbox has no Anthropic API key and no
egress). To record / refresh:

```bash
export CODESPACE_ANTHROPIC_API_KEY=sk-ant-...        # a real key
dotnet test backend/tests/CodeSpace.IntegrationTests \
  --filter 'FullyQualifiedName~RealModelPhaseAuthorshipFlowTests'
git add backend/tests/CodeSpace.IntegrationTests/Workflows/Cassettes
git commit -m "Record real-model planner cassette"
```

The LIVE-tagged test calls the real model AND writes/updates the cassette in this folder. The REPLAY-tagged
test then runs deterministically from it.

## File format

Each cassette is a human-diffable JSON list of entries:

```json
[
  {
    "KeyHash": "<sha256 of model+systemPrompt+userPrompt+canonical(jsonSchema)>",
    "Model": "claude-sonnet-4-5",
    "SystemPromptPreview": "",
    "UserPromptPreview": "Decompose this task into a list of independent subtasks…",
    "Json": "{\"subtasks\":[\"…\",\"…\"]}"
  }
]
```

Only `KeyHash`, `Model`, and `Json` are load-bearing on replay; the previews exist for review legibility.

## Drift

`PlannerCassetteDriftTests` pins the planner prompt + responseSchema the builder emits against the key inputs
the cassette is recorded under. If a planner prompt/schema change moves the key, that test fails and forces a
re-record here — a stale cassette can never pass silently.
