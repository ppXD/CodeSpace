# Backend test taxonomy

Three projects, one tier each. Pick the tier by **what real dependency the test needs**, then place it
in the matching project and tag it with the matching `[Trait("Category", …)]`. The `Category` (and, for
E2E, `Surface`) trait is the source of truth — CI filters on it, never on a count.

| Project | `[Trait("Category", …)]` | What it tests | Real dependencies | CI workflow |
|---|---|---|---|---|
| `CodeSpace.UnitTests` | `Unit` | Pure logic — parsers, builders, evaluators, the decision/grade math | **None** (no DB, no network, no OS processes) | `backend-unit.yml` |
| `CodeSpace.IntegrationTests` | `Integration` | A component integrated with its real **local, hermetic** dependencies: Postgres (EF change tracker, concurrency, the durable spool) and a local git remote (`file://`, no external network) | Real Postgres + (optionally) real local git/process | `backend-integration.yml` |
| `CodeSpace.E2ETests` | `E2E` | The **whole system / outward surface** exercised as a client would: the HTTP API and the headline `planner → map → real-agent → synthesizer` engine flow | The full assembled stack | `backend-e2e.yml` |

Two more categories live alongside the above (not a separate project tier):

| `[Trait("Category", …)]` | What it is | Where | CI workflow |
|---|---|---|---|
| `Sandbox` | Real-kernel `bubblewrap`/`prlimit` isolation against a live runner | `CodeSpace.SandboxTests` (its own project; runs confined only in the sandbox workflow, degrade-returns elsewhere) | `sandbox-isolation.yml` |
| `RealModel` | Supervisor brain/agent behaviour driven against a **live model endpoint** (gated on secrets; main + on-demand only, to control token cost). The **component** evals (decision · trajectory · arbiter — they drive the decider/arbiter directly, no DB) live in `CodeSpace.IntegrationTests`; the **whole-loop** eval (the full durable engine + Postgres + git, `Surface=Engine`) lives in `CodeSpace.E2ETests`. Both carry `[Trait("Category", "RealModel")]`, so the **trait — not the project — routes them** to `real-model.yml` (and `Category!=RealModel` excludes them from the normal Integration/E2E gates). | `IntegrationTests` (component evals) + `E2ETests` (whole-loop) | `real-model.yml` |

## The Unit / Integration / E2E line

- **Unit** — no out-of-process dependency at all.
- **Integration** — the component talks to a **real local** dependency that is deterministic and hermetic. A real
  Postgres or a `file://` git remote counts as Integration: git is a local deterministic tool, like the DB. The
  question it answers is *"does my component integrate correctly with its real dependencies?"*
- **E2E** — the **assembled product** is exercised end-to-end as a user/client hits it: the HTTP surface, or the
  full engine flow with a real agent executing under real sandbox confinement. The question is *"does the whole
  thing work?"*

A test that clones a hermetic local git remote and runs a real `check.sh` is **Integration**, not E2E — the real
git is a local tool, the same tier as Postgres. (See `SupervisorAcceptanceGradeFlowTests`.)

## E2E is split into two CI gates by the `Surface` trait

All E2E tests live in `CodeSpace.E2ETests`, but the two gates need different runtimes, so each E2E test also
carries a `Surface` trait:

- `[Trait("Surface", "Http")]` — runs on the host runner via `WebApplicationFactory`, reaching Postgres at
  `localhost`.
- `[Trait("Surface", "Engine")]` — runs in a privileged container (for unprivileged user namespaces / bubblewrap)
  and reaches Postgres by the service alias.

## Shared test infrastructure

The Postgres fixture, seed helpers, and job-client fakes live in `CodeSpace.IntegrationTests/Infrastructure` (and
`…/Workflows/Infrastructure`). `CodeSpace.E2ETests` **references the IntegrationTests project** to reuse them —
deliberately, rather than extracting a separate `TestInfra` assembly, to keep one home for that infra. xUnit
discovers `[CollectionDefinition]` per test assembly, so E2ETests declares its own `PostgresCollectionE2E` over the
shared `PostgresFixture` type (`CodeSpace.E2ETests/Infrastructure/PostgresCollectionE2E.cs`).

## Adding a new test

1. Does it need **no** out-of-process dependency? → `UnitTests`, `[Trait("Category", "Unit")]`.
2. Does it integrate a component with **real Postgres / local git**? → `IntegrationTests`, `[Trait("Category", "Integration")]`.
3. Does it exercise the **whole product** (HTTP surface or full engine flow)? → `E2ETests`, `[Trait("Category", "E2E")]` **plus** `[Trait("Surface", "Http" | "Engine")]`.

Fidelity tiers + the canonical E2E shape are specified in the repo's `CLAUDE.md` (Rules 9 and 12).
