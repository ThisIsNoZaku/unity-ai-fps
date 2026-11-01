# Stealth Maze

A small stealth game with three playable perspectives, explicit factions, and
numbered checkpoints. The `infiltrator` must visit every checkpoint in order and
then reach the green exit. The `guards` faction has a commander who assigns
persistent checkpoint-defense orders. Touching a guard resets the infiltrator,
all guards, objective progress and defense assignments.

## Run

1. Install this package in a Unity 6 Built-in or URP project.
2. In Package Manager select **Goal Driven FPS AI → Samples → Stealth Maze → Import**.
   If you imported an older version, replace the old imported sample with a fresh
   import, including its new Core assembly.
3. For first-person play, set **Edit → Project Settings → Player → Other Settings →
   Active Input Handling** to **Both** or **Input Manager (Old)**. Restart the
   Editor if requested. Commander and observer controls use GUI buttons.
4. Open the imported `StealthMaze.unity` scene and press Play. Choose a role in
   the join menu. The scene generates at runtime; no manual NavMesh bake, models or
   external services are needed. The simulation pauses while choosing a role.

## Roles

- **Infiltrator:** first-person control of the yellow character in the
  `infiltrator` faction. Click the Game view to capture the cursor; WASD moves,
  mouse looks, hold Left Ctrl or C to crouch, Esc releases the cursor, R restarts
  and Tab opens role selection. The AI commander initially distributes guards
  across checkpoints and redeploys them to remaining checkpoints as progress is
  announced. It does not use the hidden infiltrator's position to issue orders.
- **Guards commander:** overhead tactical view, controlling the `guard-commander`
  identity in the `guards` faction. Use each guard's CP buttons to assign which
  checkpoint it protects, or use **Patrol CPs** to release its hold and cycle
  through all named checkpoints. Guards briefly scan at each patrol stop and
  continue after a complete lap. The selected assignment is marked with a dot. An AI
  infiltrator follows the checkpoint course and uses low passages. Your orders
  persist during pursuit and resume when the guard finishes searching. Use the
  Restart button to start a fresh encounter.
- **Observer:** the same overhead view with an AI infiltrator and AI commander.
  Choose a Guard button to view that guard's senses through its `PerspectiveDebug`
  camera and hear only sounds its hearing sensor accepts. Overhead returns to the
  map. There are no player movement, order or restart controls. Use Roles to change
  perspective; role changes preserve the encounter rather than resetting it.

In first person, use Tab or release the cursor to access Roles. The overhead
views reveal the whole maze for this small demo. Neither view adds knowledge to
individual guards. Roles is a session/view control: opening its menu pauses the
simulation. Switching into Observer continues the same simulation without
applying new defense orders merely because the role changed.

Checkpoints are generated at distinct positions along the ordinary entry-to-exit
route. Pink marks the next required checkpoint, violet marks later checkpoints,
and green marks completed checkpoints. Numbers also appear above them. Visiting
one early does not skip the required sequence. Reaching the exit before all
checkpoints are complete does not win. The HUD shows progress, captures, each
guard's current goal and assignment. Blue guards defend, amber guards investigate,
and red guards see the infiltrator. Shouts appear as text feedback; recorded
voices/audio assets are not required.

Teal lintels mark low passages; only the crouched infiltrator fits. Amber crates
provide low cover: crouch behind them to break line of sight. Walking makes
louder footsteps than crouching. The AI infiltrator is intentionally simple:
it crouches and follows NavMesh paths through the objectives, with the
same collider and noise rules as human play. It is not an evasive tactical
planner and can be caught repeatedly by well-positioned guards.

## Architecture and tuning

The sample consumes the reusable runtime `LocationRegistry`, looping command plans,
`RuntimeNavMesh` and `NavMeshPathFollower` services. See the package's
[Navigation guide](../../Documentation~/Navigation.md) for using them in another
game. `Core/MazeMap.cs` and `Core/MazeScenario.cs` belong to the separate sample
Core assembly; they are not dependencies of the runtime navigation APIs.

`MazeScenario.Locations` uses `LocationRegistry` to register `checkpoint-1` through `checkpoint-N` and
`exit` as the package's `CatalogHandle` / `HandleKind.Location` entries. Each
checkpoint also binds its defense `RegionId`. `NextObjective` identifies the next
checkpoint or exit. Guard defense orders carry these location IDs and regions;
NavMesh pathing consumes the corresponding location handles. Objective checks
and scene markers use the same catalog positions, rather than recomputing them
from grid coordinates. Locations remain stable across capture/restart.

