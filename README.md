# iStCAD_1

Source version: **v1.0.25**.

WPF CAD application foundation for Windows, organized into Core, Application, Infrastructure, Rendering and WPF projects. The ACadSharp library source is vendored under `vendor/ACadSharp` and referenced as a local project; the application does not call an external web API.

## Solution projects

- `iStCAD_1-WPF`: WPF shell, drawing surface, command line, dynamic input and native dialogs.
- `iStCAD_1.Core`: independent drawing model and 2D geometry types.
- `iStCAD_1.Application`: editor interaction state, dynamic input parser, CAD document loading coordinator, and R-tree/snap indexing.
- `iStCAD_1.Infras`: compiles the checked-in ACadSharp, CSMath and CSUtilities source directly into the infrastructure assembly. No separate ACadSharp project or NuGet package is required. See the upstream MIT licenses and `vendor/ACadSharp/UPSTREAM.md`.
- `iStCAD_1.Rendering`: WPF geometry conversion and a bounded least-recently-used geometry cache, isolated from commands and file parsing.

## Current CAD workflow

- `LINE` / `L` / `PLINE` / `PL`: click a start point and then one or more endpoints (currently stored as connected line entities). Compact dynamic input follows the pointer only while a drawing command is active; type `X,Y`, relative `@dX,dY`, a polar value such as `100<45`, or a distance along the current cursor direction. Press Enter or Space to accept a typed point; Esc ends the command.
- `CIRCLE` / `C`: specify a center and radius by mouse or dynamic input.
- `RECTANGLE` / `REC`: specify two opposite corners; creates a closed lightweight polyline.
- `ARC` / `A`: specify center, start and end points; creates a counter-clockwise arc.
- Click selects a model-space object; Shift-click adds/removes. `SELECT` / `S` enters selection help, `ERASE` / `E` or Delete removes selected objects, and `MOVE` / `M` or `COPY` / `CO` asks for base and destination points.
- Scroll the mouse wheel to zoom around the pointer. Hold the middle mouse button and drag to pan. Use the bottom `+`, `−`, `Ext.` buttons or the slider; `ZE`/`ZOOM E` fits all entities and `ZP` restores the previous view.
- Left-to-right drag selects fully enclosed entities; right-to-left drag selects entities crossing the box. Shift-drag adds to the selection. Esc cancels the active command. Ctrl+1 toggles Properties. Ctrl+N/O/S and Ctrl+Shift+S create, open, save and save as. `REGEN` redraws the view.
- File open/save, selection, drafting toggles, zoom and editing outcomes are reported in the command-line status area and expandable command history.
- Command line autocomplete filters the supported commands and aliases as you type, shows a short tooltip/help description, accepts with Tab, and runs with Enter or Space; Space on an empty command line repeats the last command. Arrow keys move through suggestions.
- `UNDO` / `U`, `REDO` / `Y`: restore editor snapshots.
- `LAYOUT` or the `+` tab button creates a paper layout. Pick a layout tab and run `PLOT` / `PDF` to export A4/A3 portrait or landscape in color or monochrome. Layout paper size and plot style are stored in the ACadSharp layout settings when the drawing is saved.
- Open and save DWG/DXF through the vendored ACadSharp reader/writer.

PDF export creates a single centered, fit-to-page vector sheet from model-space geometry. Recognized primitives export as vectors; other ACadSharp entities that have finite bounds export as bounds previews. Color output assigns a stable preview color per layer; monochrome output uses black. Full paper-space viewport composition, native CAD layer colors, CTB/STB lineweight interpretation, multi-sheet plotting, and AutoCAD's complete plot configuration are not implemented yet.

Viewport performance uses a packed R-tree for visible-entity queries and hit testing plus a compact hash grid for endpoint snaps. Rendering queries only the current viewport, avoids sorting draw candidates, keeps up to 90,000 recently-used frozen geometries, cancels geometry work when the viewport changes, and reuses the finished drawing image while the viewport and drawing are unchanged. Document decoding and the initial index build run through an Application-layer coordinator off the UI thread. Later index rebuilds run in the background for drawings with at least 5,000 entities; wheel zoom, zoom-slider redraws and cursor previews are coalesced. Pan temporarily translates the cached image. Undo/redo history is bounded by both snapshot count and entity-reference budget to limit memory use. Type `PERF` in the command line to see entity counts, visible candidates, geometry-cache hits, last render/open/index duration, process CPU and working-set RAM. Opening a drawing also reports elapsed wall time, process CPU time and RAM change.

Dirty drawings are autosaved every two minutes to `%LocalAppData%\iStCAD_1\Recovery`; closing the application writes a final recovery copy. The next launch offers to restore available copies as drawing tabs. A successful manual save removes that drawing's recovery copy. New DWG files use AC1027 (AutoCAD 2013 format) for broader version compatibility. DWG/DXF saves write to a sibling temporary file, reopen it with ACadSharp, check the model-space entity count, then replace the destination only after validation passes. This protects the existing destination from failed or truncated writes; ACadSharp re-reading a file is a structural check and is not a substitute for opening it in AutoCAD.

The ACadSharp reader scans every layout-associated block record and reports per-space counts, while preserving the complete native document for saving. Model and paper-layout entities are shown when their tabs are selected. LINE, CIRCLE, ARC and lightweight POLYLINE are editable; many additional types (including text, spline, ellipse, dimensions, hatch boundaries and block insert graphics) receive geometry previews, while other objects use ACadSharp proxy graphics or bounds when available. Preview does not mean full editing or exact AutoCAD rendering: custom objects, some 3D graphics, paper-space viewport composition and block-definition editing remain limited by the current adapter. The view uses batched WPF drawing groups, a separate cursor-preview layer, viewport culling and transform-based panning. Trim, full layer/property editing, and very large drawing spatial indexing remain future work. Custom or unknown DWG/DXF entities retain the limitations of the vendored ACadSharp reader/writer.

## Build

Open `iStCAD_1.sln` (the solution file, not an individual `.csproj`) in Visual Studio 2022 with the .NET 8 desktop workload. ACadSharp and its shared utilities are compiled directly with the infrastructure project from checked-in source, avoiding a nested project reference and external ACadSharp package. The WPF project targets `net8.0-windows`. Build outputs are placed under `_Build/bin` beside the solution, separated by project and configuration; intermediate files stay in each project's standard `obj` folder. If Visual Studio still shows stale project information, close the solution, delete the `.vs` folder next to it, then run `dotnet restore .\iStCAD_1.sln` from this folder and reopen it.
