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

## Integrating into an existing game

Start in a separate test scene using the one-guard setup above, with ordinary
typed orders working **before** enabling inference. Keep the package in source
control alongside the game, or install it through Package Manager's **Add
package from disk** using the manifest above. Another checkout needs its own
absolute manifest path; the runner path is not a game deployment requirement.
Assemblies in your game that use these APIs must reference `GoalDrivenAI` and,
for the supplied MonoBehaviours, `GoalDrivenAI.Unity`.

Choose one integration path:

- **Prototype components:** use the supplied `Actor`, `NavMeshMotor`,
  `AimController`, `WeaponAdapter` and `AIBrain` together. Disable any existing
  NPC controller that also moves, rotates, fires or reloads this actor. Do not
  combine NavMeshAgent movement with root-motion or Rigidbody movement authority.
- **Existing character stack:** implement the contracts in
  `GoalDrivenAI.Characters` against your health, movement, aiming, weapon and
  interaction systems, then compose the pure tactics/commands services in your
  own brain. The supplied `AIBrain` resolves **concrete components** with
  `GetComponent`; implementing `INavigationMotor` or `IWeaponAdapter` alone does
  not replace those components. Adapting that composition is game-side work,
  not an Inspector option currently provided by the package.

For either path:

1. Keep damage, death, ammo transfer and interaction results authoritative in
   the game. Publish verified outcomes to action execution; never treat a
   planner prediction or LLM response as an actual kill, reload or arrival.
   Keep Unity scene/physics access on the main thread.
2. Bind stable entity IDs and faction relationships. Populate `CommandHost`'s
   actor list for the prototype; a custom host must implement
   `ICommandValidator` and configure `AuthorityGraph`. Register recipients with
   the scheduler/host and unregister on disable. Add capabilities to validation
   only after their execution and completion checks exist.
3. Route menus, scripts and language plans through the same `CommandService`.
   `Submit` starts pending delivery, not action execution: your communication
   layer must deliver and acknowledge it. The prototype host does this with its
   configured radio delay. Use verified completion/failure reports; expose
   pending, active, suspended and terminal states separately in game UI.
4. Build an `ICommandContextSource` from the issuer's permitted knowledge.
   Resolve display names to actual actor IDs and snapshot locations into
   `CatalogHandle`s. A named HoldArea location must include its `RegionId`.
   Catalog identifiers allow letters, digits, `_` and `-`, up to 96 characters;
   do not use arbitrary scene paths or display strings as handles.
5. Author a versioned `CommandCatalog` containing only implemented orders.
   For example, define Move as target-required and primary, with an authored
   timeout and `PlanFailurePolicy`. Give the model that vocabulary, the
   captured handles and selected recipients, not a scene-object registry.
6. In your bootstrap component's `Start`, after `CommandHost.Awake` has run,
   call `CommandInputController.Configure` on the main thread. Start with a
   `FixedCommandModel` returning a known valid proposal. Connect your UI to
   `SubmitText`, `BeginVoice`, `CancelPending` and `Stop`, rather than invoking
   motors/weapons from UI or inference callbacks.
7. On scene/save/session transitions, cancel pending interpretations and
   invalidate the context session/relevant revisions. Reconfigure the input
   component after it is disabled and re-enabled; it disposes its interpretation
   service on disable. Persist accepted orders/plans separately from transient
   provider requests; do not retranscribe or repeat completed effects on load.

Before moving into the main encounter, verify occlusion/search, unreachable
routes, HoldFire, delayed delivery followed by Stop, recipient death, and
pooling. Then enable a real provider and verify that malformed, stale and
cancelled responses leave the current valid order untouched.

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

## Locally hosted LLM sidecar

A sidecar is a separate inference process on the **same computer** as the game:

`game → ILanguageModelTransport → loopback HTTP inference → proposal JSON → compiler → CommandService`

The repository supplies `LocalModelProvider`, scheduling and validation, but
**does not ship an HTTP transport, model runner or automatic process launcher**.
Starting a server alone does not enable language commands. The following
Ollama example shows the deployment and the mapping your transport must
implement; a llama.cpp server or another runner can use the same game contracts.

### 1. Start and check the local service

