# Local diagnostics

Diagnostics stay on the machine. log does not upload session metadata, source
frames, crash data, or telemetry.

For a session under `%LOCALAPPDATA%\log\sessions\<id>`, the useful evidence is:

- `manifest.json`: committed state, independent track counts, adaptive capture
  cadence, retained semantic targets, gaps, capture metrics, pruned-track
  state, and the last successful export settings. For a long adaptive run,
  compare adjacent retained `targetTicks` values; they must be no more than
  60 seconds apart.
- `events.jsonl`: the checksummed local journal used to replay slots, pauses,
  highlights, finalized segments, adaptive compaction, and raw-track pruning.
- `segments` recorded under each track: finalized MP4 recovery evidence; the
  renderer still uses the durable sampled source model.
- the catalog database: library ordering, deletion state, highlights, and
  render-job status.

The `log.Capture` ETW provider adds local, payload-free lifecycle events for
capture scheduling, source availability, encoder selection, export status, and
recovery status. It is intended for focused troubleshooting with Windows
Performance Recorder/Analyzer or an ETW listener; it is not a product telemetry
channel.

The library also shows a compact per-session diagnostic summary: retained sample
count, gap count when nonzero, and local storage size. Recovery issues are
reported as a count in the library status and do not prevent healthy cards from
being listed when an incomplete catalog row is encountered.

When reporting a manual issue, record the session id, packaging mode, Windows
build, shell state, and the relevant manifest/journal status. Do not attach raw
screen or camera assets unless they are intentionally part of the test.
