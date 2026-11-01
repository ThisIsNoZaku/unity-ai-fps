# Stealth Maze

A small first-person escape game demonstrating the package's `BeliefStore`,
`GoalSelector`, copied evidence, evidence expiry and completed searches. Enemies
patrol, pursue sightings, investigate footsteps and converge on shouted sightings.
Touching a guard returns you and the guards to their starting positions. Reach
the green floor tile to win; press R to restart.

## Run

1. Install this package in a Unity 6 project.
2. In Package Manager select **Goal Driven FPS AI → Samples → Stealth Maze → Import**.
3. Set **Edit → Project Settings → Player → Other Settings → Active Input Handling**
   to **Both** or **Input Manager (Old)**. Restart the Editor if requested. The
   sample uses the built-in input axes and requires no Input System package.
4. Open the imported `StealthMaze.unity` scene and press Play. Click the Game view
   to capture the cursor. The scene builds its maze, characters, camera and light
   at runtime; no NavMesh bake, models or external services are needed. Geometry
   appears when you press Play, rather than in the scene's edit-time preview.

WASD moves, mouse looks, hold Left Ctrl or C to crouch, Esc releases the cursor,
and R restarts. Teal lintels mark low passages; only the crouched player fits.
Amber crates provide low cover: crouch behind them to break line of sight.
Walking makes louder footsteps than crouching. The HUD shows each guard's goal
and shout feedback. Blue guards patrol, amber guards investigate, red guards see
you. Shout feedback is displayed as text; recorded voices/audio assets are not
required.

The default seed is 42. Ordinary corridors always connect entry to exit; crawl
shortcuts are optional. Enemies use grid BFS and full-height capsules, never the
crawl edges. This avoids requiring Unity's navigation authoring package. Patrol
points are distributed through the maze, outside the starting area. Guards do
not collide with one another so opposite patrols cannot deadlock in corridors.

## Tune and extend

Select **Stealth Maze Demo** before Play to configure seed, enemy count, sight
range, field of view, sight sensitivity, hearing range, hearing sensitivity and
shout range. These defaults are copied onto spawned guard components. During
Play, select a guard to tune its `MazeSight`, `MazeHearing` and `MazeGuard`
components. Setting a sense's sensitivity to zero or disabling the component
turns that sense off. `MazeSight.recognitionSeconds` controls recognition delay;
higher sensitivity shortens it. Hearing strength falls with distance, is capped
by listener range, and scales with source radius and listener sensitivity.
Player walking/crouching speeds and noise radii are on `MazePlayer`.

Sight tests the player's current head height with a physics ray, so walls,
crates and lintels occlude it. Pursuit then uses a copied observed position:
losing sight never grants the guard access to the unseen player's position.
Footsteps supply an anonymous location. Shouts supply the original sighting ID,
position and time, heard from the shouting guard's position. Only direct vision
causes shouts; listeners do not rebroadcast. Listeners converge on the captured
location, dwell and scan, mark the search complete, then resume patrol. Expired
reports cannot refresh memory. The default auditory model is radial and can
carry through walls; it does not simulate room/portal acoustics.

To add a sense (for example a pressure plate), subclass `MazeSense`, implement
`Observe`, and attach it before `MazeGuard.Initialize`. Return a bounded copied
`MazeObservation`. Guard execution remains the only enemy movement owner. This
sample uses the package's beliefs and utility decisions with a small grid motor;
it does not exercise GOAP combat, weapons, orders or language inference.

The bootstrap chooses Standard, URP Lit or HDRP Lit for the active render pipeline. The
intended baseline is a Unity 6 Built-in or URP project. HDRP rendering has not
been validated. For a standalone build, add the imported scene to Build Profiles
and retain the selected Lit shader in Graphics → Always Included Shaders (the
sample looks up its material shader at runtime).

## Validation

Headless `StealthMazeTests` verify maze connectivity, bounded navigation,
player-only shortcuts, repeatable seeds and hearing falloff/range/sensitivity.
These do not compile Unity scripts or establish playability.

With Unity Test Framework installed, the imported sample's Play Mode tests cover
bootstrap geometry, local hearing/shout boundaries and capture resets. Run them
in **Window → General → Test Runner → PlayMode**. Then manually check:

- Walk the ordinary route to the green exit; confirm win and R restart.
- Walk into a guard's view; confirm recognition, red state and pursuit.
- Break line of sight behind a wall; confirm it searches the last sighting,
  rather than following you through walls, then resumes patrol.
- Walk near an occluded guard; confirm footstep investigation. Crouch at the
  same distance; confirm the reduced noise radius changes its response.
- Have a guard see you within another guard's hearing range; confirm the second
  converges on the reported location. Repeat outside range and with hearing
  sensitivity zero; confirm it receives no report.
- Crouch behind a crate and use a teal passage. Confirm guards cannot enter,
  and releasing crouch beneath a lintel does not stand into the ceiling.
- Touch a guard while moving and while stationary; confirm one capture/reset,
  cleared memories, entry spawn and the brief spawn grace period.

Unity is not installed in the development sandbox. Unity compilation, these
Play Mode tests, rendering and a full maze playthrough must be checked in the
host project before treating the sample as validated.
