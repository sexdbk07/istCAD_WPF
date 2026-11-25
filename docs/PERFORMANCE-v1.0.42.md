# v1.0.42 — viewport responsiveness

Changes:
- A single background worker now owns viewport R-tree queries, draw-order sorting, geometry cache lookup/build and frame composition, including small drawings.
- A cancelled frame retains completed frozen geometry in the bounded, synchronized LRU cache. Geometry construction and cost estimation occur outside the cache lock.
- Spatial index membership is snapshotted to prevent concurrent edits from changing indexed positions. R-tree traversal supports cancellation; sorting checks cancellation before and after sorting.
- Stale frames are rejected on camera, revision, session or spatial-index changes. A tab switch while a worker is busy retries the current session.
- Pan transform updates are coalesced to compositor frames. Wheel-to-pan preserves the preview transform. Worker completion cannot reset a live pan.
- Window close cancels viewport work; no completion schedules another viewport render.
- Single-click selection reuses its initial hit-test result instead of performing the same query twice.
- Source/fallback version v1.0.42; build counter 41 produces 1.0.42 on next build.

Validation performed here:
- XAML and project XML parsing; worker call-site/obsolete-worker checks.
- Arithmetic checks for pointer-anchored zoom and combined zoom/pan translations.
- Patch reconstruction validation against the v1.0.41 source snapshot.

Not validated: C# compilation, WPF execution, FPS, RAM, CPU, DWG fidelity. No dotnet/Windows runtime is installed here. These changes are not a guarantee against every hang.

Windows acceptance checks:
1. Build iStCAD_1.sln; open small and large DWGs. Record PERF before/after on the same machine and drawing.
2. Wheel continuously for 30 seconds in both directions, including extreme scales; check grid and latest-frame recovery when stopping.
3. Wheel then immediately middle-drag; release, wheel again, double-click middle for extents. Check pointer anchor and no old-frame jumps.
4. Switch tabs and modify/delete entities while a frame is building; confirm latest session/revision appears.
5. Select single entities and large windows; verify selection and grips. Test Ctrl+1 and DYN during background work.
6. Close while rendering; confirm process exits and no new viewport task starts.

Remaining work: expensive individual geometry builders do not yet support mid-entity cancellation; large selection overlays and dirty-region calculations still run on UI; draw-order sorting cannot be interrupted mid-sort; bitmap preview outside its safe transform range is hidden until a fresh frame arrives. Native/display fidelity and GPU viewport remain separate workstreams.

## Build automation added — 2026-10-29
- Added `build-windows.ps1` to publish a self-contained Windows x64 single-file app and package the published output.
- Added `.github/workflows/build-windows.yml` for Windows CI artifact generation on matching source changes or manual dispatch.
- The workflow is included in this source ZIP; it has not been executed from this environment.
