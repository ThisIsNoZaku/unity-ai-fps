# Locations, runtime navigation and looping plans

These APIs have no dependency on the Stealth Maze sample, checkpoints, factions,
commander controls, or a particular character motor.

| API | Assembly | Responsibility |
| --- | --- | --- |
| `GoalDrivenAI.Navigation.LocationRegistry` | `GoalDrivenAI` | Immutable snapshot of named `CatalogHandle` locations; rejects actors and duplicate IDs. |
| `PlanCompiler.CompilePatrol` / `DependencyPlanExecutor` | `GoalDrivenAI` | Expand a patrol to ordinary navigation/completion/dwell nodes; execute the bounded body once, a finite number of times, or until cancelled. |
| `GoalDrivenAI.Unity.RuntimeNavMesh` | `GoalDrivenAI.Unity` | Owns a runtime bake, registered mesh and temporary agent type. Takes caller-provided build sources, bounds and `NavMeshAgentProfile`. |
| `GoalDrivenAI.Unity.NavMeshPathFollower` | `GoalDrivenAI.Unity` | Computes steering deltas and explicit navigation progress. Your motor retains transform, gravity and collision ownership. |

## Author a game's locations and routes

```csharp
using GoalDrivenAI;
using GoalDrivenAI.Language;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Navigation;
using GoalDrivenAI.Unity;

var locations = new LocationRegistry(new[] {
    new CatalogHandle("depot", HandleKind.Location, new Position(2, 0, 4), "yard"),
    new CatalogHandle("watchtower", HandleKind.Location, new Position(15, 0, 8), "north")
});
var clock = new UnityClock();
var compiler = new PlanCompiler(new CommandCatalog("game-v1", new[] {
    new OrderDefinition("Move", true, true, 120,
        PlanFailurePolicy.AbortPlan, repeatable: true)
}, allowLooping: true));
var context = new CommandContext("commander", "world-session", 1,
    new[] { "unit-1" }, locations.Values, capturedAt: clock.Now);
var result = compiler.CompilePatrol(context,
    new[] { "watchtower", "depot" }, dwellSeconds: 2, iterations: 0);
```

A patrol is a planning translation, not a separate route state machine. The
compiler emits **Move → wait for verified completion → optional timer** for each
stop, in the requested order. `CommandServiceAdapter.TryCommit` accepts the
validated plan; the host pumps the adapter and explicitly delivers/acknowledges
its ordinary Move orders. Only host-verified physical arrival may complete them.
Unknown/unauthorized locations and expansions exceeding the 16-node body limit
are rejected. There can be up to five stops with dwell, or eight without dwell.

`ValidatedCommandPlan.Iterations` (proposal JSON field `iterations`) is `1` by
default, `2..1024` for a finite count, or `0` for repetition until cancelled.
Dependencies remain acyclic inside an iteration. The executor restarts the body
only after every node succeeds and runs at most one iteration per pump. Looping
requires a catalog with `allowLooping: true`, repeatable order definitions, and a
completion wait for every issued order. Persistent policies cannot be looped.
Default single-pass plans retain their prior canonical JSON/hashes. Canonical
restore validates loop metadata and binds it into the hash.

The native adapter creates fresh order IDs per iteration, revalidates authority
and context, and resets timers/deadlines each cycle. Finished iteration records
are retired while a compact replay fence survives command save/restore; late
results and resubmission of retired IDs cannot act on a later cycle. Journaling
is bounded and traces carry the iteration number. Execution progress itself is
live host state, as before; restoring an accepted plan is not automatic resumption
of a loop. Hosts decide how to reissue gameplay intent after loading a session.

Cancel with `adapter.Cancel(plan.Hash)`. A navigation failure, deadline, stale
context or failed revalidation stops repetition. A replacement primary plan
cancels an overlapping loop only after atomic acceptance, and lower authority
cannot replace its intent between iterations. Treat a multi-recipient loop as
one plan: replacing a recipient stops that overlapping plan. Direct typed command
paths should explicitly cancel their old plan before replacing it, as the sample
does. Custom `IPlanNodeRuntime` implementations opt into looping by implementing
`IPlanIterationRuntime` and scoping/releasing their work per iteration.

Location IDs and region IDs are authored by the host game. `LocationRegistry`
does not confer authority: include only authorized handles in a `CommandContext`.
It captures immutable positions; rebuild a registry/context for a new snapshot.
Repeated stops are allowed in patrol input.

