# Agent perspective debugging

Add `PerspectiveDebug` to an agent root to observe its senses during Play Mode.
It is optional: inactive components do not create cameras or audio voices and do
not request extra AI sensing ticks. Camera/voice resources are created lazily on
first observation or `ViewCamera` access. The framework's `PerceptionSensor` implements
`IAgentPerspectiveSource` and is discovered automatically on the same object.
Assign its eye pivot above the actor's own collider as in the normal sensor setup.

Use **Observe this agent** from the component's context menu, call
`BeginObserving()`, or bind it to an observer UI. `ViewCamera` exposes the owned
camera; `BeginObserving(renderTexture)` renders into a caller-owned texture.
`StopObserving()` returns to the host view. The sample's Observer role has Guard
buttons and an Overhead button using these same APIs.

## Sight

The camera follows the source's actual eye position and direction. Its viewport
fits inside the source's vision cone, uses its range and visibility layers, and
blacks out when vision is unavailable. Spherical layer culling also limits range.
This is a conservative forward geometric eye view, not a panoramic view or a
visualization of remembered/recognized contacts. A single camera cannot display
a complete 360-degree sense at once. Recognition delay, ray budgets and belief
aging remain owned by the sensor/AI and are not bypassed by the debug view.

By default the agent's own renderers are hidden only while this camera renders,
so its head/visor does not obstruct the view. Renderer state is restored after
the camera pass and on teardown. The hooks support Built-in and SRP camera passes;
the standalone sample is validated with Built-in rendering.

## Hearing

The debug component subscribes to the selected source's **accepted hearing
events**, never to the raw world sound bus. The sensor remains responsible for
range, occlusion, sensitivity and other auditory rules. Supply a clip with the
same game event that publishes a sound:

```csharp
SoundBus.Publish(position, audibleRadius, Time.timeAsDouble, audioClip,
    volume: .8f, pitch: 1);
```

`PerceptionSensor.Heard` emits `PerceivedSound` only when its hearing rules
accept that emission. Playback uses that same clip, pitch and perceived gain,
with stereo panning from the heard origin. Events without clips can still supply
AI evidence, but do not synthesize missing audio. `WeaponAdapter` exposes an
optional shot clip; the sample supplies short procedural footstep/shout cues.
The host still owns normal world audio playback.

Playback uses a bounded voice pool (eight by default), deduplicates recent sound
IDs, and drops stale events. Preserve the sound ID when forwarding an existing
`SoundBus.Sound` instead of manufacturing a new emission. Timestamps use Unity
time by default; assign `PerspectiveDebug.Clock` if a custom source uses a
different `IClock`. Publish Unity sensor events on the main thread.

## Observer ownership and cleanup

Only one `PerspectiveDebug` owns the audible observer view at a time. Selecting
another agent stops the previous view. The component temporarily disables other
enabled audio listeners and restores them when observation ends. With **Isolate
Audio** enabled (default), normal world sources are paused using
`AudioListener.pause`; debug voices bypass that pause. This prevents ordinary
world playback from leaking sounds that the selected agent did not hear.
Sources explicitly configured to ignore listener pause remain a host concern.
Disable this option when the game's mixer already isolates observer playback.
The prior pause value is restored on stop, disable, destruction or switching.
Coordinate this observation window with the host's own audio/pause manager.

The component owns only its camera/listener/voice objects, not caller-provided
render textures, sensor clips or the agent's simulation. Disabling/destroying it
unsubscribes events and restores observer state. A debug playback subscriber
failure stops observation without interrupting the agent's hearing pipeline.

## Custom sensors

Implement `IAgentPerspectiveSource` on a component exposing:

- `Eye`, `VisionAvailable`, `VisionRange`, `VisionFieldOfView`, and `VisionMask`.
- `Heard`, an event carrying a `PerceivedSound` after the real sensor accepts it.

Bind that component with `PerspectiveDebug.Bind(component)`. Separate sight and
hearing implementations can expose one small composite source, as the sample's
`MazeGuard` does. Emit the actual sound origin, not a remembered target position;
a shout about an unseen target is heard at the speaker. The sample uses its
existing sight/hearing checks, so observation does not grant knowledge to guards.
