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

Provider-credential tests share two more pieces, both in `CodeSpace.IntegrationTests/Infrastructure` (and reused by E2E):

- `GitLabDoorkeeperStub` — a loopback GitLab whose OAuth server is modelled on Doorkeeper: refresh tokens are single use, a
  refresh revokes the access token it replaces, and the REST and git smart-HTTP endpoints honour only live tokens. It also
  stands in for personal, project and group access tokens (`AddAccessToken` / `RevokeAccessToken`: `PRIVATE-TOKEN`, a date-valued
  `expires_at` at `personal_access_tokens/self`, 401 once revoked or expired) and can script the members lookup to fail
  (`FailMembers(status, times)`). Every user has an id of their own (`UserIdOf`) and a personal access token can be seeded for one
  (`SeedPersonalAccessToken`), so a test can tell two accounts apart and drive a pasted token, as the in-place re-authorization tests do.
  Once a project has been added (`AddProject(id, path)`) `GET /api/v4/projects/:id` answers that project to a live token — and 404 for any other id — so a
  repository move that asks the new credential for the project is seen from the stub's log (`Log`, the token and the path) as well as in the database.
  Its counters (`Count(StubEvent.Refused)`, …) are how a test says "GitLab was never shown a consumed refresh token".
  `GitLabDoorkeeperStubTests` pins what it does, so a red test built on it is the product's and not the stub's.
- `SilentProviderHost` (`CodeSpace.IntegrationTests/Webhooks`, linked into the unit project like `StubProviderHost`) — a loopback provider that
  accepts the connection and never answers, for what a hung GitLab costs the caller. A call to it only returns when the caller gives
  up, so a test of it runs the call on a thread of its own and bounds the wait, and shortens the sweep's deadlines by registering a
  `CredentialHealthDeadlines` instead of waiting out the real ones.
- Provider failures that reach a person: a failed run's record keeps the failure as a code and what it names, and the run page answers
  what is wrong and what the caller can do. `ProviderFailureThroughTheEngineFlowTests` drives the real engine against the stub or a
  loopback host: a real `git.list_prs` (a dead OAuth grant, a token GitLab revoked, a disconnected credential, a renewal GitLab did not
  answer, an address that redirects) and the six git write nodes that word a provider 401 themselves (`git.open_pr`, `git.merge_pr`,
  `git.pr_review`, `git.create_issue`, `git.close_issue`, `git.comment_issue`), and asserts what the node's ledger record carries and what
  the remedy says. A repair the remedy offers is proved against the real command the button runs: the redirect test sends
  `UpdateProviderInstanceCommand` (refused while a token is connected, accepted once it is disconnected) and `InitCredentialOAuthCommand`
  is sent as the credential's own Member, and as a teammate, to show the button's enabled state is the endpoint's answer (the credential's
  own person passes on membership, anyone else needs `credentials.manage`; `CredentialReauthorizationFlowTests.OwnerReconnect` and
  `CredentialOwnerReconnectE2ETests` pin the rest of who may start a reconnect and who may finish it) — never an edit by SQL that
  production would refuse. `ProviderRemedyFlowTests` pins the per-role matrix — and, for every asker role against every role the credential's person can have (or has left the team), that the remedy's button, the credential list's `callerMayRepair` and the real command (a connect, or a token replace against the stub) all agree, a command counting as accepted only if it returns and as refused for any exception at all — and every other source of a failure (a run that failed on
  another node than the one that handled a provider failure has none; a standing lookup that got no answer blames nobody);
  `ProviderRemedyHttpE2ETests` pins the wire shape for an Owner, a Member and a Viewer. Those fixtures write the failure with
  the real `IRunRecordLogger` from the real exception through `ProviderFailureCapture`, never a hand-typed payload.
