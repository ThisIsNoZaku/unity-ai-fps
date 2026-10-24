# Goal-driven FPS AI

A Unity package and headless C# implementation of the version 1.2 architecture.
This is a **prototype foundation**, not a finished FPS or a claim that the
production encounter, speech accuracy or performance acceptance criteria pass.

## Architecture

- **Knowledge:** bounded, timestamped evidence; unknown facts; original report
  IDs/times; confidence decay, growing positional uncertainty and completed
  searches. The Unity sensor publishes copied observations, never live targets.
- **Decisions:** eligibility and feasibility gates, normalized utility,
  deterministic ties, 1-second commitment, 0.15 hysteresis and emergency bypass.
- **Tactics:** incremental uniform-cost GOAP with immutable snapshots and bound
  facts, duplicate detection, depth 8 and 128 expansions. Cutoffs return no
  executable prefix; generation/revision/goal checks reject stale searches.
- **Execution:** per-execution actions, verified effects, channel arbitration,
  atomic expiring leases, deadlines, bounded cancellation and exception-safe
  cleanup. Predicted planner effects never update gameplay or beliefs.
- **Orders:** caller-derived authority, faction/scope validation hooks, explicit
  delivery and acknowledgement, monotonic replacement ordering, queues of 8,
  persistent policies, suspension, expiry, deduplication and verified completion.
  Delegation is opt-in, bounded to depth 3 / 8 children, narrows constraints and
  rejects recipient ancestry cycles. Parent dispatch is not parent completion.
- **Language:** strict JSON compilation, 16-node / 8-recipient DAGs, authorized
  captured handles, canonical plans/hashes, atomic acceptance, main-thread
  dispatch, prerequisites and persistent milestones. Providers propose data;
  they never receive character adapters.
- **Recovery:** request IDs, generations, session/context checks, a configurable
  interpretation deadline (2.5 seconds by default), one bounded repair, typed
  clarification/failure and validated-plan journaling. Failed translation
  leaves existing orders unchanged.

Gameplay owns health, ammunition and transforms. `NavMeshMotor` alone controls
locomotion; `WeaponAdapter` alone spends rounds and applies hitscan damage.
`AimController` uses observed positions and frame-rate-independent turn speed.
The guard stops ordinary fire under HoldFire and checks the actual dispersed
shot ray for friendly obstruction.

## Headless validation

Requires the .NET 8 SDK or newer:

```sh
dotnet restore /home/runner/work/unity-ai-fps/unity-ai-fps/UnityAiFps.sln
dotnet test /home/runner/work/unity-ai-fps/unity-ai-fps/Tests/GoalDrivenAI.Tests.csproj --no-restore
```

For a focused run, add `--filter FullyQualifiedName~OrderTests` (or
`TacticsTests`, `LanguageTests`, `CharacterTests`). NUnit tests cover evidence
aging, planning bounds, resource cleanup, committed ammunition, authority,
delivery, queues, delegation, stale replacements, compiler rejection, request
idempotency, cancellation, dependency failures and native command integration.
The core targets .NET Standard 2.1 / C# 9; headless tests do **not** compile
Unity-dependent adapters.

## One-guard Unity setup

Use a Unity 6 project (Unity 6000.3 is the intended validation target). In Package
Manager, add the package from this local manifest:

`/home/runner/work/unity-ai-fps/unity-ai-fps/Packages/com.thisisnozaku.goal-driven-ai/package.json`

The package declares Unity's Newtonsoft JSON dependency. The headless equivalent
is pinned independently to Newtonsoft.Json 13.0.4. No provider keys are included.

1. Create a small floor, walls/occluders and colliders. Bake a NavMesh using your
   project's compatible Unity navigation authoring package. Navigation baking,
   player movement, assets and a prebuilt scene are not bundled.
2. Add an `AIScheduler` and a `CommandHost` to scene objects. Populate the host's
   actor list. Set the player identity/faction; hostile orders are rejected.
3. Create an AI Profile with **Assets → Create → Goal Driven AI → Profile**.
   Add `AIBrain` to a guard. Required motor, sensor, actor, aim and weapon
   components are added automatically. Assign its profile, scheduler and host.
   Put the guard on the baked NavMesh. Assign an eye/aim pivot above its own
   collider and configure candidate, occlusion and firing masks.
4. Add an `Actor` and collider to a hostile target. Add optional `CoverSlot`s:
   forward points toward the threatening side, refuge lies on the protected
   side, and each demo slot is single-capacity. Assign slots to the brain.
5. For an ally, use the player's faction. Add `CommandInputController`, select
   ally actors and assign a marker. Its prototype menu offers Move, HoldArea,
   HoldFire and immediate Stop/Cancel without inference. HoldArea additionally
   requires a `CommandRegion` with a convex box boundary and a marker inside.