`MazeScenario` owns the objective sequence and uses `CommandService` plus an
`AuthorityGraph` to give the commander authority over guards. Faction, capability,
checkpoint target and defense region are validated before orders are delivered
and acknowledged. Infiltrator, observer and ordinary guard identities cannot
issue commander orders. `HoldArea` remains persistent: pursuit/investigation
suspends it, then execution resumes at the assigned checkpoint. Guard locomotion
follows Unity NavMesh paths built from the floor, walls, crates and lintels;
defense means moving to the post and scanning there.
Patrol plans navigate the named checkpoint locations in sequence.
The commander's **Patrol CPs** action compiles the checkpoint list into a looping
Move/completion/dwell plan. Its ordinary Move orders use the same authority,
delivery, acknowledgement, suspension and verified-arrival checks as other orders. A new CP assignment restores persistent defense. Guards do not collide with one another,
which prevents deadlocks in narrow corridors.

`BeliefStore` and `GoalSelector` supply copied evidence, expiry, completed
searches and utility decisions. Sight tests the infiltrator's current head height
with a physics ray, so walls, crates and lintels occlude it. Losing sight leaves
a last-seen snapshot, not access to the unseen infiltrator's transform. Footsteps
supply an anonymous location. Shouts preserve the original sighting ID, position
and time, heard from the shouting guard's position. Only direct vision causes
shouts; listeners do not rebroadcast. Listeners converge on the captured location,
dwell and scan, mark the search complete, then return to checkpoint defense.
The auditory model is radial and can carry through walls; it does not simulate
room/portal acoustics. This sample does not exercise GOAP combat, weapons or
language inference.

Select **Stealth Maze Demo** before Play to configure seed, enemy/checkpoint
counts, sight range, field of view, sight sensitivity, hearing range, hearing
sensitivity and shout range. Sense defaults are copied to the spawned components.
During Play, select a guard to tune `MazeSight`, `MazeHearing` and `MazeGuard`.
Sensitivity zero or disabling a sense turns it off. `recognitionSeconds` controls
sight recognition delay; higher sensitivity shortens it. Hearing strength falls
with distance, is capped by listener range, and scales with source radius and
listener sensitivity. Player speeds/noise radii are on `MazePlayer`.

To add a sense, subclass `MazeSense`, implement `Observe`, and attach it before
`MazeGuard.Initialize`. Return a copied `MazeObservation`. Guard execution
remains the only enemy movement owner; both infiltrator modes use one player
motor.

Two runtime NavMeshes use separate agent types for standing guards and the
crouched infiltrator. Their baked radius includes clearance around cover; low lintels exclude
guards by height. Only static geometry enters the bake, before actors spawn.
CharacterController remains the movement/collision owner; both AI modes follow
NavMesh corners and periodically replan copied destinations. Mesh data and temporary
agent settings are removed when the demo is destroyed. The Unity AI module must
be enabled in the host project (`com.unity.modules.ai`).

The map seed is deterministic and ordinary corridors always connect the
entry, checkpoints and exit; crawl shortcuts are optional for human play.

The bootstrap selects a Lit shader for the active render pipeline. Built-in and
URP are the intended baseline; HDRP rendering is not validated. For a standalone
build, add the imported scene to Build Profiles and retain the chosen Lit shader
in Graphics → Always Included Shaders (materials use runtime shader lookup).

## Validation

Headless tests verify connectivity, crawl restrictions, audibility, checkpoint
ordering, escape gating, faction authority, order replacement, emergency resume,
and capture resets. They do not compile Unity scripts or establish playability.

With Unity Test Framework installed, run the imported sample's Play Mode tests
in **Window → General → Test Runner → PlayMode**. They cover scene geometry,
sight occlusion, shout boundaries, crouch clearance, capture, role restrictions,
autonomous movement and exit gating. Manually check:

- Join each role. Verify the expected camera, human/AI infiltrator control, and
  absence of order/restart controls in Observer. Switch roles without resetting.
- As Infiltrator, visit all numbered checkpoints, then exit. Try the exit early,
  then capture/restart after partial progress; verify progress resets.
- As Commander, assign different checkpoints to individual guards. Trigger a
  sighting and verify the order suspends during pursuit and resumes afterward.
- As Observer, watch AI progress and automatic capture resets. Opening the Roles
  menu pauses movement; joining a role resumes it.
- Verify walls/cover block sight, walking/crouching produce different hearing
  ranges, and shout recipients converge only when within earshot.
- Use a teal passage; guards cannot enter and the infiltrator cannot stand
  inside it. Verify capture on contact with both moving and stationary actors.

Play Mode tests also bake both NavMeshes against the generated colliders, check
guard reachability and crawl clearance, and run the AI infiltrator to escape and
guards to their posts with opposition disabled to isolate navigation. Run these
in the host Unity project alongside the manual encounter checks above.

The sample attaches the reusable `PerspectiveDebug` component to guards via their
`IAgentPerspectiveSource` interface. The eye camera uses the real sight settings;
accepted hearing events carry short procedural footstep/shout clips. These are
placeholder cues, not recorded dialogue. The debug view hides objective labels
that would reveal locations through walls and restores normal audio/view on exit.
