# iSt-CAD v1.0.44 — stage 2 validation

UI-FREEZE: no XAML layouts, colors, controls, keyboard bindings or user command semantics changed.

## Implemented (first-party only)
- `native-wpf` (default) and `lightweight-wpf` (experimental) composition paths selectable by `CadViewport.TrySetBackend(...)`.
- Lightweight path omits the transparent drawing-node layer but keeps the same geometry, pen colors, draw order and clip. This is an internal WPF prototype, not Tida.CAD itself.
- Backend changes cancel an in-flight geometry build and invalidate the image cache; the main window triggers a fresh render through the original document and spatial index.
- Completed-frame duration measurements and explicit upstream integration status are available to diagnostics.

## Still blocked
- Neither actual ProCad assemblies nor actual Tida.CAD assemblies are loaded; their adapters remain unavailable.
- Neither renderer benchmark nor Windows WPF build was executed in the Linux workspace (dotnet SDK unavailable).
- Performance results must be measured on the target Windows machine: open the same 10k/100k entity drawings, check click/window/crossing/snap/zoom/pan and compare frame times, UI responsiveness and peak memory. Never deploy experimental mode without identical visual and interaction results.

## Developer diagnostics (no UI changes)
- `CadViewport.TrySetBackend("lightweight-wpf", out var error)` switches to the experimental compositor.
- `CadViewport.TrySetBackend("native-wpf", out var error)` restores baseline.
- `CadViewport.Performance.Snapshot()` yields accepted frame count and mean total async-build duration, not FPS.
- `ViewportBackendEvaluation.All` reports exact capability gates.

Upstream source licenses and API requirements must be checked before source-level merging. Keep baseline unchanged.
