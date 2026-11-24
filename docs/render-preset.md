# Fixed timelapse render preset

The active recorder uses one preset for new exports and re-exports. It is built
from the existing `RenderTimelinePlanner` and
`MediaFoundationSessionRenderer`; it does not discard retained source slots to
fit a requested duration.

New recordings receive an automatic local-time name in the form
`Recording yyyy-MM-dd HH-mm-ss`. Saving does not require the user to enter a
title. Existing library entries can still be renamed from the library.

## Preset

- Output frame rate: **30 fps**.
- Normal output: one output frame for each retained semantic timeline slot, in
  slot order. Duplicate samples remain duplicate frames so their time positions
  do not shift.
- Highlight holds: included highlights add
  `round-half-up(holdTicks × 30 / ticksPerSecond)` frames. Excluded highlights
  add no frames. The renderer uses the captured highlight assets when present;
  otherwise it uses the latest slot at or before the highlight, subject to the
  same gap handling as normal frames.
- Pauses do not add normal frames. Highlight event placement uses active time,
  so time spent paused is not treated as elapsed capture time.
- Output duration is the planned output frame count divided by 30. For an
  empty render plan, the existing renderer writes one black frame so the MP4
  can be finalized; its planned duration is therefore `1/30` second. The
  container and player may round the encoded duration slightly, so the
  completed file remains the authority for its actual duration.
- Every semantic slot emits an output frame. If a slot has no usable source,
  the renderer holds the last composed image; before any source has been
  composed, the image is black. A missing camera image does not erase a valid
  screen image, and vice versa.
- Screen-only sessions render the display full-frame. Camera-only sessions
  render the camera full-frame. Dual-source sessions, including old recordings
  with saved editor choices, use display full-frame with the camera mirrored in
  a bottom-right picture-in-picture at 28% of output width and 4% inset. The
  camera and screen source tracks remain independent.
- The elapsed timer overlay stays enabled at the existing 75% opacity. The
  watermark stays off. Neither setting is configurable in the active app.

## Planned duration examples

| Render input | Planned output frames | Planned duration |
| --- | ---: | ---: |
| Empty input | 1 black frame | 1/30 s (about 0.033 s) |
| One retained slot | 1 | 1/30 s |
| 3,600 one-second slots + one 2-second included highlight hold | 3,660 | 122 s |

These are renderer-planning examples, not measured hardware export timings or a
promise that an MP4 container reports an identical rounded duration.


## Core API boundary

In the current saved app-source snapshot, both library re-export and new-capture export create settings through `CreateRenderSettings`, which delegates to `RenderPreset.CreateSettings`. The Core `SessionExportRequest` still accepts its 1–240 fps engine-facing range; that range is not an active app preference or editor control.

The card 01 shell edits remain in progress, so recheck the binding after those edits are saved and verify the full app build before treating this snapshot as final.