Install Ollama from [its official instructions](https://docs.ollama.com/).
Check model/runtime licenses and hardware requirements before redistribution.
For an initial text-only experiment, use a small instruction model such as
`llama3.2:3b`; this is an example, not a measured accuracy or latency choice.

In a POSIX shell, run the server in one terminal:

```sh
OLLAMA_HOST=127.0.0.1:11434 ollama serve
```

If an installed Ollama background service already owns that port, use that
service (verify its loopback binding) or stop it before launching your own;
do not start two servers on the same port. In another terminal:

```sh
ollama pull llama3.2:3b
curl --fail --max-time 5 http://127.0.0.1:11434/api/tags
curl --fail --max-time 120 http://127.0.0.1:11434/api/chat \
  -H 'Content-Type: application/json' \
  -d '{"model":"llama3.2:3b","stream":false,"format":"json","messages":[{"role":"user","content":"Return only a JSON object with a ready field set to true."}],"options":{"temperature":0,"num_predict":256}}'
```

The second response should be an Ollama envelope with JSON text in
`message.content`. This checks inference only; it is **not** a valid command
proposal or a game acceptance test. The generous CLI timeout allows cold
loading and is not the game's interpretation deadline. Download/preload models
before play; pin the tested runtime version and record the model digest from
`/api/tags`, since model tags can change. See the runner's
[chat API reference](https://docs.ollama.com/api/chat) for version-specific
options and structured-output support.

### 2. Implement the game-side transport

Implement `ILanguageModelTransport.IsHealthy` and
`SendAsync(LanguageModelRequest, maximumOutputCharacters, cancellationToken)`
in the game or an application integration assembly:

- Configure a fixed, trusted loopback base URI and model ID outside NPC code.
  Never let model output, scene labels or user utterances choose a URL, model,
  executable or shell arguments. Do not expose the unauthenticated service on
  `0.0.0.0` or add permissive browser-origin settings.
- Cache health/readiness from bounded asynchronous checks; do not block Unity
  in the `IsHealthy` getter or perform health HTTP requests every frame.
- POST to `/api/chat` with the configured model, `stream: false`, and bounded
  generation/context settings. Put the command-proposal contract, schema
  version and `request.Catalog` vocabulary in the instruction message; include
  `request.Transcript` and authorized `request.Context` values as clearly
  delimited data. Include captured marker/aimed handles so “there” resolves to
  the submitted snapshot, not the player's later aim. Treat labels and dialogue
  as untrusted data, not permission to change the contract.
- Ask for a single proposal object matching the example above: no Markdown,
  executable expressions or invented handles. `format: "json"` improves shape
  but does not enforce this command schema. If your runner supports a JSON
  Schema, supply the approved schema as an additional constraint; the
  `PlanCompiler` remains mandatory in either case.
- Parse the bounded HTTP envelope, extract `message.content`, and return
  `new ModelResult(ModelStatus.Success, json: content)` only for a complete
  response. Do **not** pass the whole Ollama envelope to the compiler or
  dispatch partial tokens. Bound the outer response bytes while reading as
  well as the extracted proposal length (`maximumOutputCharacters`, currently
  at most 32,768); checking size after buffering an unbounded body is too late.
- Map unavailable service, non-success HTTP status, invalid envelopes and
  cancellations to the appropriate `ModelStatus`; never substitute a
  successful empty plan. Forward cancellation to HTTP, dispose responses and
  use a finite request timeout. Preserve `RequestId` and the original deadline
  through a repair attempt. `Deadline` uses the game's injected clock, not a
  Unix timestamp to compare against the server clock.

Use an HTTP API supported by your Unity target without blocking `.Wait()` or
`.Result`. HTTP/provider continuations must not read Transforms or commit
orders. Do not log raw microphone audio or unrestricted world context.

### 3. Wire the provider and own its lifecycle

Pass your transport to `LocalModelProvider(transport, timeout)`, wrap that
provider in one shared `ScheduledCommandModel`, then pass the wrapper,
context source and catalog to `CommandInputController.Configure`. For a single
local model, start with concurrency 1 and a small queue; tune after measurement.
Also configure server-side inference/queue limits: a timed-out HTTP call may
leave server-side generation running, so the client scheduler alone is not a
hard bound on runner work.

The supplied input component uses a 2.5-second interpretation deadline.
Choose a transport/provider timeout within that window and warm the model
before showing language input as ready. If your hardware needs a different
budget, change the game integration deliberately; changing only the server's
timeout will not extend the acceptance gate. No cloud fallback is automatic.
Text works without speech recognition; voice still needs an implementation
of `ISpeechRecognizer`.

For a packaged game, have the launcher start a trusted runner executable with
fixed arguments, wait for bounded readiness, and retain its process handle.
On exit, cancel pending requests, close HTTP resources, and stop only a runner
process the launcher owns. Do not kill a player's shared Ollama service or
download models silently. A manually launched `ollama serve` can be stopped
with Ctrl-C; installing, updating and bundling a runner are separate deployment
work, not behavior provided by this package.

### 4. Smoke tests and troubleshooting

| Check or symptom | Expected action |
| --- | --- |
| Connection refused / port in use | Check the owned process, configured port and `/api/tags`; do not widen the network binding. |
| CLI works but game rejects output | Inspect the bounded failure detail; extract `message.content`, match schema/catalog versions, recipient IDs and authorized target handles. |
| First request times out | Preload the model and measure cold/warm latency, output tokens and queue wait; do not disable deadline/staleness validation. |
| “Move there” | Capture a marker handle, submit, then move the aim; the accepted destination must remain the captured one. |
| Cancel or replace during generation | A late reply must not dispatch or replace the newer command, even if server cancellation fails. |
| Stop runner during an active NPC order | Interpretation fails visibly; the NPC keeps its current valid order and direct menu/Stop controls still work. |
| Unsupported or ambiguous command | Commit nothing; show rejection/clarification, never guess a scene target. |

Run the same command corpus against `FixedCommandModel` and the real sidecar:
compare accepted order structure and parameters, not model wording. Record
runtime/model digest, prompt/catalog/schema revisions, valid-plan rate and
end-to-end latency on the intended game hardware. The commands above are setup
examples; no local runner or model is installed or validated by this repository.

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