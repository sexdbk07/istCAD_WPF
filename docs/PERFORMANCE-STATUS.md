# Performance plan implementation status

Current source version: **v1.0.41**. This report tracks `PERFORMANCE_OPTIMIZATION_PLAN.md` against the current source. It distinguishes implemented mechanisms from measured outcomes: this workspace has no Windows CAD sample set or .NET build toolchain, so no performance gain or regression-free build is claimed here.

## Implemented mechanisms

| Plan area | Current implementation | Remaining verification |
|---|---|---|
| Startup | Main window records time to the first rendered frame. Recovery scanning/prompt and command-usage JSON loading are deferred until after the initial window render; the latter runs on a worker thread. | Compare cold and warm startup on Windows. |
| File open / native database | ACadSharp read and bounds/R-tree preparation run off the UI thread. Snap-candidate generation is deferred until the first viewport image is ready and then runs in the background. The active drawing is only replaced after read and bounds index succeed. A per-document lock serializes native save with layout and layer-table access. | Check small, medium and large DWG/DXF and malformed files. OSNAP is unavailable briefly while its candidate index is prepared. |
| Open cancellation | Latest selected file wins. A stale result is discarded; ACadSharp document reads are serialized, and cancellation is checked after synchronous ACadSharp read, throughout mapping/database inventory/index preparation, and before UI activation. | ACadSharp's synchronous `Read()` itself cannot be interrupted; closing the app can still leave the current read worker running until the reader returns. |
| Rendering / spatial queries | Viewport-only R-tree query, per-entity frozen geometry cache with entry and estimated-complexity bounds, LOD, background geometry/composite work, cached grid image, selection overlay and pan/zoom preview transforms. Wheel zoom uses a cached bitmap transform during the gesture, cancels stale geometry/composite work once per gesture, skips pointer-preview work during wheel input, and commits after a 140 ms idle window. It composes a frame from cached and uncached visible geometry so cache eviction cannot cause repeated rebuilds. Small selection changes update only their dirty rectangle while preserving the old overlay outside it; the incremental chain is periodically flattened by a full redraw. Entity query order sorts in place, polyline bounds use one vertex pass, and Zoom Extents uses cached bounds. | Compare frame time and image fidelity at several scales; confirm the selection overlay has no artifacts. |
| Interaction | Spatial queries are used for pick/selection and OSNAP candidates; hidden entities are excluded from index candidates; large-document index rebuild is debounced and asynchronous. PERF separates bounds/R-tree build, OSNAP candidate build and OSNAP query latency. | Measure hover, crossing/window selection, OSNAP and dynamic input latency. |
| Resource lifetime | Geometry cache is cleared on drawing switch, pruned after edits, and bounded by both entry count and a geometry-complexity estimate. Spatial indexes are attached to per-drawing sessions. | The cache estimate is a guardrail, not a byte-accurate memory measurement; validate repeated opens on Windows. |
| Diagnostics | `PERF` reports startup, open stage timings, CPU/RAM, viewport/render, cache, interaction, bounds/R-tree, snap-index build and snap-query metrics. `BENCH` repeats viewport query, picking and OSNAP workloads. | Capture results from a fixed machine and test corpus before/after each optimization. |

## Measurement procedure

Use the same Windows machine, build configuration, display scale and sample files for each run. Record at least one small, one medium and one large drawing; include a file with nested inserts, dense hatch/text and dimensions. Restart iSt-CAD between cold-start runs. After opening a file, wait for background index/render work to settle, then run `PERF` and `BENCH`. Repeat each run three times and record the median. `process-to-window first frame` uses the OS process start time; `window creation-to-frame` measures the WPF window's own startup. `drawing first viewport` starts after decoding and bounds-index construction, when the new session activates. `total open` includes time waiting for the serialized ACadSharp read slot.