6. Observe goal/last-failure properties, sensor and cover gizmos, scheduler
   timings, command feedback and the primary order shown by the menu.

Runtime prefab instances receive unique actor identities before registration.
Scene IDs remain authored where unique; save the resolved runtime identity for
spawned instances. Pooling must disable the brain, reset authoritative actor /
equipment state, then re-enable it; brain memory, actions and leases reset on
disable. Load logic must resolve saved entity IDs before restoring orders.

The demo guard supports Move, HoldArea, Attack, Follow and single-point
SearchArea. Attack uses bounded move/burst/observe episodes, never a predicted
kill. Follow uses recently sensed ally locations rather than an unseen leader's
transform. Search reaches a remembered/captured point, dwells, then records
checked evidence; exhausted attack searches report failure rather than success.
Cover geometry and routes are rechecked; occupied slots retain renewable leases
until departure, invalidation, death or disable.

## Language and voice integration

Call `CommandInputController.Configure(model, contextSource, catalog, recognizer)`
on the main thread after the host initializes. Choose `FixedCommandModel` for
tests, or `LocalModelProvider` / explicitly opted-in `CloudModelProvider`.
Share a `ScheduledCommandModel(provider, concurrency: 2, maxQueued: 16)` across
interpretation services to bound inference concurrency and queue admission.
Queued requests honour cancellation; active permits stay held until the
provider actually returns, even if it ignores cancellation.

Implement these application boundaries:

- `ICommandContextSource`: capture only the issuer's permitted recipients,
  observed/contact handles and named locations, aimed/marker handles, session
  and relevant context revision. `IsCurrent` must validate authority/bindings,
  not simply accept all snapshots. Do not invalidate for unrelated world changes.
- `CommandCatalog`: supply only supported orders and authored failure policies.
  An order's `Primary` flag is authored, never supplied by the model.
- `ILanguageModelTransport`: implement the chosen loopback service or
  authenticated **application backend**, enforcing response limits while
  streaming. Provider credentials belong on that backend, not in Unity.
- `ISpeechRecognizer`: return only finalized transcripts. `BeginVoice` captures
  spatial context before recognition; partial text/tokens do not dispatch.

Both services expose `Pump` for main-thread acceptance/dispatch. The Unity input
component pumps them; provider continuations do not access Unity. Stopping
translation and cancelling an accepted order are separate operations.

Example proposal for a catalog named `demo`, with authorized `mara`, `ivo`,
`doorway` and `office` handles:

```json
{
  "schemaVersion": 1,
  "catalogVersion": "demo",
  "nodes": [
    {"id":"policy","type":"SetPolicy","recipients":["mara","ivo"],"policy":"Engagement","value":"HoldFire"},
    {"id":"cover","type":"IssueOrder","dependsOn":["policy"],"recipients":["mara"],"order":"HoldArea","target":"doorway"},
    {"id":"ready","type":"WaitForResult","dependsOn":["cover"],"observedNode":"cover","predicate":"HoldPositionEstablished","timeout":30},
    {"id":"search","type":"IssueOrder","dependsOn":["ready"],"recipients":["ivo"],"order":"SearchArea","target":"office"}
  ]
}
```

Location handles used for HoldArea must bind an authored region. Mara's
HoldArea remains active after the milestone; Ivo is not dispatched if the
prerequisite fails or is cancelled. Sequential primary orders for the **same**
recipient require an explicit completion wait, not merely an issue dependency.
Native policy relaxation and formation orders are deliberately rejected rather
than silently pretending to apply unsupported semantics.

`CommandService.Save/Restore` stores immutable order/policy/batch state.
`BoundedPlanJournal` has a host persistence hook; the dependency executor exposes
its state/outcomes. A real save system must persist accepted plans and terminal
outcomes, revalidate bindings and avoid replaying completed effects. Runtime
NavMesh paths and leases must always be rebuilt, not restored.

## Remaining game-specific delivery

Unity is not installed in the development sandbox: adapter compilation,
PlayMode encounters and profiling still need to run in the declared Unity
project. The proposed 3 ms AI / 1 ms planning p95 budgets and voice latency
targets are configurable measurement targets, **not measured guarantees**.
The scheduler caps dispatch work between agents; a synchronous physics query
cannot be preempted halfway through.

This package does not supply a complete FPS, player controller, authored
encounter, door/alarm/animation adapters, room/portal acoustics, squad role
allocation, multi-anchor search coverage, a concrete speech/model transport,
provider failover, full save-game integration, a production clarification UI,
semantic provider corpus, or a 32-agent benchmark. Those need the model,
recognizer, weapon/traversal rules, hardware and encounter decisions called out
in the design. Core interfaces and explicit capability rejection keep these
extensions from bypassing authority, knowledge or gameplay ownership.