## Build navigation for the host geometry

Run Unity navigation APIs on the main thread. The host supplies `staticRoot` and
`worldBounds`, scoped to the intended scene geometry, and owns the lifetime:

```csharp
var sources = RuntimeNavMesh.CollectSources(staticRoot);
var navigation = new RuntimeNavMesh(sources, worldBounds,
    new NavMeshAgentProfile(height: 2, radius: .45f, climb: .2f));
var follower = new NavMeshPathFollower(navigation.Filter,
    new NavMeshPathOptions(horizontal: true));
```

Source collection uses Unity's
[`NavMeshBuilder.CollectSources`](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AI.NavMeshBuilder.CollectSources.html):
colliders, including non-box geometry, or render meshes and terrain can supply
the bake. Layer masks, geometry mode, default area and build markups are
configurable. Alternatively, pass your own `NavMeshBuildSource` list directly.
Exclude moving actors and decorative collision shapes from static sources.
Bakes capture geometry at construction; rebuild after changing static geometry.

Profiles configure height, radius, climb, slope, voxel size, tile size and minimum
region area. Create separate instances for different clearances or worlds. Each
instance owns only its mesh and agent type; disposing it does not remove another
instance's data. Call `Dispose()` on scene teardown and before replacing a bake.
The host project needs the Unity AI module (`com.unity.modules.ai`).

## Follow through an existing motor

In the host's update loop, read the active **Move order**, navigate to its
captured location and apply steering through the existing motor. `move` below is
the game's callback accepting a displacement:

```csharp
var order = commands.GetPrimary(actorId);
if (order?.Request.Type == OrderType.Move && order.Status == OrderStatus.Active)
{
    var progress = follower.Next(actualPosition, locations, order.Request.TargetId, out var delta);
    move(Vector3.ClampMagnitude(delta, speed * Time.deltaTime));
    if (progress == NavigationProgress.Arrived)
    {
        // The game's ICommandValidator.VerifyCompletion must verify actual position.
        commands.ReportCompletion(order.Request.OrderId, "host verified arrival");
    }
    else if (progress == NavigationProgress.Partial || progress == NavigationProgress.Unavailable)
        commands.ReportFailure(order.Request.OrderId, "navigation unavailable");
}
```

The plan's completion wait observes the command result, then the executor issues
the next navigation order. There is no `PatrolRoute` object to tick independently.
Emergency pursuit/search can suspend the ordinary Move through `CommandService`
and resume it afterward; bounded plan deadlines still apply.

The follower also accepts `(position, registry, locationId, out delta)` or a copied
`Vector3` destination for sensory evidence. It never moves an actor itself.
Keep only one locomotion owner; use this with a custom motor or CharacterController,
or use the existing `NavMeshMotor` adapter when a `NavMeshAgent` owns movement.

`NavigationProgress.Moving` includes steering toward an accessible endpoint.
`Arrived` means a complete path reached the requested destination within tolerance.
`Partial` means the accessible endpoint was reached without arriving at the
requested destination, including destinations snapped too far onto the mesh.
`Unavailable` means no usable path. The host chooses how to handle failures or
investigate partial endpoints; never count them as completed patrol visits.

`NavMeshPathOptions` configures replanning, target-change thresholds, sampling
radii, corner and arrival tolerances. By default deltas retain vertical movement;
set `horizontal: true` when the motor owns gravity on the XZ plane. A fixed
query filter per follower prevents mixing paths for different agent types. Call
`Clear()` after teleporting or replacing movement intent. An optional `IClock`
allows deterministic timing.

## Validation and sample boundary

Package navigation Play Mode tests use independent arena geometry, different
agent profiles and location names. They exercise detours, unreachable destinations,
and independent bake lifetimes without importing the maze. Add this package to
the Unity project's `Packages/manifest.json` testable list to discover them:

```json
"testables": ["com.thisisnozaku.goal-driven-ai"]
```

The standalone .NET test project covers pure registry and looping-plan behavior. Its
sample scenario tests compile the sample's `Core` source separately; the runtime
assembly does not ship the maze map or checkpoint rules. The Stealth Maze sample
imports its own `GoalDrivenAI.StealthMaze.Core` assembly and consumes the shared
APIs for movement. Its checkpoints, exit gating, captures, sensing policy and
commander UI remain game-specific.
