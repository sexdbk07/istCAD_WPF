# ProCad primary / Tida.CAD fallback — iSt-CAD v1.0.42

Request date: 2026-10-29. Implementation: stage 1 (UI-preserving backend boundary).

## Guardrails
- MainWindow.xaml, CadViewportControl.xaml, command aliases, palette, input overlays and visual properties are **unchanged**.
- Current WPF renderer remains the only enabled backend. `TrySetBackend` rejects unavailable third-party adapters without interrupting the active renderer.
- Single canonical model, R-tree, LRU and undo service stay with iSt-CAD. No external second document or duplicate selection state.
- `ViewportPerformanceCounter` provides non-invasive stopwatch timings for future A/B tests. It must be used around an existing completed frame scope, not around mouse-move callbacks.

## Candidate review
- ProCad: primary candidate for isolated engine and feature study. Avalonia rendering controls are NOT directly WPF-compatible. Review API/version/build/license and use an adapter or port selectively. Do not add NuGet/project dependency until proven compatible.
- Tida.CAD: fallback for lightweight WPF viewport. Check source license, render control lifecycle and unit mapping. Keep existing document/undo/snap services.
- OpenCad2D: separate experiment, not included by this feature (GPL-3.0 license).

## Current delivery
Added `IViewportBackend`, `ViewportBackendRegistry`, `NativeWpfViewportBackend`, `ViewportPerformanceCounter` in Rendering and added a fail-safe backend selection method to CadViewportControl code-behind. No UI layout changes, no third-party copying, no behavior changes to existing drawing.

## Next validation on Windows
1. `dotnet build iStCAD_1.sln -c Release` and launch the app.
2. Open a large DWG, pan/zoom, verify snap, selection, command HUD, undo and save.
3. Add timing scopes to existing frame completion, measure p50/p95 frame time, RAM and input latency with identical models.
4. Only after passing legal/technical review implement ProCad adapter on an isolated branch. Activate Tida.CAD only if the main candidate underperforms.

Build status: not verified (no .NET SDK or Windows WPF runtime in packaging environment).