- A provider failure inside a container (a `flow.map` branch, a `flow.loop` pass, either nested in the other) is carried onto the
  container's own `node.failed` record, because the run's failure is the one the node that ended it recorded and that node is the
  container. `ProviderFailureThroughTheEngineFlowTests.Containers.cs` drives the real engine through a map, a loop and both nestings against
  the stub (the sentence each container writes is asserted untouched), and pins the choices that keep it honest: a `terminate` map answers for
  the first failing branch by element order (not whichever finished first), a branch replayed from the ledger while a sibling was parked is
  still the one that ended the map, a failure the body handled — or a `flow.try` caught — leaves nothing on a container that later failed on
  something else, and a cell whose node worded its failure without a cause is traced through its own agent run, never a sibling's.
  `CellFailureReaderFlowTests` pins the reader's boundary (the cell's newest record, then its last agent run; nothing outside the cell — not
  another run of the same team either) and that the real rerun seeder carries a failed cell's failure into the fork it replays it in, from the
  cell's own record or from the original run's agent run, and leaves a failure-less cell's record as it was. A map-branch rerun whose fork fails
  again on the replayed failed branch answers with the original run's remedy, never the fresh branch's
  (`ProviderFailureThroughTheEngineFlowTests.Rerun.cs`, and over HTTP in `ProviderFailureInAContainerHttpE2ETests`, which also pins what the run
  detail and the lean remedy read answer, plus that a run recorded before containers carried the failure answers exactly as it did).
- A provider failure inside a `flow.subworkflow` CHILD run that ends the PARENT is read back where the child recorded it — by the same
  `IRunFailureReader` the child's own page asks — and carried up: the engine's completion hook (`ISubworkflowService.ResumeParentIfChildTerminalAsync`, the
  one resume path the reconciler's stranded-parent backstop re-fires too) stamps it on the resume payload, the node fails with its own sentence untouched and
  the failure beside it (`NodeResult.CarriedFailure`), and the parent's `node.failed` — and, through a map or loop the node sits in, the container's — carries
  it with a `FailureOrigin` naming the deepest child run that met it, the node that ended that run and when that run recorded it. `ProviderFailureThroughTheEngineFlowTests.SubWorkflows.cs`
  drives the real engine parent → child (top level, in a map, in a loop, and three levels deep) against the stub and pins: the parent's remedy is the
  child's (same code, credential, repositories, buttons, resolution — compared with the child's own remedy) plus the origin and a sentence that names it; a
  failure that names no repository is traced through the CHILD's launch scope, never the parent's; a retried node carries the last attempt's child; and the
  negatives — a child that failed on something else, handled the failure, or handled it and failed on something else carries nothing — plus an origin that is
  not the team's run (another team's, or none) is never named and never changes the remedy; a fan-out parent (two children at once, one slow) whose token is replaced between the
  child's failure and the barrier's release reads "fixed" exactly as the child's page does, because the fix is measured from when the CHILD recorded the failure
  (`FailureOrigin.At`), not from the parent's later record — and a record that kept no time falls back to the carrying record's own; a child whose node worded
  its failure alone is traced through its own agent run, as the child's page is; a cancelled child carries nothing even when an agent run of it met a refused push; and a map-branch rerun fork that fails on a
  replayed sub-workflow branch names the child the ORIGINAL branch ran (`….Rerun.cs`). `StuckRunReconcilerFlowTests` pins the backstop path;
  `ProviderFailureInASubWorkflowHttpE2ETests` pins the wire shape for an Owner, a Member and a Viewer, that Re-run on the parent replays the PARENT and
  starts a fresh child, and that a parent recorded before the field answers as it did. The frontend's `ProviderRemedyCard.test.tsx` / `RunRemedyCard.test.tsx`
  pin the "In sub-workflow … (run …)" line, its link, and that Re-run replays the run the page is on.
- `RefreshPodHarness` — pods as real OS processes (`CodeSpace.StorageTestWorker`'s `oauth-refresh-pod` mode) running the
  production refresher, or the pinned previous build's (`StorageTestWorker/PreviousBuild`), against the fixture's Postgres.
  One process shares one refresh semaphore, so only separate processes exercise the cross-pod advisory lock.

## Adding a new test

1. Does it need **no** out-of-process dependency? → `UnitTests`, `[Trait("Category", "Unit")]`.
2. Does it integrate a component with **real Postgres / local git**? → `IntegrationTests`, `[Trait("Category", "Integration")]`.
3. Does it exercise the **whole product** (HTTP surface or full engine flow)? → `E2ETests`, `[Trait("Category", "E2E")]` **plus** `[Trait("Surface", "Http" | "Engine")]`.

Fidelity tiers + the canonical E2E shape are specified in the repo's `CLAUDE.md` (Rules 9 and 12).