| Version | Machine / build | Drawing and format | Native objects | Startup ms | Read ms | Map + inventory ms | Bounds/R-tree ms | Snap build ms | Total open ms | First viewport ms | Snap query p95 ms | BENCH query ms | BENCH pick ms | BENCH snap ms | Working set MB |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| v1.0.41 | Not measured in this environment | — | — | — | — | — | — | — | — | — | — | — | — | — | — |

`BENCH` covers spatial query, object picking and OSNAP lookup. It is not a substitute for frame-time profiling or pixel comparison against AutoCAD. Approximate open-to-first-drawing time by adding `total open` and `drawing first viewport`; snap-index time may continue after the first viewport. Use `PERF` and screenshots alongside this table when evaluating renderer changes.

## Remaining plan work

1. Run Release build and the drawing corpus on Windows; capture real baseline values. This environment has no .NET SDK, Windows WPF runtime, or DWG/DXF sample corpus, so this acceptance item cannot be completed here.
2. Verify DWG/DXF open failure preserves the current tab and file switching cannot install stale results.
3. Profile mapping versus ACadSharp read on representative drawings; optimize only the measured dominant stage.
4. Compare rendered output and interaction latency for text, nested blocks, hatch, dimensions, linetypes and layer visibility.
5. Test repeated open/close memory behavior and cancel/close behavior under a large-file read.
6. Add automated regression fixtures and CI only after a redistributable test corpus and Windows runner are available.


## v1.0.41 wheel/grid correction

Wheel input updates camera center/scale immediately instead of queuing every wheel event for replay. The pointer anchor is preserved; refinement is debounced. Grid redraw is coalesced at display-frame cadence and runs independently from the geometry preview transform so zoom-out cannot expose empty grid regions while geometry workers run. Grid iteration uses bounded screen-space counts (at most 2048 lines per axis), double precision phase and finite-number checks instead of world-coordinate integer loop indices that can overflow during deep zoom. These are source changes, not measured Windows results.

## v1.0.41 deep-zoom safety

Source changes: camera wheel/button zoom limits no longer depend on fit scale (numeric range 1e-12 to 1e12). Preview transforms sent to WPF are bounded to a ratio between 1/64 and 64 with finite offset checks. Beyond this relative range the old geometry preview is temporarily hidden while the camera and grid continue updating; a completed refinement restores it. Dash cycles below one screen pixel render continuously, preserving native linetype data; dash detail returns at resolvable scales. Proxy/selection fallback dash units were corrected to WPF pen-thickness multiples. A numerical check exercised 20,000 wheel updates and subpixel dash thresholds; no WPF benchmark or freeze-free claim is made without Windows testing.

## v1.0.41 analytic curves and readable dialogs

ARC and bulged POLYLINE geometry now use WPF arc segments instead of fixed-count sampled polylines. Full circles use EllipseGeometry. Curves keep their analytical radius/sweep through zoom, and line/circle/arc/polyline cache entries are reused across LOD levels. FitView cancels pending wheel work and uses the same numeric scale range as wheel zoom. Plot and Text/MText dialogs have independent light resources; text editors scroll when fields exceed available height. Bulge radius/sweep arithmetic and XAML XML were checked. Compilation, WPF screenshots, CAD fidelity and runtime performance still require Windows verification. This does not expand support for unsupported native entity types.

## v1.0.41 picking and hints

Click/window-selection redraws the selection overlay directly. Empty selection clears the overlay without a viewport query. Highlight and Properties look up selected IDs through the spatial index instead of scanning all visible/all document entities; hidden Properties are no longer rebuilt on selection. Polyline hit testing computes straight/bulged-segment distance analytically instead of tessellating/allocating screen-point arrays for each pick. Pick tolerance is 11 DIPs, converted through the current scale. Command hints use Bottom placement under both input fields, subject to WPF screen-edge popup fitting. Analytic curve radius checks and XAML XML passed; no Windows build, latency benchmark or GUI verification was possible.
