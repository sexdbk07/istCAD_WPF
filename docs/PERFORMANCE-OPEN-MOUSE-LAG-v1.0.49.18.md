# Open and pointer responsiveness — v1.0.49.18

## Source finding

The idle `DrawingCanvas_MouseMove` handler in `MainWindow.xaml.cs` only stores `_lastPointer` and returns unless a CAD command is active. It does not call `FindEntityAt`, enumerate the drawing, or create entities during ordinary pointer movement.

A heavier task was automatically scheduled at first viewport completion: `MarkInitialViewportRendered` queued `QueueSnapCandidateBuild` at `ApplicationIdle`. `CadSpatialIndex.BuildSnapCandidates` then traversed all indexed objects and materialized endpoint/midpoint OSNAP records for every polyline. This ran on a worker, but on large drawings it could still saturate CPU and allocate a large grid while the UI was processing pointer events.

The DWG adapter also allowed generous preview budgets (up to 1.5M nested entities / 2M points drawing-wide), and repeated INSERTs could rescan the same dynamic visibility tree.

## Changes

- OSNAP candidate preparation now starts only when a draw/edit command becomes active. Opening a drawing and moving the pointer no longer triggers a full-drawing OSNAP pass.
- The global snap cache is capped at 180,000 snap candidates. Polylines with more than 12,000 vertices are skipped by the global snap cache to avoid a pathological O(vertex-count) allocation burst.
- Per-INSERT preview limits are reduced to 12,000 nested entities, 8,000 points and 3,000 paths. Drawing-wide temporary preview budgets are reduced to 350,000 entities, 450,000 points, 40,000 paths, 20,000 texts and 100,000 proxy items.
- Dynamic visibility tree checks are cached by BlockRecord and inherited layer, with a 50,000-entity scan ceiling. Cycles, depth overflow and incomplete scans conservatively suppress detailed preview and retain the INSERT bounds.

## Expected behavior and trade-offs

- Idle pointer movement after file open should no longer compete with an eagerly scheduled full-document OSNAP build.
- OSNAP preparation is deferred until a drawing/editing command starts. Snaps may not become available until the bounded background build completes.
- Very large polylines are not represented in the global snap cache, and very large INSERT previews may be partial or bounds-only. This affects temporary preview/snap assistance only; the native DWG entities are preserved.

## Verification

Static source inspection and ZIP integrity checks only. Build and Windows/WPF runtime testing, including measurement on the user's DWG, remain required.
