# Deep AutoCAD optimization roadmap — source cross-check

Current source: **v1.0.41**. The uploaded roadmap describes an older source layout (`iSt-CAD/MainWindow.xaml.cs`) than this project (`iSt-CAD-WPF`), so its observations were checked against the present implementation before applying changes. This note records implemented mappings and the remaining Windows acceptance work; it does not claim measured gains.

| Roadmap recommendation | Current source status |
|---|---|
| Shared document scene, mapped entities and extents | `DrawingSession` owns a `CadEditor`/`CadDrawing`, revision, bounds-backed `CadSpatialIndex`, geometry cache and selection IDs. `Zoom Extents` uses cached index extents; open builds the new session before activating it. This is the equivalent scene boundary in the current architecture. |
| Correct world/screen OSNAP | Pointer input is converted through `ScreenToWorld`; snap tolerance is expressed in drawing units (`10 / _viewScale`), and the returned point is drawn through `WorldToScreen`. No screen/world comparison was found in this path. |
| Nearby OSNAP candidates | Snap endpoints and centers are bucketed in a compact spatial hash; intersection candidates come from the R-tree around the cursor. Intersection queries append only line/polyline segments whose bounds overlap the cursor tolerance, avoid LINQ/per-entity iterators, compare squared distances, and reuse a static all-modes set. R-tree bulk loading uses fewer materialized intermediates. |
| Selection by entity ID and bounds | Pick/window/crossing selection query the R-tree and track stable entity IDs; selection highlight is drawn on a separate overlay. |
| Style-aware render batches | Viewport composition retains each entity and selects pens by effective color and linetype pattern; cached geometry is separate from compositing. Geometry is frozen and composed into a frozen `DrawingImage`. |
| Text and grid | Text geometry is built from `FormattedText` and cached per entity/LOD. Grid output is composed into a cached drawing image keyed by viewport/grid state instead of creating one WPF `Line` per grid segment. |
| Viewport culling, LOD, cancellation | R-tree culls outside viewport, LOD selects geometry detail, background build/composition is cancelable, and zoom/pan previews use transforms. |
| Measurements | `PERF` and `BENCH` report open stages, render, cache, interaction, selection, OSNAP, CPU and RAM. No reliable baseline can be recorded here without Windows/.NET and representative DWG/DXF files. |

## Remaining acceptance work

Run Release build on Windows, then use 3–5 permitted drawings (small, medium, large, text/block/hatch/spline heavy). Repeat each scenario at least five times and compare medians. Verify open failure and rapid file switching, world/screen OSNAP under several zoom/pan transforms, click/window/crossing selection, render fidelity against AutoCAD, long-wheel responsiveness, and repeated open/close memory. Record machine, display scale, file/entity counts, timings and RAM. Consider another renderer only if those measurements show WPF composition remains the bottleneck.
