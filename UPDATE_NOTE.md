# UPDATE NOTE — 2026-10-29

## iStCAD_1 v1.0.42
- Applied `iStCAD_1-v1.0.42.patch` to the v1.0.41 source archive.
- Viewport rendering now routes visible-entity query, geometry cache/build, and frame composition through one cancellable background worker.
- Added synchronization around the geometry LRU cache and snapshot membership for the spatial index.
- Improved pan/wheel-preview handling, stale-frame rejection, tab-switch recovery, and viewport-worker cancellation on window close.
- Single-click selection reuses the first hit-test result.
- Added `docs/PERFORMANCE-v1.0.42.md` with changes, known limitations, and Windows acceptance checks.

## Validation status
- Patch applied cleanly.
- Project `.csproj` and XAML XML files parsed successfully.
- C# compilation and WPF runtime tests were not run: the current environment has no `dotnet`/Windows WPF runtime.
- Do not treat this archive as a verified executable build until it is built and tested on Windows.

- Added `build-windows.ps1` for reproducible self-contained Windows x64 publishing and ZIP packaging.
- Added a Windows GitHub Actions workflow to produce the EXE package artifact.
- Build remains unverified here because this environment has no .NET SDK or Windows WPF runtime.

## UN-2026-10-29-001 — ProCad primary, Tida.CAD fallback with UI freeze
- **Request date:** 2026-10-29
- **Request:** Keep iSt-CAD v1.0.42 UI unchanged; evaluate ProCad engine and Tida.CAD WPF viewport fallback.
- **Status:** Stage 1 completed in source; external engine integration pending; Windows build validation pending.
- **Update date:** 2026-10-29
- **Implementation:** Added backend interface/registry and fail-safe native WPF selection; optional frame counter; detailed adapter gates in `docs/PROCAD-TIDA-UI-FREEZE.md`. No external third-party code is copied.

## UN-2026-10-29-ProCad-Tida-stage2
- **Request date:** 2026-10-29
- **Request:** Complete missing ProCad/Tida viewport integration while freezing current iSt-CAD UI.
- **Status:** Partially completed; external upstream engines and Windows validation pending.
- **Implementation date:** 2026-10-29
- **Implemented:** Selectable lightweight-WPF compositor backend, backend evaluation gate, accepted-frame counter, backend change notification. Native baseline remains default; WPF XAML untouched.
- **Pending:** Import and license-review upstream ProCad/Tida sources, adapt API, WPF/.NET compatibility tests, FPS/RAM comparisons, and Windows CI build. No external source was copied.

## UN-2026-10-29-ProCad-native — v1.0.48
- Request date: 2026-10-29
- Request: Embed ProCad native viewer in iSt-CAD source while retaining current UI.
- Status: Implemented in conditional source; restore, Windows build and actual operation unverified.
- Update date: 2026-10-29

## UN-2026-10-29-DYN-UI — v1.0.49.3
- Requested: Separate lightweight non-interactive pointer hint and editable dynamic input. Show editable input only after typing, never from pointer move.
- Status: Source updated; Windows build pending.

## UN-2026-10-29-004 — Zoom-out responsiveness, v1.0.49.4
- Requested: Avoid freezing UI when zooming out to a large visible region.
- Findings: Main R-tree query and geometry composition were already inside Task.Run in v1.0.49.3. Remaining repeated UI metadata snapshots and large selected-object overlay loops were risks.
- Changes: Cache hidden-entity and layer-color snapshots by drawing geometry revision; keep R-tree queries, LOD and geometry composition on worker; suppress huge (>25,000) selection overlay during rendering without changing selected IDs; do not restart a failed identical worker indefinitely.
- Status: Source changes completed; Windows WPF build/runtime tests pending.

## UN-2026-10-29-Geometry-Cancellation — v1.0.49.5
- Request: OperationCanceledException during zoom (BuildVisibleGeometryAsync worker).
- Status: Source corrected; Windows build/runtime verification pending.
- Change: Explicitly recognize expected cancellation tokens; avoid same-frame retry storms; distinguish unrelated cancellations.
- Note: Visual Studio can still pause on first-chance cancellation when configured to break on thrown CLR exceptions, even if handled.

## UN-2026-10-29-LOD-001 — v1.0.49.6
- Request: prioritize background render, density-aware LOD, memory-efficient spatial query, and UI responsiveness on large zoom-out.
- Status: source implemented; compilation, large-file benchmarking and visual regression pending.
- Implemented: unordered spatial query yields directly without allocating/sorting a giant results list; dense zoom-out >12k candidates merges sub-3px bounded entities into 3px occupancy blocks; full geometry preserved in drawing and spatial index; long/large objects remain vectors; background task and stale-frame cancellation retained.
- Limits: occupied pixels use approximate neutral color and may not honor draw order; no tile-based partial redraw yet.

## UN-2026-10-30-Geometry-Cancel-Cooperative — v1.0.49.11
- **Request date:** 2026-10-30
- **Request:** Handle `OperationCanceledException` reported from `BuildVisibleGeometryAsync` while rendering the viewport.
- **Finding:** Line 2928 in v1.0.49.10 called `token.ThrowIfCancellationRequested()` inside the visible-entity loop. Cancellation of a stale frame is expected during zoom/pan, and was already caught; Visual Studio can still break on the first-chance thrown exception when CLR exception breaking is enabled.
- **Changes:** Replaced cancellation throws in the adaptive viewport query and the main frame-composition loops with cooperative cancellation checks and early discard results. Partially composed frames are never accepted after cancellation. The catch remains as a fallback for dependencies that still throw cancellation exceptions.
- **Validation:** Source inspected and ZIP integrity checked. Build/runtime test is pending because this environment has no .NET SDK or Windows WPF runtime.

## UN-2026-10-30-Viewport-Responsiveness — v1.0.49.12
- **Request date:** 2026-10-30
- **Request:** Coalesce preview updates; hide grid during wheel/pan motion and redraw at idle; cancel inside large polyline/proxy geometry loops; reduce large-query sorting and share duplicate geometry; recover render after cancellation during zoom out/in.
- **Changes:** Added a nominal 16 ms render-priority wheel-preview timer; hid and force-refreshed the grid at interaction boundaries; passed cancellation tokens into polyline/proxy geometry generation and chunked proxy path materialization; lowered the far-LOD unordered-query threshold to 8,000 candidates (leaving 12,000 as the pixel-aggregation threshold), and added content-signature/exact-match geometry sharing for duplicate line/circle/arc and bounded polyline geometry (up to 4,096 vertices); and restarted the latest missing viewport image after canceled worker completion.
- **Validation:** Source review completed and ZIP integrity checked; Windows build, runtime rendering, and benchmark are not available in the current environment because .NET SDK/WPF are absent.

## UN-2026-10-30-INSERT-Ghost-Geometry — v1.0.49.13
- **Request date:** 2026-10-30
- **Request:** Investigate many circular shapes that iStCAD displays for native DWG `INSERT` entities but AutoCAD does not display.
- **Finding:** The adapter expanded every block child without applying entity invisibility and layer Off/Frozen state. It also could fall back to cached `INSERT` proxy paths when expanded content had no visible paths, resurrecting hidden circles.
- **Changes:** Filtered nested preview geometry by entity visibility and layer state; propagate layer-0 inheritance through nested INSERTs; avoid stale proxy-path fallback for non-empty blocks made entirely of native previewable entity types; cache block preview classification per drawing open; added top INSERT block-name counts to open diagnostics.
- **Validation:** Source reviewed and XML/ZIP integrity checks performed. Affected DWG was not supplied; Windows WPF build and rendering comparison against AutoCAD remain pending.

## UN-2026-10-30-Native-Preview-Safety — v1.0.49.14
- **Request date:** 2026-10-30
- **Request:** Apply ten viewport/DWG-preview safeguards: cadence-coalesced preview; hide grid while scrolling; cancellation within large polyline/proxy loops; less sorting and duplicate geometry caching; recover render after cancellation; avoid cloning/exploding nested INSERTs; filter invisible/off/frozen children; prevent native/proxy double drawing; reject degenerate arcs as circles; detect recursive blocks and cap preview work.
- **Changes:** Retained the 16 ms render-priority preview timers and wheel-idle commit; the grid is now restored only when it is still hidden (avoiding a duplicate full grid redraw after zoom commit). Kept cancellation checks inside polyline, Hatch, nested-block and proxy loops; added a drawing-wide preview budget in addition to per-INSERT/proxy limits; cached block-native capability by both block definition and inherited INSERT layer; stopped rescanning known unsupported block definitions; and stopped preview traversal once the drawing-wide budget is exhausted. Added content-based geometry sharing for bounded, identical proxy/INSERT previews (up to 8,192 points and 64 text items). INSERT native paths and proxy paths are selected as alternative sources instead of being overlaid. Nested INSERT traversal is direct and depth-limited; circular block references are cut off. Invisible, off-layer and frozen children are skipped with layer-0 inheritance. Degenerate/full-turn ARC entities are not converted into full circles.
- **Safety limits:** 12 nested block levels; 50,000 entities, 24,000 points, 8,000 paths and 512 text items per INSERT; 8,192 points and 2,048 paths per proxy entity; drawing-wide limits of 1,500,000 preview entities, 2,000,000 points, 200,000 paths, 100,000 texts and 500,000 proxy items. When a preview budget is reached, rendering preview data is clipped; the underlying native DWG entities are not deleted or modified.
- **Validation:** Static source inspection, project/XAML XML parsing, brace-balance checks and ZIP integrity checks. A Windows WPF build, opening the supplied DWGs, image comparison against AutoCAD and measured FPS/latency could not be run in this environment because .NET SDK/WPF is unavailable.
- **Known limitation:** This version prevents duplicate native/proxy drawing and handles entity/layer visibility, but it does not claim full evaluation of every AutoCAD Dynamic Block visibility/evaluation-graph state. Such blocks still require DWG-specific regression validation.

## UN-2026-10-30-Dynamic-INSERT-Ghost-Circles — v1.0.49.15
- **Request date:** 2026-10-30
- **Symptom:** Screenshot shows multiple unwanted concentric circles/arcs around a single INSERT; these are absent in AutoCAD.
- **Source finding:** INSERT preview traversed all block entities and filtered only entity invisibility/layer state. It did not account for `BlockVisibilityParameter` state membership exposed through `BlockRecord.EvaluationGraph`, so a dynamic block source definition could be previewed as though every named visibility state were active simultaneously.
- **Changes:** Added recursive detection of unresolved dynamic visibility-state blocks within nested INSERT/DIMENSION trees, respecting inherited layer visibility and recursion limits. Native child tessellation is skipped for those trees; the INSERT's proxy graphics become the preferred display source. Evaluated anonymous `*U` block representations linked to their source remain eligible for native traversal. This also avoids allocating previews for geometry that would be discarded.
- **Important limitation:** This is a safe fallback, not a complete evaluator for dynamic block actions (stretch, lookup, flip, array, etc.). If the INSERT has no suitable proxy graphics, the renderer may show only its extents fallback rather than fabricate circles from all states.
- **Validation:** Source-level inspection and static package checks only. Build and visual comparison against the provided DWG still require Windows/.NET WPF.

## UN-2026-10-30-Dynamic-INSERT-Proxy-Fallback — v1.0.49.16
- **Request date:** 2026-10-30
- **Feedback:** The v1.0.49.15 preview still showed multiple large circles/arcs around the same INSERT.
- **Source finding:** v1.0.49.15 suppressed native traversal when a visibility state was unresolved but then treated the INSERT proxy graphics as a safe fallback. The screenshot shows that proxy geometry is not a reliable active-state representation for this case and can itself contain unwanted paths.
- **Changes:** Detect visibility-state graphs on both the referenced block and its anonymous block source. When the active state is unresolved, suppress native and proxy preview paths/text together and retain only the INSERT bounds fallback. Add read diagnostics counting affected top-level INSERTs by block name. Native DWG objects and proxy data remain preserved; no database entities are deleted.
- **Trade-off:** The affected dynamic INSERT may display as a dashed bounds rectangle until active Visibility State evaluation is implemented. This is preferable to fabricating geometry from inactive states.
- **Validation:** Static source checks, project/XML checks, and ZIP integrity only. Windows build and visual comparison with the same DWG are still required; .NET SDK/WPF is not available in this environment.


## UN-2026-10-30-INSERT-Proxy-Artifacts — v1.0.49.17
- **Request date:** 2026-10-30
- **Evidence:** The same DWG displays normally in AutoCAD; iStCAD shows multiple large concentric circles/arcs. The selected problematic object is a single INSERT on layer `dbao`, native handle `172EF7F`, with very large reported extents. This points to preview generation for one INSERT, not actual standalone CIRCLE entities in the drawing.
- **Source finding:** v1.0.49.16 still called `ExtractProxyGraphics()` for INSERTs whose dynamic visibility was not detected as unresolved and tessellated every `ProxyCircle`/`ProxyCircularArc` in that proxy stream. Such proxy geometry is not guaranteed to represent the active visible state shown by AutoCAD.
- **Change:** Never read/tessellate proxy graphics for any INSERT in viewport preview. Keep native block traversal only when the block is classified as safely previewable; otherwise retain the INSERT extents fallback. Non-INSERT proxy entities keep their existing proxy preview path. No native DWG objects, proxy records, or source file data are deleted or modified.
- **Trade-off:** Some custom/unsupported INSERTs may show only their extents until proper block evaluation is implemented. This deliberately avoids false circles.
- **Validation:** Static source inspection and package integrity only. A Windows WPF build and comparison with the supplied DWG are still required; no runtime rendering result is claimed.


## UN-2026-10-30-Open-Mouse-Lag — v1.0.49.18
- **Request date:** 2026-10-30
- **Request:** Investigate application stalls after opening large DWG, including when moving the pointer.
- **Finding:** Idle `DrawingCanvas_MouseMove` does not hit-test or create CAD objects; it only records the pointer position. However, the first completed viewport automatically queued a background OSNAP build that walked every indexed entity and generated endpoint/midpoint candidates for every Polyline, even when no draw command was active. Large polyline/block preview traversal during DWG mapping also had generous global/per-INSERT budgets, and unresolved Dynamic Block visibility could be rescanned for repeated INSERTs. The concurrent background work could saturate CPU and allocate large snap/preview collections, making pointer/UI response appear frozen.
- **Changes:** Deferred OSNAP preparation until a draw/edit command starts; capped precomputed snap candidates at 180,000 and skipped pathological polylines above 12,000 vertices in the global snap cache; reduced per-INSERT and drawing-wide preview budgets; cached Dynamic Block visibility-tree classification by block and inherited layer; made incomplete/cyclic visibility scans fail closed to bounds-only preview.
- **Trade-off:** OSNAP may be unavailable until its command-triggered background preparation completes; very large polylines are omitted from the global OSNAP cache; oversized INSERT previews may be clipped to bounds/partial preview. Original native DWG entities and block definitions are preserved.
- **Validation:** Source-level review and archive integrity checks completed. Build, Windows/WPF runtime and real DWG responsiveness tests are pending because the current environment has no .NET SDK/WPF runtime.

## UN-2026-10-30-Import-Object-Allocation — v1.0.49.19
- **Request date:** 2026-10-30
- **Request:** Investigate extra objects allocated during native DWG reading and reduce lag caused by geometry/coordinate preview generation.
- **Findings:** The v1.0.49.18 importer eagerly generated INSERT preview coordinate arrays before checking whether the referenced block was supported by the native preview walker. Unsupported block previews were then discarded, so the entire allocation was wasted. Hatch preview also called `BoundaryPath.Edge.ToEntity()` per edge, creating transient CAD entities only to tessellate a preview. The open-time database summary additionally traversed the full XDictionary/Reactor/XData graph although the result was diagnostic-only.
- **Changes:** Run native-preview eligibility classification before extracting INSERT coordinates; skip the expensive extraction for unsupported/unsafe blocks; skip Hatch edge materialization in preview and retain native Hatch/extents; avoid recomputing INSERT bounds from generated preview points when native extents are already present; replace the recursive metadata graph walk with direct table/entity counts; reduce per-INSERT and drawing-wide preview allocation budgets; disable deep content hashing for ProxyCadEntity and limit Polyline content-sharing signatures to 1,024 vertices; log temporary preview allocation totals.
- **Trade-off:** Complex INSERTs and Hatch boundaries may show bounds/partial preview. Native entities, handles, block definitions, and source DWG data remain preserved.
- **Validation:** Static inspection, XML validation and ZIP integrity checks only. .NET/WPF build and real DWG timing/memory tests remain pending.

## UN-2026-10-30-Full-Entity-Preview — v1.0.49.20
- **Request date:** 2026-10-30
- **Request:** Improve opening fidelity for the supplied DWG samples while keeping viewport interaction responsive.
- **Changes:** Added direct, bounded Hatch boundary tessellation from ACadSharp's native edge records (Line, circular arc, elliptical arc, bulged polyline, and spline). The importer no longer needs to explode a Hatch into temporary database entities to display its boundary. Hatch boundaries inside INSERTs are transformed through the owning block transform and remain one native Hatch object. Top-level proxy entities and Dimensions now use independent bounded preview budgets, so a complex earlier INSERT cannot exhaust the shared recursive INSERT budget and suppress later entities in Model Space.
- **Preservation:** The original native Hatch, INSERT, proxy records, handles, block definitions, and all source entities remain intact. Preview geometry is only a viewport representation.
- **Known limitations:** This change displays Hatch boundary loops, not full pattern/gradient fill. Dynamic Block visibility states still use the existing safe fallback where ACadSharp cannot resolve the active state. Per-INSERT safety limits can still clip extremely complex nested blocks; unsupported entities remain native-preserved but may be represented by preview paths or extents rather than fully editable native editor tools.
- **Validation:** Source-level review, brace-balance checks, project XML checks, and ZIP integrity checks only. The current environment has no .NET 8 SDK/WPF runtime, so no successful Windows build, DWG open test, visual comparison, or FPS/RAM benchmark is claimed. The supplied `x1.dwg`, `x5.dwg`, `x6.dwg`, and `x7.dwg` still require execution on Windows for measured acceptance.


## v1.0.49.21 — Interaction and viewport polish
- Pin/unpin the edge toolbar.
- Preview MOVE/COPY/ROTATE transforms before committing.
- Add independent PLINE command with live preview, Enter-to-finish, C-to-close, and Backspace vertex undo.
- Use AutoCAD-like neutral grid colors with red X and green Y axes. Keep grid visible and transform it during pan/zoom to reduce flashing.
- Build/runtime validation remains pending because the current environment has no .NET SDK.


## iStCAD_1 v1.0.50

- Full-length crosshair; DYN width follows prompt hint; fuzzy command suggestions; Space/Enter parity.
- AutoCAD-style Properties groups; polar guide rays and OTRACK alignment guides from hover-acquired OSNAP points; Parallel and Perpendicular OSNAP relation modes.
- Windows build and interaction validation pending.


## iStCAD_1 v1.0.52

- Fixed CS0103 in Hatch proxy-preview extraction by applying the owning `toWorld` transform when converting hatch boundary points.
- Fixed CS1739 by matching the `ProxyPreviewPath` record parameter name `Closed`.
- Version incremented from 1.0.50 to 1.0.52.


## v1.0.52 – Matrix4 Hatch preview compile fix

- Replaced unsupported `Matrix4.ApplyTransform(XYZ)` with the supported `Matrix4 * XYZ` operator when applying accumulated INSERT transforms to Hatch preview points.
- Full .NET build/runtime validation remains pending on Windows.

## UN-2026-10-30-001 — v1.0.52.1 UI selection / COPY safety
- Request date: 2026-10-30
- Status: Source updated, Windows build and runtime tests pending.
- Removed red and green origin axis strokes; retained minor/major grid.
- Increased object hit tolerance to at least 8 screen pixels; kept canvas focus after selection.
- Guarded two-click selection completion and differentiated active commands from idle repeat; Space and Enter unified outside text inputs.
- COPY refuses unchanged or duplicate IDs and captures exceptions instead of silently adding the same object.
- Follow-up: verify complex native entity COPY, OSNAP selection interplay, document undo stability in Visual Studio.

## 2026-10-30 — v1.0.52.2 CAD style management (partial implementation)
- Added dropdowns for Layer, Color, LineStyle, Text Style and Dim Style in existing toolbar.
- Added management dialog to create/update named Layer/Text/Dim/Line style definitions; drawing snapshot preserves these definitions for undo.
- Newly created LINE/CIRCLE use the current layer, color, font family and linetype pattern.
- Preview honors selected color and basic dashed mode. Temporary polyline committed vertices participate in Endpoint OSNAP without being inserted into the document.
- Limitations: Other creation commands, true dimension/text command integration, per-document DWG/DXF table persistence, precise per-pattern preview and snapping to moving preview geometry are NOT completed; Windows build not verified.

## v1.0.52.3 — CAD toolbar dropdown theme, style manager commands, DYN input width
- Request: Dark readable ComboBox menus; LA/LAYER, ST/STYLE, DIMSTYLE, LT/LINETYPE commands; DYN editable input at least as wide as hint/suggestions.
- Status: Implemented in source; compilation on Windows pending.

## 2026-10-30 — v1.0.52.4 — Toolbar / Properties / control IDs
- Edge toolbar: vertical alignment to top of drawing, automatic scrollbar only on overflow. Status: source updated.
- Properties: compact, dark, two-column categorized AutoCAD-like presentation; retain read-only property values and selection synchronization. Status: source updated.
- Tooltips: include AutomationId or x:Name for named XAML buttons. Status: source updated. Dynamic buttons without stable names still require explicit IDs.
- Build validation: Windows WPF SDK unavailable in environment; pending.

## UN-2026-10-30-UI-005 — v1.0.52.5
- Request: AutoCAD-like dark caption buttons and slate dark ComboBox display and popup.
- Status: Implemented in XAML; Windows build and visual verification pending.
- Date: 2026-10-30.

## v1.0.53 — viewport alignment, toolbar and hover
- Date: 2026-10-30. Status: Updated in source; Windows build and on-screen verification pending.
- Remove the horizontal 11-command ribbon strip (commands remain accessible via CLI and vertical tools).
- Align geometry/preview/selection/grid Image top-left to the drawing Canvas origin (prevent center alignment drift).
- Left toolbar stretches from top to bottom of drawing workspace, with empty space after last item and scroll bar only on overflow.
- Use slate-toned hover and contrasting white foreground.
- Versioning: 1.0.53 then 1.0.54, 1.0.55, ... (sequential integer).

## v1.0.54 — Expanded command line full drawing width and selectable history
- Request: Expanded command history must span entire drawing canvas width, be 30% shorter, and support text selection and copying.
- Implemented: Bind panel width to DrawingCanvasBorder.ActualWidth; height 196→137 (~70%); replace nonselectable TextBlocks with read-only multiline TextBox with Ctrl+C and scrolling.
- Status: Source updated; Windows build/runtime validation pending.

## v1.0.55 — Fixed Create / Modify header in CAD selector dropdowns
- Request: Every CAD style selector shows a permanently top-pinned Create / Modify action, not a selectable item.
- Status: Implemented in source; Windows build pending.
- Controls: LayerSelector, TextStyleSelector, DimensionStyleSelector, CadColorSelector, CadLineStyleSelector.
- Implementation: Separate non-item button in ComboBox Popup above ScrollViewer, with command routing to the corresponding CAD Style Manager. Current selection is unchanged when launching manager.

## v1.0.56 — 2026-10-30
- Toolbar left 66 -> 53 px. Uniform darker hover. Caption Close glyph increased.
- Properties groups are based on selected entity type. Single Circle: General / 3D Visualization / Geometry; Text has Text group. Multiple selections show shared General only. No selection hides all groups.
- Status: source updated; WPF build/runtime validation pending.

## v1.0.57 — 2026-10-30
- Request: draggable Properties panel width; DYN input width 60% of prior 270 px and closed border.
- Status: Source implemented; Windows build and UI validation pending.
- Properties: 6 DIP GridSplitter, 230 DIP minimum intended width, remembers last measured width on hide.
- DYN: outer panel 162 DIP; inner entry 143 DIP plus arrow, complete 1 DIP border; tooltip remains separate.

## v1.0.58 — 2026-10-30
- Request: Color selector Create / Modify should open a full custom color picker and show the result like a swatch + RGB text (for example 0,213,159).
- Updated: Added Windows color dialog, custom color persistence in CadColorSelector, swatch item template, and RGB/HEX color parsing for preview and entity creation.

## v1.0.58 — 2026-10-30
- Color Create/Modify opens dark-theme RGB picker; swatch and RGB values in selector.
- Child CAD Styles form styled dark; ComboBox widths standardized to 134 px.
- Native control hover palette darkened with legible foreground.
- DYN suggestion execution preserves input while an interactive command is active.
- Custom RGB color now applied to newly created LINE and CIRCLE through CadEditor.
- Status: Source updated; Windows build and UI testing pending.

## v1.0.59 — 2026-10-30
- Properties: filter dropdown All (n) / each entity type count, scoped properties for selected category (without changing selection).
- ESC closes style/secondary modal dialogs; existing unsaved-close confirmation remains in place.
- Command status text increased from 10 to 11 pt.
- Fix CS0104 Brush ambiguity with explicit WPF Brush alias.
- Status: source updated; Windows build/runtime validation pending.

## v1.0.60 — 2026-10-30
- Fix CS0104 ambiguous WPF/WinForms references in MainWindow.xaml.cs: Image, MouseEventArgs, TextBox, KeyEventArgs, Point.
- Use explicit WPF aliases while retaining explicit WinForms alias.
- Source validation only: .NET Windows build unverified.

## v1.0.61 — 2026-10-30
- **Request:** Fix 165 CS0104 ambiguous WPF/Forms/Drawing symbol errors.
- **Root cause:** `<UseWindowsForms>true</UseWindowsForms>` was enabling SDK implicit namespaces that shadow `System.Windows` controls/media/shapes. No Windows Forms API usages were found in application source.
- **Change:** Removed Windows Forms enablement from WPF project; removed unused WinForms alias; retained existing WPF aliases and UI behavior.
- **Status:** Source fix completed, build not verified on Windows.

## v1.0.62 — 2026-10-30
- Đồng bộ Width=148 px cho Text Style, Dim Style, Layer, Color, LineStyle.
- Sửa template hover/selection ComboBox để chữ trắng tương phản nền xanh xám tối.
- Form con tạo bởi CreateCadChildWindow dùng tông sáng trắng/xám nhạt; input và button có màu chữ tối, hover rõ ràng.
- Chưa kiểm thử build trên Windows.

## v1.0.63 — 2026-10-30
- Added detailed Plot dialog similar to AutoCAD with page setup, plot style, printer/plotter, paper size, plot area, plot scale, plot options and drawing orientation.
- Unified child windows to light theme matching plot dialog.

## v1.0.64 — 2026-10-30
- Fixed Create / Modify pinned dropdown action with an explicit WPF ControlTemplate. Hover background #263443 is darker than the normal #3B4A60 and text stays white; pressed state #1A2736. Avoid OS default light-blue hover.
- Kept the existing dark hovered ComboBox list items and main toolbar.
- Increased CommandStatusText FontSize from 11 to 12.
- Build not verified on Windows.

## v1.0.65 — 2026-10-30
- Form con sử dụng chữ đen (#000000) trên nền sáng, áp dụng TextBlock, TextBox, ComboBox, ComboBoxItem, Button, CheckBox, RadioButton, GroupBox.
- Gọi ConfigureEditorDialog ngay từ CreateCadChildWindow để tất cả cửa sổ con sử dụng cùng tài nguyên giao diện.
- Giữ màu hover của form con đủ tương phản với chữ đen; giao diện CAD chính giữ nguyên.

## v1.0.66 — 2026-10-30
- Added catalog of 50 common CAD commands plus alias manager; commands that lack handlers are labelled Pending, not claimed as implemented.
- Supports literal Vietnamese shortcut characters with no Telex conversion. Set aliases via ALIASES/SHORTCUTS.
- Custom shortcuts saved to local AppData command-aliases.json and used in command dispatch and DYN suggestions.

## v1.0.66 — 2026-10-30
- Added 50+ common CAD commands to command catalog; existing functional commands remain available; unimplemented commands explicitly identified as Pending and do not silently execute another action.
- Added ALIASES/SHORTCUTS manager to edit, persist and validate shortcuts (including Vietnamese letters and diacritics). Exact Unicode input is preserved; no automatic transliteration.
- Fixed WPF ComboBox face hover: transparent toggle-button template prevents system-default light hover overlay. Popup dropdown remains unchanged; highlighted face background is dark navy and caption text stays light.
- IMPORTANT: The catalog is not a claim that all 50 commands are implemented. Pending commands require future geometry/action implementations.

## v1.0.67 — 2026-10-30
- Drawing right-click ends active command, or repeats previous command when idle.
- Implemented preliminary selected-object DIST/AREA, MATCHPROP, OVERKILL, PROPERTIES.
- Existing Unicode alias editing table retained; child dialog ink remains black.
- Remaining complex CAD commands remain Pending; full implementation requires further geometry and UI work.

## v1.0.68 — 2026-10-30
- Implemented SCALE, MIRROR, OFFSET (line/circle), EXPLODE (straight polylines), JOIN (connected lines), PURGE (unused custom definitions), TEXT, MTEXT via validated lightweight dialogs and undoable entity edits.
- Unsupported geometry remains explicitly excluded; existing TRIM, EXTEND, FILLET, CHAMFER, STRETCH, ARRAY, BREAK, HATCH, DIM, INSERT, BLOCK remain pending.
- Child dialogs use shared light theme, black text.

## v1.0.69 — 2026-10-30
- Fix CS1501 at MainWindow.xaml.cs: PROPERTIES/PR command now calls parameterless TogglePropertiesPalette().
- Confirmed all direct TogglePropertiesPalette call sites match its zero-argument signature.
- XAML and csproj XML validated; Windows build not available in this environment.

## v1.0.70 — 2026-10-30
- Implemented BREAK for one LINE via distances; rectangular ARRAY for supported entities; CHAMFER for two LINE objects with equal setbacks; FILLET only radius 0 on two LINE objects.
- Unsupported geometry and invalid parameters are rejected explicitly. All edits go through ApplyEntityChanges for Undo/Redo.
- Pending: TRIM, EXTEND, STRETCH, HATCH, DIM, INSERT, BLOCK; complex fillet and chamfer geometry.
- Not Windows build-tested.

## v1.0.71 — 2026-10-30
- MI: pick mirror axis by two points on viewport, OSNAP and dashed axis preview; no form.
- DIST: pick two points on viewport, distance, DX/DY/angle in Command History, dashed tracking and length preview.
- ROTATE: preserves base point, drag preview, click second point workflow.
- Right-click/Esc cancel point-based tools; no loss of selected objects.
- Other numeric-form commands pending migration to point-driven workflows.

## v1.0.72 — 2026-10-30 — Large DWG open / wheel-zoom freeze fix
Root causes found by static review (no Windows profiler available here):
- The viewport worker handed tens of thousands of vector geometries to the WPF UI/render thread per frame (every entity that was not already aggregated, including unreadable text outlines and polylines with hundreds of thousands of vertices). Each wheel commit or file open rebuilt that image and froze the UI.
- `CadGeometryCache` copied its content-group list (`ToArray`) on every duplicate-geometry hit and grew the group with alias entries: O(n^2) on drawings with many identical primitives.
- Pattern keys were rebuilt with LINQ/`string.Join` for every entity on every frame, and every entity owned its own linetype array.

Changes:
- Frames with 600+ items are rasterized on a short-lived STA background thread (`RenderTargetBitmap`) and handed to the UI as ONE frozen bitmap (falls back to the previous vector image if rasterization fails or the surface is > 8192 px / 36 MP).
- Entities whose whole extent is <= 1 px (<= 3 px in dense far-zoom views) are drawn as 2x2 dots in one pixel buffer instead of geometry.
- TEXT/MTEXT shorter than 4 px on screen get a thin box instead of glyph outlines (no FormattedText work).
- Polylines with more than 1,500 vertices and > 4 vertices per screen pixel are screen-simplified per frame (0.75 px step); the entity itself is untouched.
- Geometry cache: no list copy, at most 8 content probes, aliases not added to the content group, group size capped, `Prune` early-out on an empty cache.
- Linetype arrays are shared per native linetype; render pen/dash keys are cached per pattern; pen brushes are frozen.
- Selection overlay no longer requests a rebuild for entities drawn as dots/boxes/simplified lines; it highlights their envelope (or the simplified polyline).

Not verified: no .NET SDK / Windows here, so this is NOT compiled or measured. Build on Windows, open the slow DWG, then run `PERF` and report numbers. Known visual trade-off: sub-pixel entities show as grey dots; tiny text shows as a thin box.

## v1.0.73 — 2026-10-30 — Hover freeze / ghost circles
- OSNAP intersection and relation snaps (run on the UI thread on every pointer move while a draw command is active) are now hard-capped at 48 entities / 64 segments, and long polylines stop being scanned once the cap is reached. Previously a zoomed-out cursor could gather thousands of segments and run an O(n^2) intersection loop per mouse move.
- Preview circle/line drawing skips non-finite or absurdly large geometry (radius > 200,000 px, dashed pen only below 20,000 px) so WPF can no longer hang tessellating it.
- Unwanted circles: most likely mouse clicks queued while the UI was frozen being replayed as center/radius clicks; removing the freeze removes them. Not reproduced here.
- Not compiled or run (no .NET SDK / Windows here).

## v1.0.74 — 2026-10-30
- Idle hover after opening a DWG (no command active) cannot be caused by snap code; the remaining suspect is the WPF render thread re-rasterizing a very heavy vector frame. Frames with 24+ items are now always pre-rasterized to one bitmap on a background thread (was 600+, which missed frames made of a few very heavy INSERT proxies).
- Not compiled/run. If it still freezes, send the Command History lines printed right after opening (Open stages, Viewport geometry sources, entity types, First viewport ready) and the output of PERF.

## v1.0.75 — 2026-10-30 — Hover freeze on any DWG
- Root cause: the full-canvas drafting crosshair (`_draftingCursor`) moves on every frame; after each viewport commit `ResetWheelZoomPreview`/`ResetPanPreview` removed the BitmapCache from the geometry, selection and grid images, so WPF re-rendered those vector images for every cursor move.
- Fix: `ApplyIdleBitmapCache()` keeps a retained bitmap (DPI-aware) on the geometry, selection and grid images at rest; only the preview image stays uncached. Combined with v1.0.74 pre-rasterized frames.
- Not compiled/run here.

## v1.0.76 — 2026-10-30 — Import created too much preview data
- Dimensions and top-level proxy graphics each got a brand-new preview budget per entity, so the drawing-wide total was unbounded (thousands of dimensions/proxies x up to 75k nested entities / 100k points each). They now share one drawing-wide budget (4x the INSERT budget); after it is spent, remaining ones are bounds-only.
- Open log now shows "Top-level preview allocation (dimensions/proxies)" first, with whether the budget was exhausted.
- Workstation concurrent GC set explicitly in the csproj.
- Not compiled/run here.

## v1.0.77 — 2026-10-30 — Stray lines in INSERT previews
- INSERT preview no longer draws attributes flagged Hidden (invisible ATTRIB) on the INSERT or on nested INSERTs.
- POINT objects inside blocks are no longer expanded into a synthetic 1-unit cross (stray lines around every INSERT). Top-level POINT previews are unchanged.
- Known, not fixed: XCLIP (spatial filter) boundaries on INSERTs are not applied, so geometry outside a clip can still show; annotative/dynamic-block multi-state content is handled only by the existing visibility fallback.
- Not compiled/run here; the exact source of the reported lines was not verified without the DWG.

## v1.0.78 — 2026-10-30 — Slow render on large DWG
- Frames with more than 1,500 items are grouped by stroke style instead of only batching consecutive equal styles, so a layered drawing is drawn with a few dozen DrawGeometry calls rather than thousands. Only the overlap order of different-colored strokes can change (no fills are drawn).
- PERF now reports the last frame's stages: query / geometry build / compose+raster ms and item count.
- Not compiled/run here; the supplied DWG (AC1024, 12 MB) could not be opened in this sandbox, so nothing was measured.

## v1.0.79 — 2026-10-30 — Click selection picked the wrong object
- Root cause: INSERT/DIMENSION/HATCH/proxy objects were hit-tested against their bounding box and returned distance 0 anywhere inside it, so clicking empty space inside a large block's box selected that block (and later entities won ties).
- Fix: `DistanceToProxyBounds` now measures against the object's real preview strokes and text boxes (capped at 40,000 segments per entity per pick). Bounds-only proxies (nothing previewed) are pickable only on their outline.
- Not compiled/run here.

## v1.0.80 — 2026-10-30
- Dynamic input / cursor prompt: its position is now set directly in the mouse-move handler. It used to move only inside the 16 ms preview timer tick, which runs late whenever the UI thread is busy (large drawings), so the box trailed far behind the cursor.
- Autosave failure on close: the dialog now shows the real exception, writes details to %LOCALAPPDATA%\iStCAD_1\autosave-errors.log, and offers "Close anyway" (default No) instead of an OK-only dialog that left no way out.
- Not compiled/run here. Cause of the recovery-file write failure is not yet known; please send autosave-errors.log.

## v1.0.81 — 2026-10-30 — Render/CPU algorithms, highlight, grid
- Viewport worker: when more than 400 candidates are visible, LINE/CIRCLE/ARC/LWPOLYLINE are written straight into ONE StreamGeometry per stroke style (new `CadGeometryBuilder.Append*Figure` writers). No per-entity geometry objects, no GeometryGroup of 100k children, no geometry-cache traffic for these types. Big polylines still use screen simplification. Text/proxy/INSERT entities keep the cached per-entity geometry path. Frames are pre-rasterized off the UI thread.
- Selection highlight no longer waits for (or depends on) the viewport worker: selected geometry comes from the cache or is built on demand (<= 400 selected objects), otherwise the envelope is outlined. Previously a busy/cancelled worker, or lines that are no longer cached, left the overlay empty.
- Grid: minor and major lines are written into two StreamGeometry paths (2 draw calls instead of up to ~4,000 DrawLine calls).
- Not compiled/run here; no timings measured. Please send PERF (query/geometry/compose+raster ms) after opening and after one zoom.

## v1.0.82
- TEXT/MTEXT: giải mã %%c (Ø), %%d (°), %%p (±), %%% và %%nnn như AutoCAD; %%u/%%o bị bỏ qua (chưa vẽ gạch chân/gạch trên).
- Font: bỏ đuôi .ttf/.ttc/.otf và ánh xạ tên file phổ biến (times, romans, simplex, isocp, txt, cour…) sang font TrueType tương ứng.
- Chưa biên dịch/đo đạc trong môi trường này; cần kiểm tra trên máy thật.

## v1.0.83
- Ctrl+A: chọn tất cả đối tượng (bỏ qua đối tượng ẩn).
- Hatch: biên bao gồm các cạnh không nối tiếp được tách thành vòng riêng (hết đường thẳng dài vắt ngang bản vẽ); vòng suy biến (<3 điểm) bị bỏ.
- Text/MText: tô đặc (fill) thay vì chỉ viền; ở mức zoom xa vẫn là khung rỗng.
- Linetype: nhập đầy đủ linetype từ DWG vào danh sách; chuẩn hóa dash/gap theo dấu, bỏ phần tử shape/text; text style và dim style của DWG cũng được nạp vào combo.
- Thanh công cụ: thứ tự kiểu AutoCAD — Layer, Color, Linetype, Text Style, Dim Style.
- Chưa biên dịch/đo trong môi trường này.

## v1.0.84
- Thanh công cụ giữ nguyên vị trí như cũ; Layer / Text Style / Dim Style / LineStyle nay được nạp dữ liệu từ DWG mỗi khi mở hoặc chuyển bản vẽ (trước đây chỉ nạp lúc khởi động).
- Grid: dựng lại ngay khi zoom/pan dừng, không chờ khung hình đối tượng hoàn tất (trước đây grid bị trễ theo render nặng).
- DYN: lấy vị trí chuột thật khi lệnh bắt đầu (kể cả gõ lệnh từ command line) và đặt lại sau layout, nên hiện ngay cạnh con trỏ.
- Khởi động: multi-core JIT profile, TieredPGO tắt, và đặt ưu tiên GPU hiệu năng cao (Windows Graphics settings, HKCU) cho iStCAD — có hiệu lực từ lần mở sau.

## v1.0.85
- Chọn TEXT/MTEXT: hộp chữ có hướng (xoay, căn lề, width factor, số dòng) dùng chung cho R-tree, click và window selection — khớp vị trí chữ thật.
- Kéo đối tượng: nhấn giữ lên đối tượng đã chọn rồi kéo để di chuyển (hiện ảnh mờ khi kéo, áp dụng khi thả; hỗ trợ Undo như MOVE). INSERT/proxy chưa di chuyển được.
- Trạng thái chờ: hiện "Regenerating…" kèm thanh tiến trình ở góc phải khung vẽ trong lúc dựng khung hình.

## v1.0.86
- Xóa/sửa/vẽ đối tượng nhanh: chỉ cập nhật phần thay đổi (R-tree dùng chung + danh sách đối tượng mới), không dựng lại toàn bộ chỉ mục và không làm trống khung nhìn; xóa nhiều đối tượng một lượt O(n) thay vì O(n·k); cache hình chỉ bỏ các đối tượng đổi.
- Render zoom/pan dần dần (giống AutoCAD): nếu khung đầy đủ trước đó chậm (>250 ms), hiện ngay khung nháp thô (chữ dạng khung, vùng dày gộp điểm) rồi tinh chỉnh thành khung chi tiết.
- Chưa biên dịch/đo trong môi trường này.

## v1.0.87
- Save DWG (lỗi AutoCAD báo file hỏng): trước khi ghi, loại các tham chiếu treo tới object mà bộ ghi DWG không ghi (proxy/unknown/visual style/dimension association/AEC/dynamic block data) khỏi dictionary, XDictionary và reactor; tắt ghi dynamic block data; đọc lại file vừa ghi để kiểm tra (lỗi Error hoặc mất entity thì không ghi đè); luôn giữ bản `.bak` của file cũ. Nếu AutoCAD vẫn báo lỗi, gửi nguyên văn thông báo của AutoCAD; tạm thời có thể Save As DXF.
- Trang in / PDF kiểu AutoCAD (lệnh PLOT / PDF): khổ giấy ISO A0–A5, ANSI A–D, Custom; hướng giấy, upside-down, lề; vùng in Extents/Display/Window (nhập số, lấy từ view hiện tại hoặc từ vùng chọn)/Layout; tỷ lệ Fit hoặc 1:n, n:1, custom; center/offset; màu Color/Monochrome/Grayscale/Pen table; lineweight theo rule, scale lineweight; linetype dash; text tô đặc; khung, plot stamp; in cả các layout thành PDF nhiều trang.
- Rule in theo layer (khớp wildcard, rule đầu tiên thắng): bỏ/in layer, ép màu, ép độ dày nét (mặc định bỏ Defpoints); pen table đổi màu → màu/độ dày xuất. Setup lưu tên, tải lại, và lưu cùng layout trong phiên.
- Layout có viewport: hiển thị Model trong khung giấy với tâm + tỷ lệ 1:n, cắt đúng khung. Có preview trực tiếp khi đổi thông số.
- Vector PDF viết trực tiếp (nén Flate, clip), không phụ thuộc thư viện ngoài.
- Chưa biên dịch trong môi trường này; cần build trên Windows và báo lỗi nếu có.

## v1.0.88
- Bỏ phần 3D (iST chỉ 2D): ẩn nhóm "3D Visualization", dòng Thickness và tọa độ Z trong bảng Properties.

## v1.0.89
- TRIM / EXTEND tương tác (TR, EX): click lên LINE, cắt đoạn giữa hai giao điểm gần nhất hoặc kéo dài tới biên kế tiếp; biên là mọi đối tượng khác (LINE, POLYLINE, CIRCLE, ARC). Esc để kết thúc. Mục tiêu hiện chỉ là LINE.
- ID (click lấy tọa độ), LIST (thông tin chi tiết đối tượng đang chọn).
- Chưa biên dịch trong môi trường này.

## v1.0.90
- Grid hết flash khi zoom/pan: grid không còn bị phóng/dịch như bitmap rồi "nhảy" khi dựng lại; nay được vẽ lại ngay tại đúng vị trí ở mỗi bước zoom (mỗi nấc lăn chuột) và mỗi khung pan, chỉ đối tượng dùng ảnh xem trước. Chưa biên dịch/đo trong môi trường này.

## v1.0.91
- Properties palette dựng lại theo bố cục AutoCAD: tiêu đề PROPERTIES, thanh nhóm tối (General / Geometry / Text / Misc) với mũi tên thu gọn ở bên phải, hàng nhãn–giá trị gọn (21 px, nhãn lùi vào, giá trị chỉ-đọc mờ), ô màu cạnh Color, Linetype scale lấy giá trị thật; bỏ nhóm 3D, Normal X/Y/Z. Chưa biên dịch trong môi trường này.

## v1.0.92
- Màu hover thống nhất toàn phần mềm: nền xanh thép đậm (#35506B) + viền sáng, icon/text chuyển trắng và nổi lên (trước đây nút không có template hiện nền xanh nhạt của Windows làm icon trắng bị chìm). Áp dụng cho mọi Button/ToggleButton (kể cả hộp thoại), ToolButton; có trạng thái nhấn và Checked. Chưa biên dịch trong môi trường này.

## v1.0.93
- Text không còn hiện thành hình chữ nhật khi zoom: ở LOD xa và khung nháp, chỉ chữ cao dưới 5 px mới vẽ thành hộp; chữ lớn hơn luôn vẽ glyph thật. Hộp proxy không còn bị lưu cache nên không "dính" lại khi zoom gần. Chưa biên dịch/đo trong môi trường này.

## v1.0.94
- Layer Properties Manager kiểu AutoCAD (lệnh LAYER/LA hoặc nút quản lý layer): bảng Status / Name / On / Freeze / Lock / Color / Linetype / Lineweight / Transparency / Plot / New VP Freeze / Objects / Description; New layer, New (VP frozen), Delete (không xóa layer 0, Defpoints, layer hiện hành hoặc đang có đối tượng), Set current, tìm theo tên, Apply/OK. Trạng thái nạp từ DWG và ghi lại vào bảng layer khi lưu (on/freeze/lock/plot/lineweight/linetype/color). Off/Freeze ẩn đối tượng; Lock không cho chọn; Plot=No và lineweight layer được dùng khi in PDF. Chưa hỗ trợ: đổi tên layer có sẵn, Undo cho thay đổi layer, linetype/transparency theo layer chưa ảnh hưởng hiển thị.
- Bộ lệnh DIM: DIMLINEAR (DLI/DIM), DIMALIGNED (DAL), DIMRADIUS (DRA), DIMDIAMETER (DDI), DIMANGULAR (DAN) theo Dim Style hiện hành (chiều cao chữ, cỡ mũi tên, extension offset/extend, gap, scale, số lẻ). Kích thước được tạo từ LINE/POLYLINE/ARC/MTEXT (không phải đối tượng DIMENSION liên kết).
- Block: block chứa đối tượng chưa hỗ trợ không còn bị mất cả block; nới giới hạn preview (20k đối tượng, 30k điểm, 4k path, 512 text mỗi INSERT; ngân sách toàn bản vẽ ×12); dynamic block có visibility state được hiển thị theo state đầu tiên (mặc định). Block động đã chỉnh state (*U) vẫn bị ẩn vì chưa giải được.
- Nạp trước geometry text/polyline/arc/circle trên luồng nền sau khung hình đầu để zoom/pan đầu tiên nhanh hơn.
- Chưa biên dịch trong môi trường này.

## v1.0.95
- Sửa lỗi biên dịch CS0136 (`ls`) trong PlotEngine.cs (lỗi MC3074 là hệ quả).
- Layout (paper space) hiển thị như AutoCAD: nền pasteboard tối, tờ giấy trắng có bóng đổ, khung vùng in nét đứt cam; khổ giấy lấy từ Page Setup của DWG (kể cả xoay) hoặc setup PLOT; màu trắng/sáng tự đổi thành đen trên giấy; Zoom Extents fit theo tờ giấy.
- Viewport: nạp Viewport của layout (vị trí, kích thước, tâm và tỷ lệ view, layer đóng băng riêng) và vẽ Model trong khung viewport, cắt đúng khung, đúng tỷ lệ. Double-click trong viewport (trên chỗ trống) để vào Model với đúng vị trí/tỷ lệ của viewport đó; dùng tab layout để quay lại.
- Chưa biên dịch trong môi trường này.

## v1.0.96
- DYN: danh sách gợi ý lệnh (hộp trắng có icon) không còn hiện ở mép trên cửa sổ; nay đặt cạnh ô DYN theo con trỏ trong khung vẽ. Thêm using thiếu trong MainWindow.Layers.cs. Chưa biên dịch trong môi trường này.

## iStCAD_1 v1.0.97 — performance pass (chưa biên dịch/đo)
- Open DWG: bỏ lượt duyệt `SelectMany` qua mọi entity chỉ để đếm; dùng tổng `block.Entities.Count` (O(số block)).
- CadSpatialIndex: 4 dictionary + danh sách entry được cấp sẵn dung lượng (không rehash khi dựng chỉ mục cho file lớn).
- Chưa đụng vào walk block/render vì không có profiler; cần dòng PERF từ DWG thật để tối ưu đúng điểm nghẽn.
- Khi viewport không có entity nào dù chỉ mục có dữ liệu: tự Zoom Extents một lần và ghi dòng `VIEW:` (center, scale, extents) vào command history để chẩn đoán.

## iStCAD_1 v1.0.98 — Grip editing (MainWindow.Grips.cs)
- Chọn đối tượng rồi bấm/kéo grip: LINE (đầu/cuối stretch, giữa move), POLYLINE (đỉnh stretch, giữa đoạn move 2 đỉnh), CIRCLE (tâm move, 4 quadrant đổi bán kính), ARC (tâm move, đầu/cuối đổi góc, giữa đổi bán kính), TEXT/MTEXT (move).
- Kéo-thả hoặc click grip (hot) → di chuyển → click đặt; Esc hủy; Undo/Redo qua ApplyEntityChanges.
- Có ghost nét đứt, rubber band, grip đỏ, số đo động (độ dài<góc, R, Δ); OSNAP hút điểm, ORTHO/POLAR theo điểm neo, giữ Alt để tắt ràng buộc. Layer khóa không có grip.

## iStCAD_1 v1.0.99 — Open DWG micro-optimizations + stage timing
- GetStableEntityId: tạo Guid trực tiếp từ handle (cùng giá trị cũ), bỏ 2 string + ParseExact cho mỗi entity (175k entity).
- MapEntity: bỏ xử lý font/đường dẫn string cho entity không phải TEXT/MTEXT.
- Thêm dòng chẩn đoán `Map stages:` (native / INSERT / DIMENSION / other proxy: số lượng + ms) để thấy chính xác ~5 s giai đoạn map nằm ở đâu.

## iStCAD_1 v1.0.100 — INSERT preview cache + progressive cold render (từ số liệu thật của file 175k entity)
- Số đo v1.0.99: map INSERT 1.569 cái = 4.517 ms (~2,9 ms/cái), native 331 ms, DIMENSION 475 ms. Khung render đầu tiên: geometry ~4,5 s, chưa được chấp nhận → viewport trống.
- Open: mỗi block + layer chỉ duyệt/tessellate MỘT lần ở tọa độ block (cache theo thread), các INSERT sau chỉ nhân ma trận (ACadSharpCadFileService.ExtractInsertPreviewContent → ApplyLocalPreview). Không cache khi bị cắt cụt/hết budget.
- Render: ngân sách 500 ms cho geometry cache-miss mỗi khung (không áp cho line/circle/arc). Hết ngân sách → vẽ khung rỗng (placeholder) cho phần còn lại, hiện ngay, rồi tự chạy tiếp các pass tiến dần (phần đã dựng nằm trong cache) tới khi đủ (tối đa 60 pass). Tránh trạng thái trống hàng giây.
- PERF in thêm dòng `PERF-BUILD` (số geometry dựng, tổng ms, entity chậm nhất, số placeholder, số pass).

## iStCAD_1 v1.0.101 — Lệnh VIEW / ZOOM (MainWindow.Views.cs)
- `VIEW`: liệt kê view đã đặt tên; `VIEW S <tên>` lưu view hiện tại, `VIEW R <tên>` (hoặc `VIEW <tên>`) khôi phục, `VIEW D <tên>` xóa. Lưu theo từng tab trong phiên làm việc (chưa ghi vào DWG).
- `ZOOM` có tùy chọn: E/A (extents), P (previous), W (cửa sổ 2 click, có khung kéo), O (đối tượng đang chọn), C x,y [cao], S n / `ZOOM 2X`/`ZOOM 0.5` (n = theo extents, nX = theo view hiện tại), IN/OUT.
- Lệnh tắt: ZW, ZO, ZA, ZC, ZS, ZOOMIN/ZI, ZOOMOUT/ZOUT.

## iStCAD_1 v1.0.102 — Nguyên nhân "rất lâu mới ra ảnh" (từ log thật)
- Log: compose+raster 14.605 ms cho 254 item; tâm Zoom Extents nhảy giữa (1,6M; -0,27M) và (4,35M; 7,3M) → có đối tượng lạc rất xa làm cả bản vẽ chỉ còn vài pixel, và các đường cực dài bị WPF tessellate toàn bộ chiều dài ngoài màn hình.
- Render: đường LINE trong chế độ direct-stream được cắt (Liang–Barsky) theo khung nhìn trước khi đưa vào StreamGeometry.
- Zoom Extents / mở file / double-click chuột giữa: dùng extents "thân bản vẽ" (bỏ 0,5% đối tượng ngoài cùng khi extents đầy đủ lớn hơn >4 lần), ghi rõ số đối tượng bị bỏ qua. `ZOOM A` / `ZA` = extents tuyệt đối.
- Open: cache bounding box của BlockRecord (Insert.GetBoundingBox đo lại cả block cho từng INSERT: 568 lần cho KCS_SNODE); thêm dòng `INSERT map breakdown` để đo bounds/visibility/native-check/walk.
- PERF-BUILD thêm: số figure direct-stream, số style, thời gian raster cuối.

## iStCAD_1 v1.0.103 — Render dày đặc + HATCH đặc
- Khung dày (>20.000 ứng viên): LINE được cắt theo khung nhìn rồi vẽ thẳng bằng Bresenham 1-px vào bộ đệm điểm ảnh (màu theo layer/ACI), bỏ hẳn bước WPF stroke 100k+ figure (nguyên nhân compose 14,6 s). Hình học, pick, snap không đổi. Khi zoom vào (ít ứng viên) vẫn vẽ vector có khử răng cưa.
- HATCH đặc/gradient ở model space được tô (độ mờ 60% để không che đối tượng bên dưới) thay vì chỉ vẽ viền (ProxyCadEntity.PreviewFilled). HATCH hoa văn (ANSI31...) vẫn chỉ vẽ viền biên — chưa sinh đường gạch.

## iStCAD_1 v1.0.104
- Khung dày: LINE nhỏ (sub-pixel) không còn bị gộp thành khối xám 2x2 px; được vẽ thành đường 1-px đúng màu layer, nên bản vẽ hiện nét thay vì các mảng vuông (theo ảnh chụp "Regenerating...").

## iStCAD_1 v1.0.105
- Log v1.0.102: INSERT map 3.614 ms trong đó bounds = 3.015 ms (walk chỉ 577 ms) → MeasureBlockBounds: đo bounding box block có memo ở MỌI cấp lồng (vendor đo lại block lồng đệ quy mỗi lần).
- Log v1.0.102: raster 9.093 ms với 162.872 figure WPF → v1.0.103/104 vẽ LINE bằng Bresenham thay thế (build bản này đã có).

## iStCAD_1 v1.0.106 — "mất đa số render / Regenerating hoài"
- Nguyên nhân: các khung "progressive" của v1.0.100 vẽ placeholder (khung rỗng) cho INSERT/TEXT chưa dựng và lặp tới 60 lần, mỗi lần dựng lại + ghép lại cả khung; cộng với INSERT proxy tạo hàng trăm StreamGeometry rời (~7 ms/INSERT).
- Proxy (INSERT/HATCH/...) dựng thành MỘT StreamGeometry nhiều figure thay vì mỗi path một đối tượng → rẻ hơn nhiều lần.
- Ngân sách dựng gấp đôi mỗi pass (0,5 → 1 → 2 → 4 s), pass thứ 5 không giới hạn → tối đa 5 pass thay vì 60.

## iStCAD_1 v1.0.107 — vùng xám "chưa render"
- Log v1.0.106: khung cuối chỉ 2 item, 163.452 LINE đã vẽ bằng pixel → vùng xám còn lại là các chấm 2x2 xám gộp từ INSERT/DIMENSION/TEXT/POLYLINE/CIRCLE nhỏ.
- Khung dày (>20.000 ứng viên): các đối tượng nhỏ (≤200 px) giờ được vẽ đúng đường nét 1-px theo màu layer: POLYLINE (đỉnh), CIRCLE/ARC (đa giác), INSERT/DIMENSION/proxy (đường preview, hoặc khung bounds nếu chỉ có bounds), TEXT nhỏ (nét baseline đúng bề rộng). Không còn dựng geometry WPF cho các đối tượng này ở zoom xa. Đối tượng lớn vẫn là vector khử răng cưa.

## iStCAD_1 v1.0.108
- Log v1.0.107: first viewport 315 ms (tốt), khung 525 item/573 figure: geometry 8 ms nhưng raster WPF phần mềm (RenderTargetBitmap) 1.120 ms (~2 ms/item do glyph chữ).
- Compose học tốc độ raster (ms/đơn vị công việc). Khung vừa mà dự đoán raster >350 ms (và <3000 đơn vị) được trả về dạng vector cho WPF render-thread/GPU (BitmapCache giữ pan/zoom rẻ); khung rất nặng vẫn raster ngoài UI thread. PERF-BUILD in thêm `learned ms/unit`.

## iStCAD_1 v1.0.109 — zoom chậm/treo nhẹ
- Log v1.0.108: raster 1.134 ms dù khung chỉ vài chục item (learned 22 ms/unit) → chi phí gần như CỐ ĐỊNH: mỗi khung tạo một STA thread mới, WPF phải khởi tạo dispatcher/kênh render của thread đó (~1 s). Thay bằng MỘT thread STA sống lâu + hàng đợi; job của khung đã hủy bị bỏ qua.
- Dựng geometry cache-miss chạy song song nhiều nhân (Parallel.For, tối đa Nhân−1) sau khi quét; cache đã có khóa. Ngân sách thời gian vẫn áp dụng (hết giờ → placeholder rỗng, pass sau dựng tiếp). Trước đó 130 miss = 784 ms tuần tự.

## iStCAD_1 v1.0.110 — file nặng treo máy
- Ngân sách preview khi mở co theo kích thước file (≤300k entity: ×12; ≤600k: ×8; ≤1,2M: ×5; lớn hơn: ×3), preview cấp trên cùng cũng co theo.
- Memory guard: nếu managed heap > 55% bộ nhớ khả dụng khi đang map, ngừng dựng preview lồng (INSERT/DIMENSION chỉ còn bounds) và ghi cảnh báo `Memory guard` vào history.
- Dựng geometry song song giới hạn min(4, nhân/2) để chừa nhân cho UI/hệ điều hành.

## iStCAD_1 v1.0.111 — bảo vệ file nặng toàn diện
- Cảnh báo RAM trước khi mở (cả Open và khôi phục): ước lượng ~70× kích thước DWG (~14× DXF) so với RAM trống (GlobalMemoryStatusEx); nếu vượt 60% RAM trống thì hỏi Yes/No.
- `System.GC.HeapHardLimitPercent=75`: heap managed không vượt 75% RAM; hết thì Open báo lỗi OutOfMemory thay vì làm cả máy paging/treo.
- Sau khi mở 3 s: một lần GC compacting (gồm LOH) để thu working set.
- Watchdog UI (MainWindow.Guard.cs): ping Dispatcher mỗi 0,5 s; UI không phản hồi >1,5 s thì ghi `UI stall: N ms` kèm bối cảnh (đang mở file?, dựng geometry/index?, lệnh cuối, heap, working set, tổng GC pause). PERF in số lần stall và stall tệ nhất.

## iStCAD_1 v1.0.112 — treo khi zoom
- Nghi phạm: nhánh "vector fallback" (v1.0.108) trả frame <3000 đơn vị công việc dưới dạng DrawingImage vector. WPF marshal geometry vector sang render thread NGAY TRÊN UI THREAD theo số figure/điểm, nên frame dày làm UI đứng khi zoom. Giờ chỉ frame rất nhỏ (<150 đơn vị) mới đi đường vector; còn lại luôn raster ở thread nền (đã bền vững, không còn chi phí khởi tạo ~1 s mỗi frame).
- Chưa có log xác nhận: nếu vẫn treo, dòng `UI stall:` (v1.0.111) sẽ cho biết đang bận gì.

## iStCAD_1 v1.0.113 — zoom với vùng chọn lớn / GC
- RenderSelectionOverlay chạy trên UI thread sau mỗi lần zoom. Với Select All (tới 25.000 đối tượng), mỗi đối tượng là một DrawGeometry vector được marshal sang render thread NGAY TRÊN UI THREAD → đứng hình. Quá 1.500 đối tượng trong khung nhìn: highlight bằng MỘT StreamGeometry các khung bounds (như xem trước chọn của AutoCAD), bỏ vẽ grip. Dưới ngưỡng giữ nguyên highlight theo hình thật.
- GCLatencyMode.SustainedLowLatency khi app chạy: tránh gen2 blocking dài lúc tương tác (ConcurrentGC đã bật).

## iStCAD_1 v1.0.114
- Log v1.0.113: UI stalls 0 → không còn UI đứng; zoom còn chậm do raster 862 ms cho khung chỉ 357 item; GC pause tổng 5.244 s (máy chỉ còn 0,9/7,9 GB trống → GC chạy ở chế độ áp lực bộ nhớ).
- Cảnh báo RAM không còn hộp thoại: ghi `Memory warning:` vào command line, vẫn mở file.
- Raster khung nặng (>=150 đơn vị) dùng EdgeMode.Aliased (nhanh hơn nhiều trong bộ vẽ phần mềm WPF).
- PERF-BUILD in thêm số lần GC gen0/1/2 để xác định GC pause đến từ đâu.

## iStCAD_1 v1.0.115 — raster 36 giây
- Log file khác: raster 36.257 ms cho khung 1.168 item + 1.122 figure (learned 24 ms/unit), 727 placeholder, GC pause 9,8 s. Nghi phạm chính: pen NÉT ĐỨT trên geometry dài/phức tạp (preview INSERT, linetype) — bộ vẽ phần mềm WPF phải tessellate từng nét đứt.
- Nét đứt chỉ áp dụng khi chu kỳ ≥ 8 px (nhỏ hơn thì nhìn như nét liền); preview proxy (INSERT/DIMENSION...) vẽ nét liền.
- Raster có timeout 5 s: quá hạn thì không chờ, không xếp hàng sau job đang kẹt (WPF không hủy được Render), hiển thị lớp pixel nhanh (đa số LINE dày nằm ở đó) và ghi nhớ chi phí cao. Không bao giờ trả ảnh vector nặng về UI thread cho khung quá tải.
- Luồng raster chạy ưu tiên BelowNormal để máy/UI vẫn phản hồi khi một frame nặng đang vẽ.

## iStCAD_1 v1.0.116 — Zoom Extents còn đối tượng bị che
- Log v1.0.115: raster chạm timeout 5 s (4.557 item + 1.983 figure, learned 4,5 ms/unit) → khung rơi về lớp pixel nhanh nên nhiều đối tượng lớn biến mất sau Zoom Extents.
- Nét đứt đã loại; chi phí còn lại nằm ở raster phần mềm WPF. Compose giờ dự đoán thời gian theo ms/unit đã học: nếu > 1,5 s thì chữ/hatch đặc (đối tượng đắt nhất: hàng nghìn cạnh Bezier + alpha) vẽ dạng khung bounds trong khung đó (kiểu draft của AutoCAD), toàn bộ bản vẽ vẫn hiện đủ. PERF-BUILD ghi `[draft: N text/hatch drawn as boxes]`.
- Timeout raster nâng 8 s.

## iStCAD_1 v1.0.117 — vùng xám
- Đối tượng dưới ngưỡng pixel được chấm 2x2 bằng MỘT màu xám cố định (180,168,150) → ở vùng dày thành các mảng xám "chưa render". Giờ chấm bằng đúng màu layer/đối tượng.

## iStCAD_1 v1.0.118
- Toolbar cạnh trái mặc định ghim (mở sẵn).
- Bỏ hộp thoại "Restore autosaved drawings" khi khởi động; chỉ ghi một dòng vào command line.
- Log v1.0.117: raster vẫn 8000 ms dù text/hatch đã thành box → chi phí không nằm ở chữ mà ở ~4 triệu điểm preview INSERT/polyline (~500 điểm/đối tượng) trong 3.680 item. Chế độ soft-raster (vẽ 1-px thẳng vào pixel buffer) giờ bật từ >1.500 ứng viên khi ở far-LOD (trước đây chỉ >20.000). Timeout raster 4 s.

## iStCAD_1 v1.0.119
- Log v1.0.118: 28.909 figure vector trong 1 style → raster 3,7 s; UI stall 3 lần (tối đa 3,5 s), GC pause tổng 8,8 s (12 lần gen2 - GC chặn cả UI).
- Khi raster khung trước >1,2 s và khung hiện tại >800 ứng viên: vẽ đối tượng MỌI kích thước (LINE/POLYLINE/CIRCLE/ARC/INSERT-preview) thẳng vào pixel buffer 1-px (soft-raster), không giới hạn 200 px. Hatch đặc và chữ lớn vẫn vector.

## iStCAD_1 v1.0.120 — nháy màn hình khi frame mới tới
- Lớp hình/lựa chọn dùng BitmapCache(1.0) khi zoom/pan rồi được GÁN LẠI một BitmapCache(dpi) mới đúng lúc ảnh mới xuất hiện → WPF bỏ surface cache và dựng lại → nháy. Giờ dùng MỘT đối tượng BitmapCache(dpi) đã freeze cho cả hai pha và chỉ gán khi khác instance.

## iStCAD_1 v1.0.121 — chế độ hiển thị theo độ nặng (RENDERMODE)
- 3 mức: Quality (file nhẹ ≤30.000 đối tượng: vector khử răng cưa, chữ/hatch/nét đứt thật, chỉ dùng pixel-plot khi khung thật sự chậm), Balanced (≤120.000), Fast (file rất nặng: pixel-plot từ sớm, raster aliased, chữ/hatch dạng khung khi dự đoán chậm).
- Auto (mặc định) chọn theo số đối tượng rồi tự nâng mức khi raster đo được chậm (>1,2 s → Balanced, >3 s → Fast); số đo reset khi mở bản vẽ mới. Lệnh `RENDERMODE AUTO|QUALITY|BALANCED|FAST` (RM) để chọn tay; PERF-BUILD ghi tier hiện tại.

## iStCAD_1 v1.0.122 — OVERKILL + FIXVIET
- `OVERKILL` (CLEANUP): xóa đối tượng lỗi/rỗng (LINE dài 0, CIRCLE/ARC bán kính 0, POLYLINE <2 đỉnh hay tọa độ NaN, TEXT rỗng), bỏ đỉnh trùng liên tiếp của polyline, xóa bản sao y hệt (LINE/CIRCLE/ARC/POLYLINE/TEXT/MTEXT cùng layer/màu/linetype, hình học trong dung sai), gộp LINE thẳng hàng chồng lên/nối đầu nhau. Dung sai tự theo kích thước bản vẽ. Đối tượng nằm xa hẳn bản vẽ (ngoài ngưỡng percentile) chỉ được CHỌN sẵn để bạn quyết định xóa. `OVERKILL CHECK` chỉ báo cáo. Một lần Undo hoàn tác toàn bộ.
- `FIXVIET` (VIETFIX): sửa chữ tiếng Việt lỗi font cũ TCVN3 (.VnTime...) và VNI sang Unicode, đặt font Arial; tự đoán bảng mã, ưu tiên TCVN3 khi hòa; `FIXVIET TCVN3|VNI` ép bảng mã; `FIXVIET CHECK` chỉ báo cáo + ví dụ trước/sau. Bảng mã viết theo trí nhớ, cần kiểm tra ví dụ.
- Soft-raster (pixel plot) bật từ 100 ứng viên khi raster đã đo chậm: log v1.0.121 có khung chỉ 198 item (~950 điểm/đối tượng) vẫn raster 1,4 s.

## iStCAD_1 v1.0.123
- Chọn crossing (kéo phải→trái): block/INSERT/DIMENSION chỉ được chọn khi vùng quét thật sự cắt/chạm đường nét hoặc chữ BÊN TRONG block (trước: chỉ cần quét vào bất kỳ chỗ nào trong khung bounds). LINE/POLYLINE/CIRCLE/ARC cũng kiểm tra giao thật thay vì chỉ bounds. Window (phải kéo trái→phải, chứa trọn) không đổi.
- MTEXT: vùng chọn tính theo xuống dòng tự động trong bề rộng MText và bước dòng ~1,67h (trước đếm mỗi dấu xuống dòng nên dòng thứ hai trở đi nằm ngoài vùng click); nếu Value rỗng thì lấy chữ từ FormattedValue sau khi bỏ mã định dạng.
- Chữ trong DIMENSION/INSERT (proxy preview) trước chỉ stroke viền nên chữ rỗng ruột; giờ tô đặc glyph (figure của StreamGeometry không bị tô, chỉ glyph và đầu mũi tên đóng được tô).

## iStCAD_1 v1.0.124 — render ưu tiên vùng quanh chuột (focus-first)
- Khi khung đầy đủ dự kiến chậm (khung trước >400 ms hoặc raster >350 ms) và đã có ảnh cũ: vẽ TRƯỚC vùng elip quanh con trỏ (~42%×52% màn hình) ở chất lượng đầy đủ — chỉ query/dựng/raster vùng đó nên nhanh — hiện như một mảng nằm trên ảnh cũ (preview phóng to), viền mờ dần bằng OpacityMask radial, fade-in 110 ms. Sau đó khung đầy đủ chạy; khi tới nơi, mảng được cross-fade biến mất 180 ms. Geometry đã dựng cho vùng focus nằm sẵn trong cache nên khung đầy đủ nhanh hơn.
- Mảng focus bị ẩn ngay khi bắt đầu zoom/pan mới; số đo tốc độ raster của khung đầy đủ không bị pass focus làm sai lệch.
- Chỉ báo "Regenerating…" chỉ hiện nếu khung kéo dài >350 ms (trước đây nháy bật/tắt ở khung nhanh).

## iStCAD_1 v1.0.125
- Log v1.0.124: GC pause tổng 12 s (17 lần gen2, 1.574 gen0). Mỗi khung cấp một buffer pixel ~8 MB (Pbgra32 toàn màn hình) → vào LOH, ép gen2. Giờ buffer được tái sử dụng (pool tối đa 3, xóa về 0 khi lấy ra).
- FIXVIET: trước chỉ sửa TEXT/MTEXT cấp trên cùng, bỏ qua chữ của DIMENSION và chữ trong block (INSERT) — nằm trong proxy preview — nên các chữ đó vẫn lỗi. Nay xử lý cả PreviewText và ProxyGraphicsTexts (chỉ đổi hiển thị; đối tượng gốc DWG không đổi). `FIXVIET INFO` in tối đa 8 chuỗi nghi ngờ kèm mã Unicode từng ký tự và kết quả chuyển đổi để chẩn đoán bảng mã khi AUTO đoán sai.

## iStCAD_1 v1.0.126 — tối ưu thêm
- Bộ nhớ đệm khung (4 khung gần nhất theo camera, theo từng bản vẽ/revision): quay lại một view (ZOOM PREVIOUS, named view, pan về, zoom ra rồi vào đúng tỉ lệ) hiện ngay không dựng lại. Khung nháp/quá hạn không được lưu.
- Khi mở file: GCLatencyMode.LowLatency (chặn gen2 chặn-toàn-bộ) rồi trả về SustainedLowLatency — log cho thấy GC pause 5–12 s, phần lớn do gen2 sao chép lại ~0,5 GB đối tượng sống lâu.
- `System.GC.RetainVM=true`: giữ bộ nhớ đã giải phóng thay vì trả OS rồi nạp lại ở khung sau.

## iStCAD_1 v1.0.127 — khung nháp pixel tức thì
- Phản hồi: vẫn khựng, chờ lâu mới nét, hình bị thiếu (đặc biệt khi zoom out: ảnh cũ thu nhỏ để lộ vùng trống trong lúc chờ).
- Stage 0: khi khung trước >120 ms, vẽ NGAY toàn bộ khung nhìn bằng nét pixel 1-px đúng màu (mọi đối tượng, mọi kích thước; hatch/chữ lớn = khung bounds) — không dựng geometry WPF, không raster: chục ms. Sau đó stage 1 vùng quanh con trỏ sắc nét, stage 2 khung chất lượng đầy đủ.
- Wheel debounce 140 → 110 ms. Bộ nhớ đệm khung được xóa cùng lúc khung đơn bị xóa (đổi layer, đổi RENDERMODE, mở/đóng).

## iStCAD_1 v1.0.128 — AutoCAD báo "2 errors found during open" với file lưu từ iStCAD
- Chưa có báo cáo RECOVER/AUDIT của AutoCAD nên nguyên nhân chưa xác định. Thêm lưới an toàn khi lưu DWG: đối tượng có giá trị không hợp lệ (NaN/Infinity ở tọa độ, bán kính, góc, cao chữ, scale, bulge) bị loại khỏi tài liệu native trước khi ghi (AutoCAD coi đó là lỗi); kiểm tra owner của entity khớp block record. Kết quả ghi vào command line sau khi lưu (`Save: removed ...`, `Save audit: ...`).

## iStCAD_1 v1.0.129 — nặng nhưng không treo máy
- Nhận xét: AutoCAD zoom file này cũng nặng nhưng không mờ/treo máy. Hai điều chỉnh giảm áp lực lên cả máy:
- Worker dựng khung chạy ưu tiên BelowNormal (UI và các ứng dụng khác luôn được ưu tiên).
- Ngân sách cache geometry theo RAM máy: ≤8,5 GB → 2,5M đơn vị (trước 4M — cache luôn chạm trần, dựng lại liên tục và chiếm hàng trăm MB), ≤16,5 GB → 4M, lớn hơn → 6M.

## iStCAD_1 v1.0.130 — dùng SkiaSharp (native, SIMD) cho nét pixel dày đặc
- Yêu cầu: dùng thêm GPU. WPF chỉ dùng GPU để ghép hình vector; các khung nặng trước đây do CPU rasterize. Bản này đưa các nét pixel dày đặc (khung nháp, đối tượng nhỏ) qua SkiaSharp: gom đoạn thẳng theo màu, vẽ một lượt DrawPoints native có khử răng cưa vào bộ đệm pixel dùng chung. Không tải được thư viện native → tự quay về Bresenham CPU (PERF-BUILD ghi "pixel lines via Skia / CPU fallback").
- Lưu ý trung thực: đây là Skia CPU (SIMD), chưa phải GPU thật. GPU thật (Skia GL hoặc D3D11) là bước sau, rủi ro cao hơn; làm khi có số đo từ PERF-BUILD.

## iStCAD_1 v1.0.131 — GPU thật (Skia OpenGL)
- Một luồng "iStCAD GPU" riêng tạo ngữ cảnh OpenGL ẩn (WGL) và dùng Skia GPU để vẽ lô đoạn thẳng dày đặc (≥3000 đoạn/khung, có khử răng cưa) rồi đọc ngược một lần vào bộ đệm pixel của khung. Điểm nhỏ đã vẽ trên CPU được đưa lên GPU làm nền trước khi vẽ.
- Mọi lỗi (không có driver/GL, timeout 2,5 s, lỗi surface) tự tắt GPU vĩnh viễn trong phiên và quay về Skia CPU rồi Bresenham. Tắt thủ công: đặt biến môi trường ISTCAD_NOGPU=1.
- PERF-BUILD hiển thị trạng thái: "GPU (Skia OpenGL, last X ms)" hoặc "GPU off (lý do)".
- Phạm vi: mới là nét pixel dày đặc/khung nháp. Hatch, chữ, vùng tô vẫn do WPF vẽ.

## iStCAD_1 v1.0.132 — rà soát xung đột tối ưu
- Xung đột 1: dọn bộ nhớ sau khi mở file dùng GC chặn + nén toàn bộ (gen2, LOH) — trái với chế độ SustainedLowLatency và làm đứng MỌI luồng (cả UI) vài giây, góp phần vào "GC pause" 7–12 s. Nay dùng gen2 nền không chặn, không nén.
- Xung đột 2: ngữ cảnh GPU chỉ khởi tạo ở khung dày đặc đầu tiên (driver khởi động làm trễ khung nháp). Nay khởi tạo sẵn ở luồng nền ngay khi cửa sổ mở.
- Lạc hậu: TieredPGO bị tắt — vòng lặp nóng (lọc, đánh dấu, raster) không được JIT tối ưu theo hồ sơ chạy thật. Bật lại (mặc định của .NET 8).
- Đã rà: các tập tra cứu (HiddenEntityIds, selection) đều là HashSet O(1); truy vấn không gian dùng R-tree; không có Dispatcher.Invoke đồng bộ hay Thread.Sleep chặn.

## iStCAD_1 v1.0.133 — click chọn text DIM/block + GC khi mở file
- Log: GC pause 14,5 s với 5.670 gen0 / 4.838 gen1 — gần như mọi gen0 đều sống sót vì là dữ liệu bản vẽ đang dựng. Khi mở file nay dùng vùng NoGC (GC.TryStartNoGCRegion) cỡ ước tính theo file (tối đa 30% RAM / 40% RAM trống / 3 GB): đọc bản vẽ không bị thu gom giữa chừng; vượt ngân sách thì runtime tự quay lại GC thường.
- Click chọn text: text trong block/DIMENSION dùng hộp trục thẳng, neo trái nên text căn giữa hoặc xoay không click trúng được. Nay dùng hộp định hướng theo đúng góc xoay, chiều rộng và điểm neo của text (CadTextExtents.WrappedTextCorners).

## iStCAD_1 v1.0.134 — lệnh VNFIX (thay FIXVIET)
- Học từ VnFontRepair_V2.vb: encoding đích lấy từ FONT hiện có của text (.VnTime → TCVN3, .VnTimeH → TCVN3 chữ hoa, VNI-* → VNI, Arial/Times… → Unicode); encoding nguồn dò theo từng đoạn bằng điểm chữ ký, mơ hồ thì BỎ QUA và báo, không đoán. Không đổi font/style.
- Bảng TCVN3 (67 chữ + ĂÂĐÊÔƠƯ hoa) và VNI (67 tổ hợp + ký tự đơn) lấy từ bảng chuẩn của file VB; chuẩn hóa NFC; ký tự không biểu diễn được trong font đích → bỏ qua, không làm mất chữ.
- MTEXT xử lý theo từng đoạn: mã định dạng, `{}` lồng nhau, đổi font `\f` giữa chừng; bỏ qua field, stack, escape số/Unicode, %%nnn.
- Chế độ: `VNFIX` (khớp font, giữ style), `VNFIX UNICODE` (sang Unicode + Arial, cho máy không có font cũ), `VNFIX CHECK` (chỉ báo), `VNFIX TCVN3|VNI` (ép nguồn), `VNFIX INFO` (chẩn đoán từng text). FIXVIET/VIETFIX vẫn gọi được.
- Text trong DIM/block (proxy) không có thông tin font style nên luôn chuyển sang Unicode.

## iStCAD_1 v1.0.135 — sửa thông số trực tiếp trên bảng Properties
- Bấm vào giá trị trong bảng Properties → gõ giá trị mới → Enter áp dụng (một thao tác Undo), Esc hoặc bấm ra ngoài thì hủy/áp dụng theo giá trị đã gõ (Esc = hủy).
- Chung (cả khi chọn nhiều đối tượng): Layer (phải là layer đã có), Color (ByLayer, tên màu, #RRGGBB hoặc R,G,B), Linetype scale.
- LINE: Start/End X-Y, Length, Angle. CIRCLE: Center X-Y, Radius, Diameter. ARC: Center X-Y, Radius, góc đầu/cuối. POLYLINE: Closed. TEXT: Insertion X-Y, Contents, Height, Rotation. MTEXT: thêm Width.
- Đối tượng trên layer khóa bị bỏ qua; giá trị không hợp lệ được báo ở command line.

## iStCAD_1 v1.0.136 — VNFIX chọn font cài sẵn + command line hiểu tiếng Việt
- VNFIX: phần lớn text lỗi là do thiếu font. Lệnh liệt kê font mà text tiếng Việt đang dùng (đã cài / CHƯA cài, encoding), rồi với từng text thử lần lượt: font của chính nó → các font khác trong bản vẽ (dùng nhiều nhất trước) → Arial / Times New Roman / Tahoma. Font đầu tiên đã cài và chuyển mã an toàn được thì chọn. Font cũ đã cài và khớp mã thì giữ nguyên. Mã `\f` trỏ font chưa cài trong MTEXT bị bỏ.
- Command line: gõ khi bộ gõ tiếng Việt còn bật vẫn hiểu — "pẻ" → per → gợi ý PERF (đảo Telex: ẻ=er, ơ=ow, ă=aw, â=aa, đ=dd); thêm từ lệnh tiếng Việt: tròn, xóa, di chuyển, sao chép, xoay, gương, cắt, kéo dài, lệch, bo góc, lớp, chữ, thuộc tính, hoàn tác, hiệu năng, sửa font… (có hoặc không dấu). Cả gợi ý lẫn khi nhấn Enter.

## iStCAD_1 v1.0.137 — DYN, COPY, FILLET/CHAMFER, STRETCH/HATCH/INSERT, Aliases, icon Save As
- DYN: sau Enter/Space ô nhập DYN ẩn ngay (gõ tiếp thì hiện lại).
- COPY: sửa lỗi gốc — bản sao giữ nguyên Id của đối tượng gốc (chỉ đổi thuộc tính EntityId của record dẫn xuất, không đổi Id gốc) nên kiểm tra an toàn luôn chặn lệnh. Nay COPY tạo bản sao mới trên model.
- FILLET/CHAMFER viết lại theo AutoCAD: chọn trước hoặc sau; click hai LINE (tự thực hiện khi đủ hai) hoặc quét window/crossing rồi Enter; gõ `R` (FILLET) / `D` (CHAMFER) trên command line rồi nhập số, hoặc `R5`, hoặc gõ thẳng số; giá trị mới nhất được nhớ (cả khi mở lại app, file %LocalAppData%\iStCAD_1\last-inputs.json). Cung tròn thật với bán kính >0; bán kính 0 nối góc. Phần giữ lại là phía được click.
- Mọi hộp thoại tham số (BREAK, ARRAY, HATCH...) mặc định bằng giá trị nhập lần trước.
- Bổ sung 3 lệnh trong danh mục chưa có chức năng: STRETCH (crossing window + điểm gốc/điểm đến), HATCH (đường song song theo góc;khoảng cách, tạo LINE, hỗ trợ đảo — nhiều biên chọn cùng lúc), INSERT (chèn một file DWG/DXF tại điểm chọn; đối tượng không hỗ trợ như block/hatch/dim bị bỏ qua và báo).
- Mô tả "Registered; tool implementation pending" trong danh mục lệnh đã thay bằng mô tả thật.
- Nút **Aliases** trên thanh menu: bảng gồm Lệnh chính / Lệnh tắt mặc định (chỉ đọc) / Lệnh tắt của user (sửa được, cộng thêm vào mặc định). File cũ tự chuyển đổi.
- Icon Save As vẽ lại: đĩa mềm + bút chì chuẩn.

## iStCAD_1 v1.0.138 — GPU: khởi động shader trước, bỏ khử răng cưa khi quá dày
- Log file 10.667 đối tượng: 238.384 đoạn qua GPU mất 437 ms (cả khung 490 ms). GPU chạy được nhưng chậm hơn kỳ vọng: nghi (1) biên dịch shader rơi vào khung dày đầu tiên, (2) đường hairline khử răng cưa tốn kém khi hàng trăm nghìn đoạn.
- Khởi động sẵn shader ngay sau khi tạo context (khung 64×64 với mọi biến thể paint). Từ 80.000 đoạn trở lên vẽ không khử răng cưa. PERF-BUILD nay tách thời gian GPU thành prepare + draw + read-back để biết đúng chỗ tốn.

## iStCAD_1 v1.0.139 — hết rác bộ nhớ khi vẽ hàng trăm nghìn đoạn
- Log file 13.500 đối tượng: 852.597 đoạn, GPU 1.546 ms, GC pause 10,7 s (gen0 7.068). Nguyên nhân chính: mỗi khung gom đoạn thẳng vào List<float> (≈14 MB, nhân đôi dần) rồi chép sang SKPoint[] (≈7 MB) — toàn bộ nằm ở large-object heap nên kéo gen2 liên tục.
- Nay các điểm nằm trong khối tái sử dụng 8.192 điểm (64 KB, không vào LOH, tối đa 16 MB giữ lại) → không còn cấp phát lớn theo khung; bước "chép sang mảng" ở luồng GPU biến mất.
- Đoạn dưới 1 pixel (phần lớn ở mức zoom xa) ghi thẳng vào bộ đệm pixel của khung thay vì gửi cho Skia/GPU: số đoạn gửi GPU giảm mạnh.
- Gồm cả thay đổi của 1.0.138 (khởi động shader trước, bỏ khử răng cưa từ 80.000 đoạn).

## iStCAD_1 v1.0.140 — Properties hiển thị font và nguyên nhân lỗi chữ
- Chọn một TEXT/MTEXT: nhóm Text trong Properties có thêm Font (đã cài / CHƯA cài, loại encoding của font), Text encoding (nhận diện từ ký tự) và Diagnosis (thiếu font / sai encoding so với font / OK) kèm gợi ý chạy VNFIX.

## iStCAD_1 v1.0.141 — VNFIX KEEP
- `VNFIX KEEP` (hoặc FIRST/GIU): lấy font của ký tự đầu làm font của text; dịch ngược toàn bộ nội dung ra Unicode chuẩn (từng đoạn tự nhận diện), bỏ các đổi font bên trong, rồi mã hoá lại theo font đó. Thiếu font trên máy thì vẫn chuyển và báo cần cài font.

## iStCAD_1 v1.0.142 — VNFIX thông minh
- VNFIX mặc định: giữ font ký tự đầu nếu máy có font đó (dịch ngược Unicode chuẩn rồi mã hoá lại theo font); font thiếu/lạ thì tự chọn font đã cài hiển thị được.
- Nhận diện mơ hồ: tin vào encoding của font tác giả chọn, chỉ khi kết quả ra toàn chữ Việt hợp lệ.
- VNFIX AUTO = luôn thay font; VNFIX UNICODE = Unicode + Arial.

## iStCAD_1 v1.0.143 — DWGCHECK
- Lệnh DWGCHECK giải thích cách tắt hộp "Foreign DWG File" của AutoCAD (DWGCHECK=0 hoặc lưu DXF); sau lần lưu DWG đầu tiên có nhắc ngắn trong command history.

## iStCAD_1 v1.0.144 — Style Manager
- "LineStyle" đổi thành "Line Style".
- STYLE / DIMSTYLE / LINESTYLE mở Style Manager 3 tab: danh sách, sửa đầy đủ thông số, Set Current, New (copy), Rename, Delete, xem trước font / nét. Đổi font text style cập nhật luôn các text đang dùng font cũ.
- Mọi form con: nền trắng, chữ đen (gồm ListBox, Tab, DataGrid), nhớ vị trí và kích thước theo từng form (window-layout.json).

## iStCAD_1 v1.0.145
- Command History tự xuống dòng (wrap) theo bề rộng khung, không còn thanh cuộn ngang.

## iStCAD_1 v1.0.146
- Layer Properties Manager: bảng đổi sang nền trắng chữ đen (trước đó nền tối chữ sáng). Mọi form con: ô bảng, dòng, tiêu đề cột, danh sách, menu đều chữ đen.

## iStCAD_1 v1.0.147 — Style Manager tinh gọn cho Việt Nam
- Giao diện tiếng Việt; danh sách font chỉ gồm font Unicode hiển thị được tiếng Việt và font .Vn*/VNI đã cài; Dim Style rút còn 5 thông số; nút "Thêm mẫu Việt Nam" (TCVN-Arial/Times/Tahoma, TCVN-1:10…1:200, nét đứt/chấm gạch/khuất).

## iStCAD_1 v1.0.148
- Sửa chữ trắng trong form con (tab, nút, mục danh sách, dropdown): style TextBlock toàn app (chữ sáng) lọt vào template; thêm DialogTheme.IsLight (thuộc tính kế thừa) để TextBlock đổi sang chữ đen trong mọi form con.
- Style Manager: vùng thông số có thanh cuộn, cửa sổ lớn hơn, hết chữ bị chồng/cắt.

## iStCAD_1 v1.0.149 — form quản lý kiểu đơn giản
- Chữ: chỉ Font + Chiều cao; Kích thước: Tỉ lệ 1:N + Số lẻ; Đường: chọn/nhập nét. Tự lưu khi chọn xong, không còn nút Lưu/Đổi tên/Thêm mẫu. Mẫu TCVN tự có sẵn; bấm đúp để dùng kiểu; 3 nút: Dùng kiểu này, Tạo mới, Xóa.
- Sửa chữ trắng trong tab/nút/danh sách (DialogTheme.IsLight).

## iStCAD_1 v1.0.150 — Quản lý kiểu dạng bảng
- Mỗi loại kiểu là một bảng nhiều cột (như Layer/Aliases): sửa trực tiếp ô (tự lưu), đổi tên bằng sửa cột Tên, ＋ Thêm kiểu (sao chép dòng đang chọn), 🗑 Xóa kiểu, ✔ Dùng kiểu này. Cột Mẫu/Xem trước hiển thị font và nét.

## iStCAD_1 v1.0.151 — thêm lệnh kiểu AutoCAD
- POLYGON, ELLIPSE, DONUT, LENGTHEN, SELECTSIMILAR, PEDIT (C/O/R/J), REVERSE, COPYCLIP/COPYBASE, CUTCLIP, PASTECLIP (dán tại điểm chọn). Có gợi ý tiếng Việt (dagiac, elip, vanhat, keodai, daochieu, saochep, dan).

## iStCAD_1 v1.0.152
- Menu Edit thực sự có menu: Undo, Redo, Cut, Copy, Paste, Erase, Select all, Select similar, Match properties, Properties, Command aliases… (nút Aliases trên thanh menu đã bỏ).
- `D` = DIMLINEAR (DIMSTYLE: DST, DDIM).
- Chọn text: thêm 4 px dung sai cho text; lệnh PICKDEBUG: click không chọn được gì sẽ báo lý do (đối tượng ẩn / nằm trên layer bị khóa).
- Text zoom quá gần bị mất/vỡ: outline chữ được dựng ở cỡ chữ lớn rồi thu nhỏ bằng transform (WPF dựng outline thô khi cỡ chữ nhỏ).
- Thuộc tính như AutoCAD: Color (ByLayer / ByBlock / màu), Linetype (ByLayer / ByBlock / tên, sửa được trong Properties), Lineweight (ByLayer / mm), đổi layer thì ByLayer theo layer mới; ghi ngược vào DWG chỉ khi giá trị thay đổi (không đổi ACI/linetype của đối tượng không sửa); MATCHPROP chép đủ.
- Lệnh chọn điểm (DIM*, TRIM, EXTEND, ID): prompt đi theo con trỏ, bắt điểm (OSNAP) và xem trước DIM (đường kích thước, mũi tên, chữ) trước khi click.

## v1.0.153
- DIM là MỘT đối tượng hoàn chỉnh (DIMLINEAR/ALIGNED/RADIUS/DIAMETER/ANGULAR): click chọn cả dim, MOVE/COPY/ROTATE/SCALE hoạt động trên cả khối; dữ liệu tham số (điểm, bán kính, kiểu dim) được lưu cùng đối tượng.
- Properties của Dim: loại, giá trị đo, Text override (nhập chữ; `<>` = giá trị đo), kiểu dim, tỉ lệ. Sửa text → dim dựng lại.
- Ghi DWG/DXF: LINEAR/ALIGNED/RADIUS/DIAMETER ghi thành DIMENSION thật + block ẩn *D; nếu ACadSharp không dựng được, hoặc ANGULAR, thì ghi thành line/polyline/MText thuần (vẫn là 1 đối tượng trong app).
- Chưa hỗ trợ: MIRROR/STRETCH/grip kéo cho Dim, DIM từ file DWG cũ vẫn là proxy (không sửa tham số được).
- CHƯA BIÊN DỊCH/CHẠY THỬ — cần build và gửi lỗi nếu có.

## v1.0.154
- MIRROR hỗ trợ Dim (một đối tượng, chữ vẫn đọc được) và Text/MText (đối xứng vị trí, không lật chữ như MIRRTEXT=0).
- EXPLODE (X) tách Dim thành line/polyline/MText rời.
- CHƯA BIÊN DỊCH.

## v1.0.155
- PLINE: sau khi chọn ≥2 điểm, Esc kết thúc lệnh và GIỮ polyline (như Enter) thay vì hủy.
- Mọi đối tượng mới (PLINE, RECTANG, ARC, POLYGON, ELLIPSE, DONUT, TEXT/MTEXT, HATCH, DIM) nhận đúng Layer / Color (ByLayer, ByBlock, màu) / Linetype hiện hành; ByLayer lấy linetype của layer. Text lấy font của Text Style hiện hành; Dim dùng Dim Style hiện hành (lưu cùng đối tượng).
- Preview (line, arc, polyline...) dùng màu hiện hành và nét đứt theo linetype hiện hành hoặc linetype của layer.
- CHƯA BIÊN DỊCH.

## v1.0.156
- Vùng status bar (OSNAP/ORTHO/…) rộng thêm ~50% về bên trái (cột 1.05* → 1.6*).
- Sửa: Dim do app tạo (đối tượng proxy có tham số) bị chặn MOVE/COPY/ROTATE với thông báo "native proxy objects"; nay cho phép. Proxy gốc từ file DWG (INSERT, DIM cũ) vẫn chỉ Erase.
- "Recovery autosave written…" chỉ là thông báo tự lưu phục hồi định kỳ, không phải lỗi.

## v1.0.157
- Dựng lại đối tượng từ DWG:
  - DIMENSION (linear ngang/dọc, aligned, radius, diameter) đọc thành đối tượng Dim có tham số (kiểu dim lấy từ DWG: cao chữ, mũi tên, tỉ lệ, số lẻ, text override). Hình hiển thị giữ nguyên cho tới khi bạn sửa; khi MOVE/COPY/ROTATE/SCALE/MIRROR hoặc sửa text thì Dim được dựng lại theo kiểu của nó và khi lưu thay thế Dim gốc bằng Dim mới. Không sửa thì file giữ nguyên Dim gốc.
  - INSERT và các proxy khác (leader, …) có hình vẽ: khi MOVE/COPY/ROTATE/SCALE/MIRROR hoặc EXPLODE được dựng lại tự động thành LINE/POLYLINE/MTEXT sửa được (INSERT bị làm phẳng, mất liên kết block). Hatch tô đặc và Dim góc từ DWG vẫn chỉ đọc/xóa.
- CHƯA BIÊN DỊCH.

## v1.0.158
- INSERT (block) từ DWG là đối tượng nguyên khối: MOVE/COPY/ROTATE/SCALE (và sửa Position X/Y, Rotation, Scale trong Properties) giữ nguyên block; khi lưu, Insert gốc được dời/xoay/tỉ lệ (kèm ATTRIB), bản COPY ghi thành INSERT mới trỏ cùng block. EXPLODE mới làm phẳng block; MIRROR block vẫn làm phẳng.
- Giới hạn: INSERT dạng mảng (MINSERT), block động (copy mất trạng thái visibility/tham số động), ATTRIB của bản COPY không được tạo.
- Sửa lỗi nhân đôi khi MIRROR Dim.
- CHƯA BIÊN DỊCH.

## v1.0.159
- Layer Properties: bỏ 3 cột cuối (New VP Freeze, Objects, Description). Chọn nhiều layer (Ctrl/Shift+click): sửa On/Freeze/Lock/Plot/Color/Linetype/Lineweight/Transparency ở một layer sẽ áp cho tất cả layer đang chọn; thanh "Selected layers" có nút Set color / linetype / lineweight, On-Off, Freeze-Thaw, Lock-Unlock, Plot.
- Combo Color hiện ByLayer: ô màu là màu của layer hiện hành. Combo Line Style: "ByLayer (tên linetype của layer hiện hành)". Tự cập nhật khi đổi layer hiện hành hoặc sửa layer.
- CHƯA BIÊN DỊCH.

## v1.0.160
- OFFSET hỗ trợ ARC (dương = ra ngoài) và polyline không có cung (dương = bên trái hướng vẽ, âm = bên phải, nối góc kiểu miter; kín hoặc hở).
- CHƯA BIÊN DỊCH.

## v1.0.161
- POLYGON (POL) = vùng khép kín: click các góc, Enter / Esc / C để đóng vùng (polyline kín, 1 đối tượng, preview có cạnh đóng). Đa giác đều cũ đổi tên RPOLYGON (RPOL).
- Combo Layer / Color / Line Style / Text Style / Dim Style trên thanh công cụ: khi đang chọn đối tượng (và không chạy lệnh) sẽ ĐỔI CHO CÁC ĐỐI TƯỢNG ĐANG CHỌN (như AutoCAD), không đổi giá trị hiện hành. Bỏ chọn (Esc) rồi chọn combo để đổi giá trị hiện hành.
- PLINE luôn tạo MỘT polyline; line rời có thể nối lại bằng PEDIT → J.
- CHƯA BIÊN DỊCH.

## v1.0.162 – lệnh pick điểm thay cho form nhập
Các lệnh trước đây mở form nay chạy theo từng bước như AutoCAD: click trong bản vẽ (có OSNAP, preview) hoặc gõ giá trị/toạ độ vào ô DYN; Enter nhận giá trị mặc định <...> (nhớ giá trị lần trước); Esc kết thúc.
- RPOLYGON: số cạnh → tâm → bán kính (click = đỉnh; gõ số + C = ngoại tiếp). POLYGON = vùng khép kín bằng click.
- ELLIPSE: tâm → đầu trục → nửa trục kia. DONUT: đường kính trong/ngoài → click tâm (lặp lại).
- LENGTHEN: lượng thay đổi → click gần đầu mút của LINE (lặp). OFFSET: khoảng cách → chọn đối tượng → click phía cần offset (có preview, lặp; hỗ trợ line/circle/arc/polyline).
- SCALE: chọn đối tượng → điểm gốc → gõ hệ số, hoặc click độ dài tham chiếu rồi click độ dài mới (có preview).
- TEXT/MTEXT: điểm đặt → chiều cao (click hoặc gõ; mặc định lấy từ Text Style) → góc quay → nội dung (preview).
- HATCH: góc → khoảng cách. ARRAY: số cột → số hàng → 2 góc ô đơn vị (preview). BREAK: click điểm đầu trên LINE → điểm sau. PEDIT: click polyline → gõ C/O/R/J; J nối tự động các LINE nối tiếp nhau.
- FILLET/CHAMFER đã chạy theo kiểu pick từ trước.
- CHƯA BIÊN DỊCH.

## v1.0.163
- Layer Properties: bỏ thanh "Selected layers". Chọn nhiều layer (Ctrl/Shift+click) rồi đổi trực tiếp trên bảng: ô On / Freeze / Lock / Plot / Color / Linetype / Lineweight / Transparency là control nằm sẵn trong ô nên 1 click đổi ngay và áp cho tất cả layer đang chọn (không làm mất vùng chọn).
- Combo Layer / Color / Line Style trên thanh công cụ hiển thị tính chất của đối tượng đang chọn (để trống nếu khác nhau; ByLayer hiện màu/linetype của layer của đối tượng); bỏ chọn thì hiện lại giá trị hiện hành. Đổi tính chất đối tượng rồi chọn lại sẽ thấy đúng tính chất đã đổi.
- CHƯA BIÊN DỊCH.

## v1.0.164 – thêm lệnh (đều chạy kiểu pick từng bước)
- ALIGN (AL): chọn đối tượng → cặp điểm nguồn/đích thứ nhất (dời) → Enter hoặc cặp thứ hai (xoay + dời), có preview.
- QSELECT (QSEL): lọc theo loại đối tượng / layer / màu (hỗ trợ * ?).
- LIST: click đối tượng → hiện loại, layer, màu, linetype, handle, chiều dài, diện tích, thông số Dim / Block…
- SPLINE (SPL): đường cong mượt qua các điểm click (lưu thành polyline mịn), Enter hoặc Esc để kết thúc.
- XLINE (XL): đường dựng hình qua 2 điểm (LINE rất dài ±100000), lặp nhiều đường qua điểm gốc.
- DIVIDE (DIV) / MEASURE: đánh dấu chia đều hoặc theo khoảng cách bằng vạch ngắn (app chưa có đối tượng POINT).
- CHƯA BIÊN DỊCH.

## v1.0.165
- Thanh cuộn dọc của toolbar bên trái: thu thành một đường mảnh 4 px (chỉ còn thumb bo tròn, không nút mũi tên).
- CHƯA BIÊN DỊCH.

## v1.0.166
- Sửa lỗi biên dịch CS0103 (thiếu using DynamicInputParser trong MainWindow.Wizard.cs).

## v1.0.167
- Layer Properties: cột đầu "Status"; cột Color hiện tên màu (White/Red/...) hoặc R,G,B (vd 80,45,25), bấm để mở bảng chọn màu (màu chuẩn + palette + nhập R,G,B).
- Cột Linetype bấm để mở hộp chọn kiểu đường có xem trước nét, nút "Load .lin…" nạp file .lin (chưa ghi vào bảng linetype của DWG).
- Cột Name giãn hết bề rộng (hết cột trống); double-click một layer = Set current.

## v1.0.168
- Giảm chớp khi zoom: không ẩn các lớp khi preview vượt ngưỡng (giữ transform hợp lệ cuối), giữ nguyên cache bitmap của lưới trong lúc zoom, dùng một BitmapCache đóng băng cho lớp preview, và không dựng lại lưới lần thứ hai khi khung hình mới về.

## v1.0.169
- Con trỏ crosshair có màu theo trục: nhánh X đỏ nhạt, nhánh Y xanh lá nhạt (kiểu AutoCAD), pickbox vẫn trắng.
- Bỏ nút "Styles…" trên thanh công cụ (vẫn mở được bằng lệnh).
- Dòng lệnh: dấu › và "Command:" căn giữa dọc, sát nhau hơn; vùng status bên phải rộng hơn (260 → 620 px).

## v1.0.170
- Chống chớp khi zoom: khi khung cũ còn phóng/thu hợp lý (0.4x–2.5x) thì giữ nguyên nó cho tới khi khung đầy đủ xong, không chen khung nháp (pixel/LOD thô) rồi mới thay khung đầy đủ.
- Text trong DIMENSION (và text trong block của dim) dùng đúng font của text style, width factor, oblique, in đậm/nghiêng, căn lề TEXT (left/center/right/middle + baseline/bottom/middle/top theo alignment point), góc xoay và attachment MTEXT, định dạng MTEXT gốc, và xoay/tỷ lệ của block cha.

## v1.0.171
- Sửa lỗi gốc của vị trí text: glyph trước đây bị đặt lệch xuống ~1–2 chiều cao chữ so với điểm chèn (nên cũng không click trúng được vì ô chọn nằm ở vị trí đúng). Nay layout theo baseline/cap-height: TEXT căn đúng left/center/right/middle và baseline/bottom/middle/top; MTEXT theo attachment 1–9 (dòng đơn dùng line box tự nhiên, nhiều dòng theo pitch 5/3).
- Sửa chiều xoay text: trước đây quay ngược chiều (CW); nay CCW như AutoCAD, cả text ngoài và text trong DIM/block.
- Ô chọn/window-select của text trong DIM/block dùng đúng góc xoay, căn lề và attachment.

## v1.0.172
- Text ATTRIB (và TEXT/MTEXT) trong block: dùng đúng alignment point, căn lề, font của text style, width factor, oblique, nên đúng vị trí như trong DWG.
- Double-click vào block có attribute mở form "Edit Attributes" (mỗi tag một ô); OK cập nhật text hiển thị và ghi lại giá trị ATTRIB vào DWG khi Save.

## v1.0.173
- Dock dòng lệnh kéo sát nhóm OSNAP (cột status theo nội dung), chừa khoảng hở 10 px (gấp đôi khoảng giữa các nút).
- Hàng dưới: ba nút ▧ ▣ ⚙ và GRID đứng sát nhau, đặt trước mức zoom (132% − + Ext. thanh trượt).

## v1.0.174
- Hộp thoại Plot làm lại theo AutoCAD: Page setup (Name/Add…), Printer/plotter (DWG To PDF + hình giấy), Paper size, Plot area (Extents/Display/Window/Layout), Plot offset (Center the plot), Plot scale (Fit to paper), Plot style table (None/acad/monochrome/grayscale/Custom + Edit… mở Plot Style Table Editor với Color 1–255, màu xuất, lineweight), Plot options, Drawing orientation, Preview…, Apply to Layout, OK/Cancel. Layer rules / pen table / layout viewport nằm trong mục Advanced.
- Tối ưu 2D một trang: mặc định A3, Extents, Fit to paper, Center, monochrome.ctb; "Auto: best fit on one page" tự chọn Portrait/Landscape theo tỉ lệ bản vẽ.
- Lineweight khi plot lấy theo đối tượng (rồi layer); pen trong style table ghi đè, 0 = dùng lineweight của đối tượng.
- Double-click block có attribute: nếu click rơi vào khoảng trống trong block đã chọn vẫn mở form; nếu block không có attribute thì báo trong command history.

## v1.0.175
- Plot: bỏ toàn bộ phần Printer/plotter; hộp thoại gọn trên một trang, không còn thanh cuộn (760×560). Layer rules / pen table / layout viewport chuyển sang nút "Advanced…". Plot area: "Current view" / "Selection" nằm cạnh What to plot, ô X1 Y1 X2 Y2 chỉ hiện khi chọn Window.
- Double-click: tự nhận diện nhấp đúp (≤450 ms, ≤6 px) ngoài ClickCount của WPF, để mở form Edit Attributes / Edit Text ổn định hơn.

## v1.0.176
- Sửa lỗi build CS0165 trong MainWindow.Plot.cs (biến `updating` khai báo trước khi dùng).

## v1.0.177
- Sửa lỗi build CS0165 (`previewTimer`, `lastSheet` khai báo trước khi hàm cục bộ Schedule dùng).

## v1.0.178
- Double-click block: thông báo rõ hơn khi block không có giá trị ATTRIB (kèm số attribute definition trong block). Log của bạn cho thấy double-click đã nhận đúng; block "11" không có ATTRIB nên không có form. Thử block có attribute thật (ví dụ tkd_opis_sztuki_035).

## v1.0.179
- Plot dialog rewritten: compact single-page, modeless, no child forms. Cards: Paper (size, margin, Auto/Portrait/Landscape), What to plot (Extents/Display/Window), Scale & position, Plot style.
- "Get Window <": hides the dialog, you pick two corners in the model (rubber-band preview); Esc returns to the dialog. "From selection" uses selected objects' bounds.
- Plot style "Custom pens": inline Color 1-255 list with output color + lineweight (replaces the broken Edit… popup).
- Removed: page setup combo, transparency, stamp, frame, hidden objects, upside-down, Advanced popup.
- Search box text renamed to "AI Assistant...".
- Not compiled/tested by the assistant.

## v1.0.180
- Fix CS0165 in Plot dialog (Schedule captured later-declared controls): segmented controls and pen editor now call it through an Action declared at the top.

## v1.0.181
- Save fix: post-write DWG validation no longer fails on unreadable ACDB_*CONTEXTDATA_CLASS / custom-object read notifications (annotation context data in the original file). Entity-count validation is unchanged.

## v1.0.182
- ACadSharp DwgObjectReader: non-graphical custom objects that fail to decode (e.g. ACDB_BLKREFOBJECTCONTEXTDATA_CLASS) are now re-read as UnknownNonGraphicalObject (Warning, file still loads/saves) and the notification includes the real exception type/message so the root cause shows in the log.

## v1.0.183
- Reader diagnostics: failures ("Could not read/decode", errors) are kept first and de-duplicated from repeated warnings; all notes are written to %LOCALAPPDATA%\iStCAD_1\reader-notes.log and the command history prints the real failures first.

## v1.0.184
- Save: BlockReferenceObjectContextData (annotation-scale data of block references) is no longer written (its dictionary entries are pruned) because the vendored DWG writer emits it in a layout its own reader cannot read back. Avoids writing questionable bytes; block annotation-scale reps are dropped on save (blocks still draw at their default scale).

## v1.0.185
- Save diagnostics: before writing a DWG the app audits owner/reactor/dictionary references that would point at objects the writer skips and writes the result to %LOCALAPPDATA%\iStCAD_1\save-audit.log (summary line in the save notes). For tracking the "79 errors found" AutoCAD reports on files saved by the app.

## v1.0.186
- AutoCAD RECOVER on a saved file reports 79 x "AcDbMText: eDwgObjectImproperlyRead". save-audit.log now also contains an MTEXT feature audit (annotative, background fill/transparency, columns, missing style, long text, xdata, ...) with sample handles so the failing MTEXT set can be identified.

## v1.0.187
- Fix: 79 MTEXT with dynamic columns were rejected by AutoCAD (eDwgObjectImproperlyRead). The DWG reader dropped the column count, so the writer wrote 0. Reader now keeps ColumnCount; writer writes a valid count and one height per column.

## v1.0.188
- Save: the DWG now stores a zoom-extents view of model space (header ModelSpaceExtMin/Max + *Active viewport center/height) so it opens showing its objects. Reported: AutoCAD opens the saved file without errors but the drawing looks empty.

## v1.0.189
- Open diagnostics: after "Loaded spaces" a "View check" line prints the active space, entity count, hidden count, view center/scale, canvas size and spatial-index extents (to find why files written by the app open as an empty view).

## v1.0.190
- FIX empty view on files saved by the app: IsNativeEntityVisible treated true-color layers (Color.Index == -1) as switched off, so every object was hidden (View check: 1,565 hidden). Save also converted every layer to a true color; now a layer color is rewritten only when it was changed.
- DWG writer: "frozen in new viewports" bit of the layer flags was written from the Frozen flag; now uses FrozenNewViewports.

## v1.0.191
- Double-click on a block with attributes now opens an AutoCAD-style Enhanced Attribute Editor: attribute list (Tag/Prompt/Value) + tabs Attribute, Text Options (text style, justification (15 modes), backwards, upside down, height, width factor, rotation, oblique angle) and Properties (layer, linetype, color, lineweight). Apply / OK / Cancel. Edited properties are written to the ATTRIB in the DWG (CadInsertAttribute extended; ReadInsertAttribute / ApplyAttributeProperties in ACadSharpCadFileService). On-screen preview follows height, rotation, width factor, oblique, justification and font; backwards/upside-down are written to the DWG but not yet previewed.
- Plot dialog: "What to plot" and "Scale & position" combined into one card; dialog 900x700 with a smaller preview; left column scrolls as a fallback; Plot style card has Save style… / Load style… (.istplot JSON: color mode, lineweights, pen table).
- Not compiled/tested by the assistant.

## v1.0.192
- Title bar quick-access icons: New / Open / Save / Save As now match Undo / Redo (26x26 button, 18x18 icon, same stroke weight 1.5 px and color #D9DCE2).

## v1.0.193
- Title bar icons redrawn as one set: Undo, Redo, New, Open, Save, Save As on the same 24-unit grid, same 28x28 buttons, 18x18 icons, same 1.5 px round-cap stroke and color, same 2 px spacing.

## v1.0.194
- Plot dialog: card renamed PLOT LAYOUT; the PLOT STYLE card now takes the remaining height of the left column so its bottom lines up with the preview (pen list grows with it, no inner scroll/overlap); Name box + Save style… / Load style… on one row at the top of the card; one 12 px font size for the whole form; paper-space layouts open taller (830 px) for the viewport card.

## v1.0.195
- New logo set in the iSt Studio orange/white tone (Assets/): iStCAD (isometric cube containing "iS", with an orange-cream dot) and iA (stylised "iA" with an AI sparkle) as SVG + PNG + ICO, plus horizontal lockups. Old files kept in Assets/previous.
- App icon (.ico) and title-bar logo replaced; the AI Assistant box shows the iA mark instead of the magnifier.

## v1.0.196
- Plot: hai nút đổi nhãn thành "Save Style" / "Load Style" (bỏ dấu "…").
- Assets: thêm các phương án logo cách điệu (iStCAD_B/C/D, iA_B/C/D) trong thư mục Assets/logo-variants; chưa áp dụng vào giao diện.

## v1.0.197
- Logo iA (AI Assistant) đổi sang mẫu khối cube 3D, chữ "i" và "A" nằm trên hai mặt cube (iA_E). Áp dụng vào ô AI Assistant ở title bar (vector XAML).
- Assets/iA_Icon.ico: icon chuẩn Windows nhiều kích thước 16/20/24/32/40/48/64/128/256 (PNG 32-bit); các cỡ ≤32 dùng bản đơn giản hóa (bỏ viền trong, nét dày hơn) để rõ nét. Cập nhật iA_Icon.svg/png, iA_Logo.svg. Bản cũ ở Assets/previous (*_v195).

## v1.0.198
- Plot (PDF + preview): chữ trong DIMENSION/INSERT/block trước đây bị vẽ thành nét viền dày (sai nét) -> nay tách line work (stroke) và chữ (tô đặc, non-zero).
- Plot: solid/gradient HATCH trước đây chỉ có viền -> nay tô đặc (even-odd để giữ đảo).
- Plot: nét lineweight 0 không còn xuống 0.02 mm (hairline mất khi in) -> tối thiểu 0.05 mm.
- PDF: dùng f* khi tô even-odd; preview dùng đúng fill rule.
- Chẩn đoán: mỗi lần Export ghi %LOCALAPPDATA%\iStCAD_1\plot-audit.log (số đối tượng plot theo loại, loại nào không có geometry = bị mất).

## v1.0.199
- Plot PDF: chữ TEXT/MTEXT (và chữ trong DIMENSION/INSERT có font) xuất thành chữ thật, nhúng font TrueType (Type0/Identity-H + ToUnicode) -> sửa/tìm/copy được trong Acrobat. Giữ đúng vị trí, chiều cao, width factor, oblique, góc xoay, căn lề (TEXT) và attachment (MTEXT nhiều dòng).
- Tự động quay về chữ vector (outline) cho trường hợp không tái tạo chính xác: MTEXT bị xuống dòng tự động, nhiều font/cỡ chữ trong một MTEXT, font .ttc/.otf/CFF hoặc font bị hạn chế nhúng, ký tự thiếu glyph.
- Preview Plot vẽ chữ thật cùng vị trí; plot-audit.log ghi số dòng chữ thật ("real text lines").

## v1.0.200
- Sửa lỗi build CS0246 (EditableText) trong PdfFontCatalog.

## v1.0.201
- Sửa lỗi build CS0103 (FontStyles/FontWeights) trong PdfFontCatalog: thêm using System.Windows.

## v1.0.202
- Plot: bấm "Window" là bắt đầu quét vùng chữ nhật ngay (2 góc, Esc để hủy); bỏ nút "Get Window <" và "From selection". Tọa độ vùng vẫn hiện bên cạnh.
- Plot: thanh trạng thái ghi rõ lý do đối tượng bị bỏ qua (hidden/off/frozen layer, layer plot flag off, Defpoints, layer rule, no geometry); rê chuột lên để xem chi tiết theo layer/loại.
- Chẩn đoán: mỗi lần cập nhật preview ghi %LOCALAPPDATA%\iStCAD_1\plot-preview.log (ghi đè), gồm vùng window, số đối tượng plot/bỏ theo loại và theo layer.

## v1.0.203
- Plot: so sánh vùng Window gốc với nội dung preview - plot-preview.log / plot-audit.log ghi: vùng window dùng, bounds hình học nằm TRONG window, bounds hình học NGOÀI window (kèm loại đối tượng); thanh trạng thái hiện thêm "N outside window".

## v1.0.204
- Plot: nét (LINE/CIRCLE/ARC/POLYLINE có bulge, đường của INSERT/DIMENSION/HATCH proxy) nay được tạo thẳng từ dữ liệu đối tượng (PlotGeometry), không còn đi qua WPF Geometry flatten/Bounds. Hatch solid tô even-odd. Chỉ còn chữ không nhúng được font mới dùng outline WPF.
- Plot: vùng Window / extents tính từ bounds của chính hình học plot.

## v1.0.205
- Lệnh mới FILTER (alias FI): một form gọn, chọn trực tiếp ngay trên form (không mở form phụ). 4 cột: Type / Layer / Color / Linetype, mỗi cột có ô tìm, "all"/"clear" và số lượng.
- Bố trí thông minh: các cột kết hợp AND, cột rỗng = bất kỳ; số lượng từng giá trị tính theo các cột còn lại, giá trị không còn xuất hiện cùng lựa chọn khác tự gập lại; có swatch màu layer/màu đối tượng.
- Phạm vi: Entire drawing hoặc Selection (vùng chọn lúc mở); kết quả: Replace / Add / Remove; "Select as I click" chọn ngay trên bản vẽ khi tick (Reset hoàn lại vùng chọn ban đầu). Form không modal nên vẫn thao tác được bản vẽ.

## v1.0.206
- Con trỏ theo ngữ cảnh như AutoCAD: khi yêu cầu chọn ĐIỂM (Specify first corner...) chỉ hiện crosshair full-screen liền (không pickbox); khi yêu cầu chọn ĐỐI TƯỢNG (Select objects..., chọn circle/arc cho DIMRADIUS...) chỉ hiện pickbox (không crosshair); khi rảnh vẫn crosshair + pickbox.
- Tooltip hướng dẫn cạnh con trỏ: lúc chờ chọn đối tượng hiện nhãn "Select objects:" đi theo chuột (các lệnh chọn điểm vẫn dùng nhãn prompt sẵn có).

## v1.0.207
- Zoom bằng con lăn: transform xem trước áp dụng ngay trong sự kiện lăn (trước đây trễ 1 nhịp timer 16 ms nên đối tượng chạy sau lưới/camera -> cảm giác trượt khỏi điểm dưới con trỏ).
- Lưới và đối tượng di chuyển cùng một khung hình; chuột lăn độ phân giải cao: lưới dựng lại tối đa 1 lần/khung, giữa các lần đi theo cùng transform zoom.
- Thời gian chờ trước khi vẽ lại nét thật tự điều chỉnh theo tốc độ dựng khung: bản vẽ nhẹ ~30 ms (nét sắc ở từng nấc lăn), trung bình 70 ms, nặng 110 ms như cũ.
- Bỏ một lần dựng lại lưới thừa khi kết thúc zoom.

## v1.0.208 - hoàn thiện nhóm lệnh vẽ / chỉnh sửa theo thao tác AutoCAD
- Lõi hình học mới (MainWindow.Curves.cs): LINE, ARC, CIRCLE, POLYLINE (kể cả đoạn cung bulge) đều là chuỗi đoạn thẳng/cung; giao điểm line-line, line-cung, cung-cung; cắt đoạn; dựng lại đối tượng giữ layer/màu/linetype.
- TRIM: cắt được LINE, ARC, CIRCLE, POLYLINE tại các giao điểm gần nhất hai bên điểm bấm (cạnh cắt = mọi đối tượng). Đối tượng kín cần >= 2 giao điểm. Shift+click = EXTEND.
- EXTEND: kéo dài đầu gần điểm bấm của LINE, ARC, POLYLINE hở (đoạn cuối là thẳng hoặc cung) tới biên kế tiếp. Shift+click = TRIM.
- BREAK: cho LINE, ARC, CIRCLE, POLYLINE; điểm bấm chọn là điểm ngắt thứ nhất; F = chọn lại điểm thứ nhất; @ = ngắt tại một điểm. Đối tượng kín: bỏ phần từ điểm 1 tới điểm 2 theo chiều đối tượng.
- EXPLODE: polyline có cung -> LINE + ARC.
- JOIN: các line thẳng hàng -> một LINE; line/arc/polyline hở chạm đầu-đuôi -> POLYLINE (giữ cung), nhiều chuỗi một lần; báo số đối tượng không nối được.
- OFFSET: polyline có cung (góc được cắt/kéo cho khớp, hở thì bo cung quanh đỉnh gốc); tùy chọn T (Through) = offset qua điểm; giữ layer/kiểu của đối tượng gốc.
- LENGTHEN: [DElta/Percent/Total] cho LINE, ARC và hai đầu POLYLINE hở; đổi đầu gần điểm bấm.
- ARC: mặc định 3 điểm (start, second, end) có xem trước; C = center, start, end (ngược chiều kim đồng hồ).
- CIRCLE: tâm + bán kính (nhớ bán kính lần trước), D = đường kính, 2P, 3P.
- ARRAY: hỏi kiểu [Rectangular/POlar]; polar: tâm, số phần tử, góc phủ (±360), xoay phần tử Y/N; thêm lệnh ARRAYRECT, ARRAYPOLAR.
- FILLET/CHAMFER: bấm một POLYLINE = bo/vát mọi góc giữa các đoạn thẳng của nó (tùy chọn Polyline của AutoCAD).
- Wizard: bước nhập hỗ trợ từ khóa tùy chọn kiểu AutoCAD ([Center], [2P/3P], [Through], [First point]).
- Chưa làm: CIRCLE TTR, TRIM/EXTEND bằng fence/crossing kéo chuột, FILLET giữa line-arc/arc-arc.

## v1.0.209 - thao tác lệnh theo AutoCAD (tiếp)
- LINE: gõ C = Close (đóng về điểm đầu, cần >= 2 đoạn), U = Undo đoạn vừa vẽ; prompt hiện đúng tùy chọn đang dùng được ([Undo], [Close/Undo]).
- PLINE (viết lại): "Specify next point or [Arc/Close/Undo]"; A = cung tiếp tuyến với đoạn trước (như AutoCAD), L = về đoạn thẳng, C = đóng, U = bỏ đỉnh cuối; Enter hoặc Esc kết thúc và giữ phần đã vẽ; có xem trước.
- COPY: chế độ nhiều bản như AutoCAD - sau mỗi bản tiếp tục "Specify second point or [Exit]" tới khi Enter/Esc/E; đối tượng gốc vẫn được chọn.
- ROTATE: "Specify rotation angle or [Copy/Reference]" - C = xoay bản sao (giữ gốc), R = góc tham chiếu rồi góc mới (gõ số hoặc bấm điểm).
- SCALE (viết lại): "Specify scale factor or [Copy/Reference]" - gõ hệ số hoặc bấm điểm (khoảng cách tới base = hệ số), C = bản sao, R = chiều dài tham chiếu rồi chiều dài mới.
- MIRROR (viết lại): chọn đối tượng trước hoặc sau, 2 điểm trục gương có xem trước, hỏi "Erase source objects? [Yes/No] <N>". Sửa lỗi gương polyline có cung (bulge sai đỉnh/sai dấu).
- ORTHO/POLAR nay áp dụng cho mọi lệnh kiểu wizard (ARC, CIRCLE, PLINE, MIRROR, SCALE, BREAK, OFFSET, ARRAY...): tính từ điểm vừa nhập trước đó.
- Giữ nguyên: MOVE (Enter ở điểm thứ hai vẫn lấy vị trí con trỏ, chưa có tùy chọn Displacement).

## v1.0.210 - đối chiếu 100 lệnh phổ biến với tài liệu AutoCAD (giai đoạn 1)
- Đã đọc tài liệu AutoCAD Command Reference và lập bảng đối chiếu toàn bộ 100 lệnh phổ biến nhất, chia 7 nhóm (Vẽ, Chỉnh sửa, Kích thước, Layer/thuộc tính, Block, Truy vấn/chọn, Hiển thị/tệp) - xem file đi kèm `docs/AUTOCAD_COMMANDS.md`. Mỗi lệnh có cú pháp prompt gốc của AutoCAD và trạng thái hiện tại của iStCAD (Khớp / Một phần / Có nhưng chưa rà / Chưa có). Kết quả: 22 Khớp, 16 Một phần, 43 Có-chưa-rà, 19 Chưa có.
- **QUAN TRỌNG - thay đổi phím tắt (alias) cho đúng chuẩn AutoCAD, có thể khác thói quen cũ:**
  - Gõ `D` nay mở **DIMSTYLE** (Quản lý kiểu kích thước) - trước đây `D` chạy DIMLINEAR, là SAI so với AutoCAD gốc.
  - Gõ `S` nay chạy **STRETCH** - trước đây `S` chạy SELECT, cũng là SAI so với AutoCAD gốc. SELECT giờ không còn alias 1 ký tự (gõ đủ "SELECT").
  - DIMLINEAR dùng alias DLI/DIMLIN, không còn D.
- MOVE/COPY/STRETCH: thêm tùy chọn **[Displacement]** - gõ D rồi nhập vector X,Y để dịch theo đúng vector đó (không phải theo điểm-tới-điểm); Enter ở "second point" dùng chính điểm gốc làm vector dịch chuyển (giống AutoCAD).
- COPY: thêm [Exit] rõ ràng khi đang đặt nhiều bản sao liên tiếp (gõ E hoặc Esc/Enter để dừng).
- TRIM/EXTEND: thêm chế độ **hàng rào (fence)** - bấm 2 điểm vào vùng trống để tạo một đường quét, mọi LINE/ARC/CIRCLE/POLYLINE cắt ngang đường này sẽ được TRIM (hoặc EXTEND nếu giữ Shift) ngay tại điểm cắt; có xem trước đường hàng rào (nét đứt).
- CIRCLE: thêm tùy chọn **Ttr (Tan Tan Radius)** - chọn 2 đối tượng cần tiếp xúc rồi nhập bán kính, giống "Specify first tangent", "Specify second tangent", "Specify radius" của AutoCAD.
- FILLET: bo tròn trực tiếp giữa 1 line + 1 arc hoặc 2 arc (trước đây chỉ có line-line và polyline-tất-cả-góc); tự cắt/kéo dài hai đối tượng tới điểm tiếp xúc rồi chèn cung bo.
- Thêm các lệnh layer kiểu AutoCAD: LAYOFF, LAYON, LAYFRZ, LAYTHW, LAYLCK, LAYULK, LAYISO, LAYUNISO, LAYMCUR, LAYCUR (tắt/mở/đóng băng/khóa layer theo đối tượng chọn, cô lập/bỏ cô lập layer, đặt layer theo đối tượng chọn làm layer hiện tại).
- Thêm **OOPS** - khôi phục lại các đối tượng vừa ERASE gần nhất.
- Thêm ORTHO, OSNAP (OS/-OSNAP), GRID như lệnh gõ tay (bật/tắt, không chỉ bấm nút).
- Thêm một loạt alias/lệnh gõ tắt đúng AutoCAD: QSAVE, REDRAW/REDRAWALL/RE/REGENALL/REA/R, RECTANG, DTEXT, MT, BHATCH/BH, CP, ME, LS, CH/MO/PROPS/DDMODIFY (Properties), DDIM, SELECTALL/AI_SELALL.
- **Lưu ý: chưa biên dịch/chạy thử lần nào** (môi trường này không có sẵn trình dịch .NET). Mọi thay đổi chỉ được kiểm tra bằng đọc code + script Python kiểm tra số dấu { } ( ) [ ] khớp nhau trên toàn bộ file đã sửa (đều khớp 0). Có 2 lệnh đã tự kiểm tra kỹ và sửa 1 lỗi cú pháp nhỏ (thiếu dấu ngoặc quanh `as string` khi dùng với `switch`): CIRCLE 3P/Ttr và SCALE Reference.
- **Việc còn lại (giai đoạn 2, theo đúng thứ tự trong docs/AUTOCAD_COMMANDS.md):** rà lại 43 lệnh đang ghi "Có, chưa rà" từng cái một cho khớp đúng prompt AutoCAD; bổ sung các tùy chọn còn thiếu cho 16 lệnh "Một phần" (ví dụ TRIM/EXTEND thiếu chọn cạnh cắt trước + tùy chọn eRase, COPY thiếu mOde/Array/Undo, OFFSET thiếu Erase/Layer/Multiple/Undo, CHAMFER thiếu Angle/Trim/mEthod/Multiple, ARRAY thiếu Path, PLINE thiếu Halfwidth/Length/Width, ARC thiếu các biến thể Start-Center-End/Angle/Length...); sau đó làm tiếp 19 lệnh "Chưa có" (RAY, POINT, BOUNDARY, REVCLOUD, WIPEOUT, DIMCONTINUE, DIMBASELINE, DIMORDINATE, QLEADER, MLEADER, DIMEDIT, DIMCENTER, LTSCALE, COLOR, WBLOCK, ATTDEF, BEDIT, UNITS, TEXTEDIT).

## v1.0.211 - đối chiếu 100 lệnh AutoCAD (giai đoạn 2a: hoàn thiện 3 lệnh "Một phần" + 2 lệnh mới)
- **OFFSET**: thêm đủ 4 tùy chọn còn thiếu - E (Erase, tự xóa đối tượng gốc sau khi offset), L (Layer, đối tượng mới theo layer hiện hành hoặc giữ layer gốc), M (Multiple, bấm liên tiếp để offset tiếp từ bản vừa tạo mà không cần chọn lại đối tượng), U (Undo, hoàn lại lần offset gần nhất trong cùng lệnh).
- **XLINE**: thêm đủ 5 tùy chọn - H (Hor, đường ngang qua điểm), V (Ver, đường thẳng đứng), A (Ang, nhập góc rồi chọn điểm), B (Bisect, chọn đỉnh góc + 2 điểm hai cạnh để vẽ đường phân giác), O (Offset, nhập khoảng cách, chọn 1 line, chọn phía để tạo đường dựng hình song song).
- **LENGTHEN**: thêm DYnamic - chọn đối tượng rồi bấm điểm mới, đầu đối tượng (line/arc/đầu polyline hở) kéo dài/co lại tới đúng điểm đó.
- **DIMCENTER (DCE)** - lệnh mới: chọn 1 ARC hoặc CIRCLE, vẽ dấu cộng nhỏ tại tâm.
- **COLOR (COL)** - lệnh mới: gõ trực tiếp tên màu (ByLayer/Red/Yellow/Green/Cyan/Blue/White), số ACI cơ bản (1,2,3,4,5,7) hoặc "R,G,B" để đổi màu hiện hành - không cần mở hộp thoại Select Color (hộp thoại cũ vẫn còn dùng được qua nút trên UI).
- Cập nhật bảng đối chiếu `docs/AUTOCAD_COMMANDS.md`: 26 Khớp / 13 Một phần / 43 Có-chưa-rà / 18 Chưa có (trước đó 21/16/43/20).
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm lại số dấu { } ( ) [ ] trên toàn bộ file sửa (OFFSET/LENGTHEN trong MainWindow.Wizard.cs, XLINE trong MainWindow.Wizard2.cs, DIMCENTER/COLOR trong MainWindow.Commands100.cs, mô tả lệnh trong MainWindow.xaml.cs) - đều khớp.
- Việc còn lại cho giai đoạn 2b (xem chi tiết trong docs/AUTOCAD_COMMANDS.md): COPY (mOde/Array/Undo), TRIM/EXTEND (chọn cạnh cắt trước, eRase), FILLET/CHAMFER (Trim/Multiple/Undo/Angle/mEthod), ARRAY (PAth), rồi mới tới rà 43 lệnh "Có, chưa rà" và các lệnh "Chưa có" còn lại (RAY/POINT cần thêm kiểu đối tượng mới - rủi ro cao hơn nên để sau cùng).

## v1.0.212 - đối chiếu 100 lệnh AutoCAD (giai đoạn 2b - ưu tiên dễ dùng, đơn giản)
- **COPY**: thêm U (Undo) khi đang đặt nhiều bản liên tiếp - bỏ bản vừa đặt mà không cần hủy cả lệnh, vẫn tiếp tục đặt bản khác từ đúng điểm gốc.
- **FILLET/CHAMFER**: thêm T (Trim/No trim - giữ nguyên hai đường, chỉ thêm cung bo/đường vát, không cắt đường cũ) và U (Undo bo/vát lần gần nhất). Bo/vát liên tiếp nhiều cặp vẫn làm được không cần gọi lại lệnh (tương đương Multiple).
- **Chủ ý KHÔNG làm thêm trong lần này** (đúng theo yêu cầu "dễ dùng, đơn giản, user-friendly" - không phải thiếu sót, mà là lựa chọn giữ lệnh gọn):
  - TRIM/EXTEND: không bắt chọn "cạnh cắt/biên" riêng như AutoCAD gốc - vẫn giữ cách hiện tại (mọi đối tượng trên bản vẽ tự động là cạnh cắt) vì ít bước hơn, dễ dùng hơn cho người mới.
  - CHAMFER: không làm Angle/mEthod (vát theo góc) - ít dùng, thêm bước sẽ rối.
  - COPY: không làm mOde (Single/Multiple) và Array (chia N bản đều giữa 2 điểm) - hành vi "đặt nhiều bản liên tiếp" hiện tại đã đủ dùng cho hầu hết trường hợp.
  - ARRAY: không làm Path (mảng theo đường cong) - phức tạp, ít cần trong vẽ 2D thông thường.
- Cập nhật `docs/AUTOCAD_COMMANDS.md` với ghi chú rõ lý do "chủ ý bỏ qua" cho từng mục trên, để không bị hiểu nhầm là thiếu sót.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm lại số dấu { } ( ) [ ] trên các file sửa (MainWindow.xaml.cs, MainWindow.ModifyCommands.cs, MainWindow.Corner.cs) - đều khớp.
- Bước tiếp theo (giai đoạn 3, vẫn theo hướng đơn giản hóa): rà nhanh một lượt các lệnh "Có, chưa rà" dễ kiểm tra nhất trước (DIST, AREA, LIST, ID, UNDO/REDO, PAN, SAVE/OPEN...), ưu tiên sửa những gì người dùng chắc chắn dùng hàng ngày; các lệnh phức tạp/ít dùng (QLEADER, MLEADER, WBLOCK, ATTDEF, BEDIT...) để lại cuối cùng.

## v1.0.213 - Mặc định tỷ lệ 1/100 + viết lại HATCH thành 1 đối tượng duy nhất
- **MẶC ĐỊNH BẢN VẼ CHUẨN TỶ LỆ 1/100 (LTSCALE)**: thêm hệ số tỷ lệ nét đứt/nét chấm toàn cục (`LTSCALE`, giống AutoCAD), **mặc định = 100** - đúng chuẩn vẽ kiến trúc/xây dựng Việt Nam khi bản vẽ dùng đơn vị thực (mm) và in ở tỷ lệ 1/100. Mọi đối tượng MỚI vẽ (line, polyline, circle, arc...) từ bản này tự động nhận hệ số 100, nét đứt/nét chấm sẽ hiện đúng kích thước, không còn bị "đặc" (quá dày) như khi dùng hệ số 1 trên bản vẽ đơn vị mm.
  - Lệnh mới **LTSCALE (LTS)**: gõ số để đổi hệ số này - đổi xong áp dụng luôn cho CẢ các đối tượng đã vẽ trước đó trong bản vẽ hiện tại (không chỉ các đối tượng vẽ sau), để toàn bộ bản vẽ luôn đồng bộ 1 hệ số duy nhất, tránh tình trạng mỗi đối tượng nét đứt khác tỷ lệ nhau.
  - Nếu bản vẽ của bạn KHÔNG theo tỷ lệ 1/100 (ví dụ 1/50, 1/200, hoặc vẽ bằng đơn vị khác mm), chỉ cần gõ `LTSCALE` rồi nhập hệ số phù hợp (thường = số mẫu của tỷ lệ, ví dụ 1/50 → 50, 1/200 → 200) - không cần sửa lại từng đối tượng.
- **HATCH (gạch mặt cắt) - viết lại hoàn toàn, chuẩn và nhẹ máy hơn:**
  - **Thay đổi hành vi quan trọng:** trước đây 1 lần gạch tạo ra RẤT NHIỀU đường LINE rời rạc (mỗi đường gạch là 1 đối tượng); từ bản này, **cả mảng gạch là MỘT đối tượng HATCH duy nhất** - giống hệt AutoCAD. Lợi ích: chọn 1 cú là chọn được cả mảng gạch (không cần window chọn hết từng đường), MOVE/COPY/ERASE/ROTATE/SCALE/MIRROR xử lý tức thì dù mảng gạch có hàng nghìn đường kẻ bên trong, file nhẹ hơn nhiều, bản vẽ load nhanh hơn. Vẫn dùng được lệnh **EXPLODE** nếu thực sự cần tách ra thành các đường LINE riêng như kiểu cũ.
  - Khoảng cách gạch mặc định giờ **tự tính theo kích thước biên** (đường chéo khung bao chia 40, làm tròn về số đẹp 1/2/5 x 10^n) thay vì luôn là 10 cố định như trước - tránh lỗi hay gặp nhất: bản vẽ theo đơn vị mm/tỷ lệ 1/100 mà gạch khoảng cách 10 thì ra hàng chục nghìn đường kẻ, treo máy. Vẫn có thể gõ khoảng cách khác tuỳ ý.
  - Thêm giới hạn an toàn chặt hơn (tối đa 20.000 đường quét / 100.000 đoạn gạch cho 1 lần HATCH) kèm thông báo gợi ý ngay một khoảng cách lớn hơn phù hợp nếu vượt quá, thay vì chỉ báo lỗi.
  - Chọn biên gạch: vẫn theo kiểu "chọn các đối tượng kín có sẵn" (đường tròn, polyline kín) như trước - **chưa làm được "pick internal point" (bấm 1 điểm bên trong để tự dò biên)** như AutoCAD gốc, vì cách hiện tại đơn giản, ít rủi ro hơn cho người mới dùng. Đây là lựa chọn chủ ý, không phải thiếu sót.
  - Mảng gạch MIRROR (lấy đối xứng): hình vẽ ra đúng (các đường gạch được lấy đối xứng điểm-từng-điểm chính xác); riêng góc gạch lưu trong dữ liệu để ghi file thì chỉ tính gần đúng - không ảnh hưởng gì trên màn hình, chỉ là chi tiết kỹ thuật khi lưu.
  - Đã bổ sung phần LƯU FILE: trước đây nếu lưu bản vẽ có HATCH mới tạo rồi mở lại, mảng gạch sẽ **biến mất** (lỗi nghiêm trọng, đã phát hiện và sửa trong chính lần này trước khi gửi). Giờ khi lưu, HATCH được ghi thành đối tượng HATCH gốc thật của AutoCAD (có biên + kiểu mẫu gạch), mở lại trong iStCAD hoặc cả AutoCAD đều thấy đúng hình. Lưu ý: sau khi lưu + mở lại file, mảng gạch sẽ ở dạng "đọc lại từ file" - các đường gạch hiển thị lại nhờ chính AutoCAD/thư viện tính toán từ kiểu mẫu + biên (không phải lại danh sách đường đã tính trước), nên hình vẫn đúng nhưng đây là lần đầu đường lưu-mở này được viết, **chưa test thực tế mở lại bằng AutoCAD thật**.
- **Vẫn chưa biên dịch/chạy thử lần nào** (môi trường này không có trình dịch .NET). Đã kiểm lại số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa (CadEntity.cs, CadDrawing.cs, CadEditor.cs, MainWindow.Rebuild.cs, MainWindow.xaml.cs, MainWindow.Wizard.cs, MainWindow.NewCommands.cs, MainWindow.ModifyCommands.cs, MainWindow.Commands100.cs, ACadSharpCadFileService.cs) bằng script riêng (tách đúng chuỗi `"..."`, `@"..."`, `$"..."`, ký tự `'...'` và comment) - tất cả đều khớp 0. Phần lưu/đọc file HATCH được viết dựa trên đọc kỹ mã nguồn thư viện ACadSharp (Hatch.cs, HatchPattern.cs...) nhưng **đặc biệt cần bạn kiểm tra kỹ**: vẽ hatch → lưu file (.dwg) → mở lại, xem mảng gạch còn đúng không, rồi báo lại nếu sai.
- Bước tiếp theo: tiếp tục rà các lệnh "Có, chưa rà" còn lại trong `docs/AUTOCAD_COMMANDS.md` theo đúng kế hoạch giai đoạn 3 đã nêu ở trên, trừ khi có yêu cầu khác.

## v1.0.214 - rà kỹ lại HATCH để chắc chắn mở được bằng AutoCAD thật
- Bạn nhắc "quan trọng là save/load được trong AutoCAD" - đã đọc lại kỹ phần ghi file DWG và DXF thật của thư viện ACadSharp (`DwgObjectWriter.Entities.cs`, `DxfSectionWriterBase.Entities.cs`) để đối chiếu từng trường dữ liệu mà HATCH mới tạo (v1.0.213) ghi ra, thay vì chỉ dựa vào đọc code định nghĩa class:
  - Xác nhận cách tính góc/khoảng cách gạch (Pattern Angle/Offset) ghi ra đúng hệ quy chiếu mà bộ ghi file DWG/DXF dùng (không bị lệch góc khi gạch nghiêng).
  - Sửa 1 chi tiết: tên kiểu mẫu gạch đổi từ "ANSI31" thành **"_USER"** - đúng quy ước AutoCAD dùng cho mẫu gạch tự định nghĩa (không phải lấy từ thư viện mẫu có sẵn), tránh làm sai tên hiển thị trong hộp thoại Properties khi mở lại bằng AutoCAD.
  - Xác nhận kiểu đường biên (Polyline boundary) và cờ đánh dấu biên (External/Polyline) ghi đúng định dạng chuẩn mà AutoCAD đọc được cho cả trường hợp 1 biên và nhiều biên lồng nhau (mảng gạch có lỗ ở giữa).
- **Lưu ý còn lại (chưa khắc phục, không ảnh hưởng AutoCAD)**: nếu bạn lưu file rồi MỞ LẠI NGAY TRONG iStCAD (không phải AutoCAD), mảng gạch sẽ hiển thị lại chỉ dưới dạng ĐƯỜNG BIÊN (không thấy các đường gạch bên trong) - đây là cách app đã xử lý từ trước cho MỌI hatch đọc từ file DWG/DXF khác (không phải lỗi mới), dữ liệu gạch thật vẫn còn đầy đủ trong file, chỉ là cách xem trước trong iStCAD chưa vẽ lại pattern. Mở bằng AutoCAD thật sẽ thấy đúng mẫu gạch như bình thường.
- **Vẫn chưa biên dịch/chạy thử lần nào** - chỉ kiểm bằng đọc kỹ mã nguồn bộ đọc/ghi file thật của ACadSharp + script kiểm tra số dấu `{}()[]` (khớp 0). Việc kiểm tra chắc chắn nhất vẫn là bạn build rồi thử: vẽ HATCH → lưu .dwg → mở bằng AutoCAD thật, xem mẫu gạch/góc/khoảng cách có đúng không, rồi báo lại.

## v1.0.215 - 3 lệnh "Chưa có": TEXTEDIT, DIMCONTINUE, DIMBASELINE
Theo lựa chọn của bạn (làm tiếp các lệnh "Chưa có"), đã làm 3 lệnh **không cần thêm loại đối tượng mới** - rủi ro thấp nhất trong nhóm còn lại, để làm chắc trước:
- **TEXTEDIT (ED)** - lệnh mới: gõ lệnh rồi chọn 1 chữ (TEXT/MTEXT) sẽ mở đúng hộp thoại sửa chữ hiện có (giống double-click); nếu đang chọn sẵn đúng 1 chữ thì mở ngay không cần bấm lại. Lặp lại để sửa nhiều chữ liên tiếp, Esc để dừng. Chưa làm Undo/Mode (ít dùng).
- **DIMCONTINUE (DCO)** - lệnh mới: nối tiếp một kích thước thẳng/song song mới ngay từ điểm gốc thứ hai của kích thước vừa tạo (hoặc bấm chọn 1 kích thước có sẵn nếu chưa tạo cái nào trong phiên này), cùng đường kích thước (cùng độ cao) - bấm điểm tiếp theo liên tục để ra 1 chuỗi kích thước liền mạch, Esc để dừng.
- **DIMBASELINE (DBA)** - lệnh mới: tương tự DIMCONTINUE nhưng luôn tính từ điểm gốc ĐẦU TIÊN của kích thước gốc, và mỗi kích thước mới tự lùi ra xa thêm một khoảng (theo chiều cao chữ + kích thước mũi tên của kiểu kích thước hiện hành) để các đường kích thước xếp chồng không đè lên nhau.
- Cả DIMCONTINUE/DIMBASELINE: **chưa làm tùy chọn Undo riêng trong chuỗi** (hoàn lại bước gần nhất mà không hủy cả lệnh) - nếu bấm sai một điểm, dùng lệnh UNDO/Ctrl+Z chung sau khi thoát lệnh. Đây là lựa chọn chủ ý để giữ đơn giản.
- Cập nhật `docs/AUTOCAD_COMMANDS.md`: dòng #42 TEXTEDIT, #50 DIMCONTINUE, #51 DIMBASELINE chuyển từ "Chưa có" sang "Một phần" (còn thiếu đúng các tùy chọn phụ nêu trên).
- Các lệnh "Chưa có" còn lại (RAY, POINT, BOUNDARY, REVCLOUD, WIPEOUT, DIMORDINATE, QLEADER, MLEADER, DIMEDIT, WBLOCK, ATTDEF, BEDIT, UNITS) cần thêm loại đối tượng mới trong model hoặc một tính năng lớn riêng (block editor, ghi file riêng...) nên để các lần sau, làm từng cái một thay vì dồn vào 1 lần để dễ kiểm tra.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm lại số dấu `{ } ( ) [ ]` trên các file sửa (MainWindow.Dimensions.cs, MainWindow.Tools.cs, MainWindow.xaml.cs) bằng script riêng - đều khớp.

## v1.0.233 - Rà tiếp các lệnh "Có, chưa rà": TEXT, MTEXT, STRETCH, PEDIT, ALIGN, REVERSE, DIVIDE, MEASURE
Tiếp tục đúng tiến trình đã làm trước đây: rà từng lệnh còn ghi "Có, chưa rà" trong `docs/AUTOCAD_COMMANDS.md`, đối chiếu với AutoCAD thật, tìm xem có lưu sai kiểu đối tượng hoặc tính sai không.

- **TEXT, MTEXT, STRETCH, PEDIT (Close/Open/Join), ALIGN, DIVIDE, MEASURE**: đã rà kỹ, không phát hiện lỗi lưu sai kiểu hay tính sai - chỉ cập nhật lại mô tả trong `docs/AUTOCAD_COMMANDS.md` cho rõ ràng, đúng với những gì app đang làm (không đổi code).
- **ĐÃ PHÁT HIỆN + SỬA 1 lỗi thật ở REVERSE**: chọn 1 đối tượng LINE (đường thẳng, không phải polyline) rồi gõ lệnh REVERSE trước đây KHÔNG làm gì cả - chỉ lặng lẽ báo "no polyline changed", dù công cụ chọn đối tượng của lệnh này (và cả gợi ý lệnh REVERSE) đều cho phép chọn LINE. Lý do: phần xử lý thật của Reverse chỉ xử lý POLYLINE, bỏ sót LINE. Đã sửa: chọn LINE rồi gõ REVERSE giờ đổi đúng 2 đầu (Start/End) của đường thẳng, giống AutoCAD thật.
- Đã kiểm lại kỹ công thức đảo chiều cung tròn (Bulge) khi REVERSE/PEDIT-R một polyline có cung - đảo đúng thứ tự đỉnh VÀ dịch đúng bulge sang đỉnh liền trước (vì bulge mô tả cung từ 1 đỉnh tới đỉnh kế tiếp) - không bị méo hình khi đảo chiều.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên file đã sửa (`MainWindow.AutoCadMore.cs`) - khớp. **Cần bạn kiểm tra**: vẽ 1 đường LINE → chọn nó → gõ REVERSE → kiểm tra bằng PROPERTIES hoặc thử các lệnh phụ thuộc hướng đường (ví dụ linetype có hướng) xem đã đổi đúng chiều Start/End chưa.

## v1.0.232 - Sửa: file KHÔNG nặng vẫn giật khi lăn chuột zoom liên tục
Bạn báo: "file ko quá nặng nhưng xử lý C# khá chậm, giật và ko chắc chắn như native CAD". Máy bạn hiện chưa cài được Rust (bị ThreatLocker chặn) nên phần Rust ở bản 231 vẫn chưa chạy - lỗi này là ở đường vẽ C# cũ.

- **Tìm ra đúng lỗi**: lúc lăn chuột zoom, app chỉ hiện 1 hình xem trước (phóng/thu nhỏ tấm ảnh cũ đã vẽ sẵn) rồi mới vẽ lại hình THẬT (nét sắc, đúng tỷ lệ) sau khi bạn NGỪNG lăn chuột (đợi "yên" 30ms với file nhẹ). Nhưng cách tính "yên" này bị RESET lại mỗi khi có thêm 1 nấc lăn chuột mới - nên nếu bạn lăn chuột liên tục (không dừng hẳn), hình THẬT không bao giờ được vẽ lại cho tới khi bạn dừng hẳn tay - suốt lúc đó chỉ thấy tấm ảnh cũ bị phóng to/thu nhỏ (hơi mờ, cảm giác "rung rinh không chắc chắn"), không giống AutoCAD (AutoCAD vẽ lại hình thật liên tục ngay cả khi đang lăn).
  - Đúng ra trong code đã có sẵn ghi chú ý định "file nhẹ phải nét ngay từng nấc, giống AutoCAD" - nhưng cách làm cũ (debounce thuần) không thực hiện đúng ý định đó khi lăn liên tục.
- **Đã sửa**: với file NHẸ (dựng hình nhanh, dưới 25ms) - ngoài việc đợi "yên" như cũ, giờ app còn tự vẽ lại hình THẬT mỗi 30ms một lần NGAY TRONG LÚC đang lăn chuột liên tục (không cần đợi dừng hẳn) - nên hình luôn nét, đúng, cảm giác liền mạch hơn, gần với AutoCAD hơn. File NẶNG (dựng hình chậm) giữ nguyên cách cũ (chỉ vẽ lại khi dừng hẳn) để không bị chậm thêm vì ép vẽ lại giữa chừng.
- **Vẫn chưa biên dịch/chạy thử lần nào** (môi trường này không có trình dịch .NET). Đã kiểm số dấu `{ } ( ) [ ]` trên file đã sửa (`MainWindow.xaml.cs`) - khớp. **Cần bạn kiểm tra**: mở lại đúng file bạn thấy giật, lăn chuột zoom liên tục (không nhả tay giữa chừng) xem hình có cập nhật mượt/nét liên tục trong lúc lăn không, thay vì chỉ "chốt" nét lúc dừng tay.

## v1.0.231 - Bắt đầu lõi Rust xử lý file nặng (zoom mượt + chuẩn)
Theo đúng yêu cầu "tạo model rust xử lý các file nặng để zoom mượt và chuẩn", và 2 lựa chọn bạn đã xác nhận: (1) làm CẢ 2 phần - chỉ số không gian (spatial index) VÀ phần vẽ (rasterizer), (2) bạn đồng ý tự cài Rust trên máy để build ra file .dll.

- **Thư mục mới `native/istcad_native/`**: 1 dự án Rust độc lập, biên dịch ra `istcad_native.dll`. Gồm:
  - **Chỉ số không gian (spatial index)**: cây BVH (bounding-volume hierarchy) tự viết từ đầu, tách đôi theo trục dài hơn tại trung vị (median-split) - tìm nhanh "những đối tượng nào đang nằm trong khung nhìn hiện tại" mà không cần quét qua toàn bộ hàng trăm nghìn đối tượng mỗi khung hình.
  - **Phần vẽ (rasterizer)**: vẽ đường thẳng trực tiếp vào vùng nhớ điểm ảnh (pixel buffer) bằng thuật toán khử răng cưa (antialiasing) Xiaolin Wu - vì tốc độ Rust nhanh hơn, có thể giữ nét mượt (không răng cưa) ngay cả với bản vẽ rất nặng, khác với đường vẽ nhanh hiện tại của app phải bỏ khử răng cưa để kịp tốc độ khi bản vẽ quá nhiều đối tượng.
- **Cố ý KHÔNG dùng bất kỳ thư viện Rust ngoài nào** (không dùng crate có sẵn như `rstar`) - để dự án Rust này build/test được hoàn toàn không cần mạng, và để môi trường này (sandbox không có Internet tới crates.io) có thể tự biên dịch + chạy thử (`cargo test`) ngay tại đây, kiểm tra đúng trước khi giao cho bạn - đã chạy, cả 11 bài test (chỉ số không gian + phần vẽ + lớp giao tiếp FFI) đều PASS.
- **Phần vẽ hình học (bulge/cung tròn...) vẫn nằm hoàn toàn ở phía C#** - mã Rust chỉ nhận vào danh sách điểm ĐÃ được tính sẵn (tessellated) từ `PolylineTessellator` quen thuộc, không tự tính cung tròn. Đây là chủ ý: đúng công thức bulge chỉ có 1 nơi duy nhất, tránh lặp lại đúng loại lỗi "cung tròn bị vẽ thẳng" đã gặp liên tiếp 3 lần ở bản 229/230.
- **Tự động hoá build trong Visual Studio**: đã thêm 1 bước MSBuild (`BuildNativeEngine`) vào `iStCAD_1-WPF.csproj` - mỗi lần Build, nếu máy có cài `cargo` (Rust) sẽ tự chạy `cargo build --release` và tự copy `istcad_native.dll` vào đúng thư mục chạy app; nếu CHƯA cài Rust, bước này chỉ in 1 dòng nhắc, KHÔNG làm hỏng build - app vẫn chạy bình thường không cần Rust.
- **Lớp giao tiếp C#** (`NativeEngine.cs`, mới): gọi sang Rust qua P/Invoke, có "lưới an toàn" đầy đủ - nếu file .dll chưa có/chưa build/sai kiến trúc, tự coi là "không dùng được" thay vì làm sập app.
- **Lệnh mới `NATIVESTATUS` (hoặc `NATIVE`)**: gõ lệnh này để xem lõi Rust đã build/load được chưa, kèm 1 bài tự kiểm tra nhanh (tạo thử 1 tam giác, dò vùng nhìn, đọc lại ranh giới, vẽ thử 1 khung hình) - báo rõ kết quả hoặc lý do chưa dùng được.
- **QUAN TRỌNG - phạm vi lần này CHƯA bao gồm**: lõi Rust **CHƯA được nối vào đường vẽ chính** của app (chưa thay thế phần vẽ nhanh "soft segment"/`SoftSegmentBatch` hay chỉ số không gian `CadSpatialIndex` hiện tại). Đây là lựa chọn an toàn có chủ ý: môi trường viết mã này không có trình biên dịch .NET, không dựng được file .dll thật trên Windows, nên chưa có cách nào kiểm chứng việc truyền dữ liệu qua P/Invoke (bố cục struct, ghim mảng...) có đúng thật 100% hay không. Nối thẳng vào đường vẽ chính (đường DUY NHẤT hiện đang chạy tốt) trước khi chắc chắn điều đó sẽ quá rủi ro.
- **Các bước tiếp theo, sau khi bạn build và chạy thử**:
  1. Cài Rust (https://rustup.rs) nếu máy chưa có.
  2. Build lại solution trong Visual Studio như bình thường (bước mới sẽ tự chạy, không cần làm gì thêm).
  3. Gõ lệnh `NATIVESTATUS` trong iStCAD, xem có báo "Self-test OK" không.
  4. Báo lại kết quả - nếu OK, bước tiếp theo sẽ nối lõi Rust vào thật sự đường vẽ chính (thay cho phần vẽ nhanh hiện tại) để tăng tốc zoom trên file nặng.
- Đọc thêm chi tiết kỹ thuật đầy đủ tại `native/istcad_native/README.md`.
- **Vẫn chưa biên dịch/chạy thử phía .NET/WPF lần nào** (môi trường này không có Visual Studio/trình dịch .NET). Phần Rust ĐÃ được biên dịch và chạy thử thật (`cargo test`, `cargo build --release`) ngay trong môi trường này - 11/11 test PASS, không còn cảnh báo (warning) nào. Đã kiểm số dấu `{ } ( ) [ ]` trên các file C# mới/sửa (`NativeEngine.cs`, `MainWindow.NativeStatus.cs`, `MainWindow.Views.cs`, `MainWindow.xaml.cs`) - khớp 0, và kiểm tra cú pháp XML của `iStCAD_1-WPF.csproj` hợp lệ. **Cần bạn kiểm tra đúng theo 4 bước ở trên.**

## v1.0.230 - Sửa tiếp: cung tròn trong polyline NẰM TRONG BLOCK vẫn bị vẽ thẳng
Bạn báo file thật (`Mr._Tuong_23.11.2026.dwg`, polyline trong block `iDOOR_7657DFC6` - rất có thể là cung cửa/door swing) vẫn chưa đúng dù đã sửa ở bản 229.

- **Lý do bản 229 chưa đủ**: bản 229 sửa 2 chỗ (vẽ polyline bình thường + đường vẽ nhanh/draft), nhưng cả 2 chỗ đó chỉ áp dụng cho polyline **ở ngoài block (top-level)**. Polyline **nằm bên trong 1 block** (như trong block `iDOOR_7657DFC6` của bạn) được app xử lý theo đường HOÀN TOÀN KHÁC: để vẽ nhanh (block có thể được chèn/INSERT hàng trăm lần), app dựng sẵn 1 "hình xem trước" (preview) cho nội dung bên trong block rồi dùng lại cho mọi lần chèn, thay vì biến từng polyline bên trong thành đối tượng polyline đầy đủ như bình thường. Hàm dựng hình xem trước này (`GetPolylinePreviewPoints`) chỉ nối thẳng các đỉnh, **cũng bỏ qua bulge (cung tròn)** - đúng y lỗi cũ, nhưng nằm ở chỗ thứ 3 app chưa sửa tới.
- **Đã sửa**: hàm dựng hình xem trước của block giờ tính đúng cung tròn (cùng công thức bulge dùng ở mọi chỗ khác) trước khi nối điểm.
- Đã rà lại, đây là hàm DUY NHẤT trong toàn bộ pipeline xử lý polyline nằm trong block, nên không còn chỗ thứ 4 nào khác bị sót nữa (tính luôn 3 chỗ: vẽ thường, vẽ nhanh/draft, và trong block - cả 3 giờ đều tính đúng cung tròn).
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên file đã sửa (`ACadSharpCadFileService.cs`) - khớp. **Cần bạn kiểm tra lại đúng file `Mr._Tuong_23.11.2026.dwg`**, xem polyline trong block `iDOOR_7657DFC6` đã hiện đúng cung tròn chưa.

## v1.0.229 - Sửa lỗi: cung tròn trong polyline bị vẽ thành đường thẳng
Theo ý "cung tròn trong polyline của native cad bị thể hiện đường thẳng":

- **Tìm ra đúng lỗi**: app có 1 đường vẽ nhanh riêng (gọi là "soft segment", dùng khi bản vẽ nhiều đối tượng/đang vẽ nháp (draft), xử lý qua SkiaSharp cho nhanh) - đường vẽ nhanh này nối thẳng các đỉnh (vertex) của polyline với nhau, **bỏ qua hoàn toàn thông tin cung tròn (bulge)** của từng đoạn - mọi nơi khác trong app (vẽ bình thường, xuất PDF, bắt điểm...) đều tính đúng cung tròn, chỉ riêng đường vẽ nhanh này là sót. Vì vậy, polyline có cung tròn (kể cả polyline đọc từ file AutoCAD thật, kể cả polyline tự vẽ trong iStCAD) hiển thị bị thẳng ra khi bản vẽ rơi vào chế độ vẽ nhanh này (bản vẽ phức tạp - rất hay gặp với file CAD thật, nên đúng như bạn mô tả).
- **Đã sửa**: đường vẽ nhanh giờ tính cung tròn trước (dùng đúng công thức bulge đã dùng ở mọi chỗ khác trong app) rồi mới vẽ, nên cung tròn hiện đúng hình dạng cong ở cả chế độ vẽ nhanh lẫn vẽ thường.
- Nhân tiện rà thêm các chỗ khác trong app có xử lý polyline cung tròn: lệnh AREA/DIST đo polyline có cung tròn thì đã tự báo rõ "(straight polyline edges only)" từ trước (chỉ đo theo đường thẳng, có ghi chú sẵn, không phải lỗi ẩn) - không cần sửa thêm.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên file đã sửa (`MainWindow.xaml.cs`) - khớp. **Cần bạn kiểm tra**: mở file có polyline cung tròn (đặc biệt file AutoCAD thật, bản vẽ phức tạp/nhiều đối tượng) xem cung tròn đã hiện đúng cong chưa, cả lúc đứng yên lẫn lúc đang zoom/pan.

## v1.0.228 - Nét vẽ đỡ răng cưa hơn (bạn đã trả lời: "răng cưa" + "lăn chuột zoom giật")
Bạn chọn 2 ý: **nét vẽ bị răng cưa (không liên quan tốc độ)** và **lăn chuột zoom bị giật/lag**. Mình đã xem kỹ phần code dựng hình (đã có khá nhiều lớp tối ưu zoom/pan có chủ ý, dễ vỡ nếu sửa mò mà không chạy thử được) và tìm ra đúng 1 chỗ liên quan trực tiếp đến "răng cưa":

- **Nguyên nhân nét răng cưa**: app có 3 mức chất lượng hiển thị tự động theo độ phức tạp bản vẽ (lệnh `RENDERMODE`: Quality/Balanced/Fast) - mức "Balanced" (mặc định cho file cỡ vừa) tự chuyển sang vẽ **không khử răng cưa (aliased)** ngay khi khung hình đang hiện có **từ 150 đối tượng trở lên** - một con số khá thấp, rất dễ gặp ở bản vẽ bình thường (không cần file lớn), nên nét bị răng cưa ngay cả khi KHÔNG zoom/pan, chỉ đứng yên nhìn thôi.
- **Đã sửa**: nâng ngưỡng này lên **400** - khung hình vừa phải giờ vẫn giữ nét mượt (có khử răng cưa); bản vẽ thực sự nặng vẫn tự chuyển răng cưa để giữ tốc độ như trước (không đổi gì ở mức "Fast" cho file rất lớn, và máy chạy chậm vẫn tự nâng mức như cũ) - chỉ nới rộng đúng phần ranh giới đang quá thấp.
- **Nếu vẫn thấy răng cưa** ở 1 bản vẽ cụ thể (bản vẽ đó chắc đang ở mức "Fast" do quá nhiều đối tượng): gõ lệnh `RENDERMODE QUALITY` (hoặc `RM Q`) để ép luôn mượt nhất có thể (đổi lại là có thể chậm hơn một chút với file rất lớn) - lệnh này vốn đã có sẵn, không phải mới.
- **Về "lăn chuột zoom bị giật/lag"**: mình đã xem kỹ phần xử lý lăn chuột (debounce, preview bằng bitmap-transform, đồng bộ lưới...) - đã được tối ưu khá kỹ từ trước, không tìm thấy lỗi cụ thể nào rõ ràng để sửa mà không chạy thử trực tiếp được (ở đây không có máy Windows để đo). Có khả năng phần "giật" bạn thấy liên quan đến chính việc đổi qua lại giữa mượt/răng cưa ở trên (chuyển đột ngột trong khi zoom) - **nếu sau bản này vẫn còn thấy giật, báo lại giúp mình**: bị ở file nào (file lớn hay nhỏ), zoom nhiều nấc liên tục hay zoom 1-2 nấc cũng giật, máy có bị đứng/CPU cao lúc đó không - để mình khoanh đúng chỗ, tránh sửa mò vào phần đang chạy ổn.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên file đã sửa (`MainWindow.xaml.cs`) - khớp.

## v1.0.227 - Đọc được POLYLINE kiểu cũ có cung tròn (arc) từ file AutoCAD thật
Theo ý "chưa có polyline with arc":

- **Lệnh PLINE của iStCAD vốn đã vẽ được polyline có cung tròn** (gõ `A`/`ARC` khi đang vẽ PLINE để chuyển sang vẽ cung, `L`/`LINE` để quay lại vẽ thẳng - xem gợi ý lệnh PLINE), và việc lưu/đọc lại cung tròn trong polyline do chính iStCAD tạo cũng đã hoạt động đúng từ trước. **Gặp đúng vấn đề khi mở 1 file .dwg/.dxf thật từ AutoCAD (hoặc phần mềm CAD khác) có kiểu POLYLINE "cũ"** (AutoCAD gọi là heavyweight POLYLINE, khác với LWPOLYLINE hiện đại mà iStCAD đã hỗ trợ đầy đủ từ trước) - kiểu đối tượng này trước giờ app **chưa nhận diện được**, nên bị rơi vào nhóm "chưa hỗ trợ" (chỉ hiện khung bao ngoài, không sửa được, mất hết các đoạn cung tròn khi hiển thị).
- **Đã sửa**: nhận diện thêm kiểu `Polyline2D` (POLYLINE cũ) khi đọc file, chuyển đúng thành cùng 1 loại đối tượng polyline có cung tròn mà iStCAD đã dùng cho LWPOLYLINE - sửa được, di chuyển/xoay/scale/explode được, cung tròn hiện đúng hình dạng.
- **Khi lưu lại**: polyline đọc theo cách này sẽ được lưu ra lại thành LWPOLYLINE (kiểu polyline hiện đại, AutoCAD vẫn đọc đúng, hình dạng/cung tròn giữ nguyên) chứ không lưu lại đúng y kiểu POLYLINE cũ - đây là lựa chọn chủ ý để đơn giản hoá (giống cách TEXT/ATTDEF cũng được chuẩn hoá tương tự ở các bản trước), không ảnh hưởng gì đến hình dạng bản vẽ.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên file đã sửa (`ACadSharpCadFileService.cs`) - khớp. **Cần bạn kiểm tra**: mở 1 file .dwg/.dxf có sẵn polyline kiểu cũ với cung tròn (nếu bạn có file như vậy) xem giờ đã hiện đúng cung tròn và sửa được chưa.

### Về "khắc phục thêm vấn đề zoom và độ mượt"
Mình đã xem lại kỹ phần code xử lý zoom/pan (đã có khá nhiều lớp tối ưu sẵn: preview bằng bitmap-transform khi đang lăn chuột để mượt, debounce rồi mới build lại hình thật, đồng bộ lưới với camera...) - đây là phần code khá tinh vi và dễ vỡ nếu sửa mà không đo đạc/test được trực tiếp (ở đây không có máy Windows để chạy thử). Để sửa đúng chỗ bạn thấy chưa mượt, mình cần biết rõ hơn: zoom bị giật ở trường hợp nào (file lớn? zoom liên tục nhiều nấc? vừa zoom vừa pan?), hay "độ mượt" bạn nói là nét vẽ bị răng cưa (anti-alias) chứ không phải tốc độ khung hình? Mình để câu hỏi riêng ở cuối tin nhắn, trả lời giúp để lần sau sửa đúng trọng tâm, tránh sửa mò vào chỗ đang chạy ổn.

## v1.0.226.4 - Sửa lỗi chạy App bị FileNotFoundException (không phải lỗi biên dịch - khác hẳn 3 lỗi trước)
Bạn báo: build xong (không còn lỗi biên dịch), nhưng **chạy app bị crash ngay khi mở** với lỗi:
`System.IO.FileNotFoundException: Could not load file or assembly 'iStCAD_1, Version=1.0.0.56...'` tại `App.InitializeComponent()`.

- **Nguyên nhân (lỗi có từ TRƯỚC, không phải do VIEWPORT)**: file `iStCAD_1-WPF.csproj` có cơ chế tự tăng số build (`build-number.txt`) và dùng số đó làm cả `AssemblyVersion` **lẫn** `FileVersion`. Với app WPF, `AssemblyVersion` **không được phép tự đổi theo mỗi lần build** - vì WPF "đóng cứng" version của chính assembly vào các resource XAML đã biên dịch (BAML, trong App.xaml/MainWindow.xaml...) ngay lúc biên dịch markup. Nếu số build tăng thêm 1 lần nữa ở 1 bước build khác trong CÙNG 1 lần Build/Run (rất dễ xảy ra vì Visual Studio build project nhiều lần: 1 lần build nền cho IntelliSense, 1 lần build thật, rồi 1 lần nữa khi bấm Run), thì file .exe cuối cùng sẽ có version KHÁC với version đã đóng cứng vào các XAML đã biên dịch trước đó → mở app lên là crash ngay, dù biên dịch không báo lỗi gì.
- **Đã sửa**: tách riêng - `AssemblyVersion` cố định `1.0.0.0` (không đổi), chỉ còn `FileVersion` (thông tin hiển thị, không ảnh hưởng gì đến việc nạp resource) mới tiếp tục tự tăng theo build.
- **Quan trọng - cần làm 1 lần**: vì bản trước đã build với cơ chế lỗi, các file biên dịch cũ (`bin/`, `obj/` của tất cả project) có thể vẫn còn đóng cứng version cũ, lẫn với version mới sau khi sửa. **Trước khi build bản này, xoá hết các thư mục `bin` và `obj`** trong `iStCAD_1-WPF`, `iStCAD_1.Core`, `iStCAD_1.Application`, `iStCAD_1.Infras`, `iStCAD_1.Rendering` (hoặc dùng Visual Studio: chuột phải Solution → Clean Solution, rồi Rebuild Solution) để chắc chắn build lại từ đầu, không dùng lại file cũ.
- **Vẫn chưa chạy thử lại được ở đây** (môi trường này không có Windows/WPF runtime) - chỉ sửa đúng theo lỗi và nguyên nhân đã phân tích. **Cần bạn Clean + Rebuild rồi chạy lại giúp.**

## v1.0.226.3 - Sửa tiếp lỗi biên dịch CS1061 (RotationRadians) do bạn báo lại
- `CS1061: 'CadAttributeDefData' does not contain a definition for 'RotationRadians'` tại `MainWindow.Blocks.cs` dòng 206 - lỗi có từ TRƯỚC (phần BLOCK/ATTDEF, không phải do bản VIEWPORT): record `CadAttributeDefData` chỉ có field `RotationDegrees` (không có `RotationRadians`) - đúng 3 dòng dưới (208-209) đã dùng đúng `def.RotationDegrees`, riêng dòng 206 gõ nhầm thành `RotationRadians`. Đã sửa lại đúng thành `def.RotationDegrees * Math.PI / 180`.
- Đã rà toàn repo tìm thêm chỗ nào gọi `RotationRadians` trên `CadAttributeDefData` - không còn chỗ nào khác.
- **Vẫn chưa biên dịch/chạy thử lại được** - chỉ sửa đúng theo lỗi bạn gửi. **Cần bạn build lại giúp xem hết lỗi chưa.**

## v1.0.226.2 - Sửa tiếp lỗi biên dịch CS1061 (PreviewTextPosition) do bạn báo lại
Bạn báo tiếp lỗi:
- `CS1061: 'CadEntity' does not contain a definition for 'PreviewTextPosition'` tại `ACadSharpCadFileService.cs` dòng 1419 (trong `EnsureBlockRecordsExist`, xử lý ATTDEF trong BLOCK) - lỗi có từ TRƯỚC (phần BLOCK/ATTDEF), không phải do bản VIEWPORT: biến `member` được so khớp `is ProxyCadEntity { AttributeDef: { } attDef }` nhưng **không gán vào 1 biến kiểu `ProxyCadEntity`**, nên dòng ngay sau vẫn dùng `member` với kiểu gốc `CadEntity` (không có `PreviewTextPosition`) → đã sửa thành `is ProxyCadEntity { AttributeDef: { } attDef } attDefProxy` và dùng `attDefProxy.PreviewTextPosition`.
- `CS0006: Metadata file '...iStCAD_1.Infras.dll' could not be found` - chỉ là lỗi dây chuyền (project `iStCAD_1.Infras` build lỗi ở trên nên không ra được .dll cho `iStCAD_1-WPF` tham chiếu) - sửa lỗi CS1061 xong, lỗi này tự hết.
- Đã rà toàn bộ file `.cs` trong repo tìm đúng dạng lỗi này (so khớp `is ProxyCadEntity { ... }` mà không gán biến, rồi vẫn dùng biến gốc) - không còn chỗ nào khác bị.
- **Vẫn chưa biên dịch/chạy thử lại được** - chỉ sửa đúng theo lỗi bạn gửi. **Cần bạn build lại giúp xem hết lỗi chưa.**

## v1.0.226.1 - Sửa lỗi biên dịch CS0246 (Point2) do bạn báo lại khi build thật lần đầu
Bạn build bản v1.0.226 trong Visual Studio và báo lỗi:
- `CS0246: The type or namespace name 'Point2' could not be found` tại `CadDrawing.cs` dòng 133 (record `CadBlockDefinition`, dùng kiểu `Point2` nhưng file này thiếu `using iStCAD_1.Core.Geometry;` - lỗi có từ TRƯỚC, không phải do bản VIEWPORT, chỉ là lần build thật đầu tiên mới lộ ra) → đã thêm `using iStCAD_1.Core.Geometry;` vào đầu `CadDrawing.cs`.
- `MC3074: The tag 'CadViewportControl' does not exist...` tại `MainWindow.xaml` dòng 345 - đây là lỗi **dây chuyền** từ lỗi CS0246 ở trên: khi project `iStCAD_1.Core` build lỗi, các project phụ thuộc nó (`iStCAD_1.Rendering`, rồi `iStCAD_1-WPF`) không build được theo, nên XAML không thấy được control `CadViewportControl` (control này vẫn tồn tại bình thường trong `iStCAD_1.Rendering/CadViewportControl.xaml.cs`, không có gì sai ở đó) → sửa lỗi CS0246 ở trên xong, lỗi này sẽ tự hết, không cần sửa gì thêm.
- Đã rà toàn bộ các file khác trong `iStCAD_1.Core/Model/` dùng `Point2`/`Point3` để chắc chắn không còn file nào thiếu `using` tương tự.
- **Vẫn chưa biên dịch/chạy thử lại** (môi trường này không có compiler .NET) - chỉ sửa đúng theo lỗi bạn gửi và kiểm tra bằng mắt + kiểm số dấu ngoặc. **Cần bạn build lại giúp xem hết lỗi chưa**, rồi mới qua bước kiểm tra chức năng VIEWPORT ở mục v1.0.226 dưới đây.

## v1.0.226 - VIEWPORT (MVIEW): lệnh mới (cửa sổ nhìn Model từ tab Layout, lưu thành đối tượng VIEWPORT thật)
Theo yêu cầu "Làm viewport nhé":

- **Cách dùng**: chuyển sang 1 tab **Layout** (không dùng được ở tab Model, giống đúng MVIEW thật của AutoCAD - gõ lệnh ở tab Model sẽ báo cần chuyển tab trước) → gõ `VIEWPORT` (hoặc `MVIEW`) → bấm góc đầu → bấm góc đối diện - **chỉ cần kéo 1 hình chữ nhật trên giấy**, gọn hơn AutoCAD gốc (vốn có nhiều tuỳ chọn Fit/2 viewport/3 viewport/4 viewport/Polygonal...). iStCAD tự canh "cửa sổ nhìn vào Model" bên trong khung đó, bằng cách fit toàn bộ extents hiện tại của Model space vào khung vừa kéo (giữ đúng tỷ lệ khung hình, không bị méo hình) - viewport mới tạo lập tức thấy ngay toàn bộ bản vẽ bên trong, không cần chỉnh thêm.
- **Hiển thị ngay trên màn hình**: tận dụng lại đúng cơ chế vẽ nội dung Model space cắt vào trong khung viewport mà app đã có sẵn (trước đây chỉ dùng cho hộp thoại Plot) - viewport vừa tạo hiện ngay nội dung bản vẽ bên trong khung, không phải khung trống.
- **Lưu file - đúng theo yêu cầu "object tạo ra đúng native AutoCAD"**: đã đọc kỹ mã nguồn thật của lớp `Viewport` trong ACadSharp, và **đã kiểm tra cả 2 bộ ghi (DWG và DXF) đều hỗ trợ ghi Viewport đầy đủ** - khác với TABLE/MULTILEADER trước đây, VIEWPORT **không có rủi ro bị thư viện bỏ qua khi đổi định dạng** - lưu .dwg hay .dxf đều giữ được.
- Khi tạo viewport đầu tiên trên 1 Layout, iStCAD tự thêm trước 1 viewport "đại diện cho giấy" (Id=1, không hiển thị, không ảnh hưởng gì đến bản vẽ) - đúng theo quy ước có sẵn của AutoCAD (viewport Id=1 luôn dành riêng cho "khung giấy", không phải nội dung thật) - để viewport thật của bạn không bị chính AutoCAD hoặc chính iStCAD lúc mở lại file nhận lầm thành khung giấy.
- Move/Scale xử lý VIEWPORT như 1 đối tượng duy nhất (di chuyển/co giãn khung, giữ đúng phần Model đang xem bên trong - thu nhỏ/phóng to khung thì vùng model nhìn thấy cũng thu/phóng theo đúng tỷ lệ, giống kéo giãn khung viewport thật trong AutoCAD).
- Mở lại 1 file đã có sẵn VIEWPORT (do AutoCAD thật tạo, hoặc do chính iStCAD lưu trước đó): đọc lại đúng thành đối tượng VIEWPORT có thể chọn/di chuyển/scale được trong app (không bị hạ cấp thành khung chỉ xem, cũng không bị mất khi lưu lại) - **tốt hơn TABLE/MLEADER trước đây** vì không gặp giới hạn "chưa đọc lại được" của 2 lệnh đó.
- **Giới hạn đã biết (đơn giản hoá có chủ ý)**:
  - Chưa có các tuỳ chọn Fit/2 viewport/3 viewport/4 viewport/Polygonal/ON-OFF riêng từng viewport của MVIEW gốc - chỉ có đúng 1 kiểu: kéo 1 hình chữ nhật, tự fit Model vào trong.
  - Chưa có UI đóng băng (freeze) layer riêng cho từng viewport - đọc được danh sách layer đã đóng băng khi mở file AutoCAD có sẵn, nhưng chưa sửa/thêm được trong iStCAD.
  - **ROTATE và MIRROR chưa áp dụng được cho VIEWPORT** - giống đúng giới hạn đã nói với TABLE ở v1.0.225 (mô hình dữ liệu hiện tại không có trường góc xoay) - nếu chọn VIEWPORT để ROTATE/MIRROR cùng đối tượng khác, VIEWPORT sẽ **giữ nguyên vị trí cũ, không xoay/lật theo**. MOVE/COPY/SCALE không bị ảnh hưởng, vẫn đúng.
  - Khung viewport trên màn hình iStCAD hiện nội dung Model space tại THỜI ĐIỂM đang xem (không tự cập nhật theo kiểu "video trực tiếp" ngay khi đang sửa Model ở viewport khác) - đủ dùng để kiểm tra bố cục, không phải để vừa sửa Model vừa xem đồng thời như AutoCAD Live/nested viewport nâng cao.
- Cập nhật `docs/AUTOCAD_COMMANDS.md`, thêm dòng #102 VIEWPORT.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa/tạo mới (CadEntity.cs, MainWindow.Viewports.cs [file mới], MainWindow.xaml.cs, MainWindow.Wizard.cs, MainWindow.Wizard2.cs, MainWindow.Rebuild.cs, MainWindow.Tools.cs, ACadSharpCadFileService.cs) - khớp 0. Nhân lúc sửa cũng phát hiện và sửa luôn **1 lỗi gõ nhầm tên biến còn sót lại từ bản TABLE (v1.0.225)** trong `ACadSharpCadFileService.cs` (dùng `document` thay vì `native` ở đoạn dọn dẹp TABLE bị xoá) - lỗi này sẽ làm lỗi biên dịch nếu bạn build bản v1.0.225 trước khi có bản này, nên bản v1.0.226 này đã sửa luôn, không cần quay lại bản cũ. **Cần bạn kiểm tra**: chuyển sang 1 tab Layout (hoặc gõ LAYOUT để tạo tab mới) → gõ VIEWPORT → kéo 1 hình chữ nhật → xem có hiện khung + nội dung Model bên trong không; thử MOVE/SCALE khung xem còn đúng không; **lưu .dwg (và .dxf) rồi mở bằng AutoCAD thật xem đúng ra đối tượng VIEWPORT (không phải lỗi/proxy), vị trí/kích thước/view đúng không**; nếu có sẵn file AutoCAD có VIEWPORT, mở thử trong iStCAD xem có chọn/di chuyển được khung đó không.

## v1.0.225 - TABLE: lệnh mới (bảng đơn giản, kéo cả 2 phương, lưu thành đối tượng TABLE thật)
Theo yêu cầu "Làm lệnh table đơn giản. Có thể kéo 2 phương":

- **Cách dùng**: gõ `TABLE` (hoặc `TB`) → nhập số cột → nhập số hàng → bấm góc đầu → bấm góc đối diện - **kéo chuột để xem trước cả lưới, co giãn CẢ chiều rộng VÀ chiều cao cùng lúc** (giống hệt cách kéo RECTANG, đúng ý "kéo 2 phương" bạn yêu cầu, gọn hơn cách AutoCAD gốc phải qua hộp thoại Insert Table trước). Lưới chia đều theo đúng số cột/hàng đã nhập. Các ô lúc đầu đều trống - **double-click vào 1 ô** để mở hộp thoại nhỏ nhập/sửa chữ của riêng ô đó (double-click lại ô khác để sửa tiếp, giống cách double-click sửa TEXT/MTEXT đã có).
- **Lưu file - đúng theo yêu cầu "object tạo ra đúng native AutoCAD"**: đã đọc kỹ mã nguồn thật của lớp `TableEntity` (và `TableContent`/`Row`/`Column`/`Cell`/`CellContent`) trong thư viện ACadSharp - TABLE tạo trong iStCAD giờ lưu đúng thành 1 đối tượng **TABLE thật của AutoCAD** (AcDbTable/TABLECONTENT, có đủ số cột/hàng, kích thước từng cột/hàng, chữ từng ô) - mở bằng AutoCAD thật sẽ thấy đúng là bảng, không phải lưới đường thẳng + chữ rời rạc.
- Move/Copy/Scale (đều) xử lý TABLE như 1 đối tượng duy nhất (giữ đúng lưới, không vỡ hình), EXPLODE vẫn tách được ra thành đường + chữ rời nếu cần (giống Hatch/Leader/Wipeout).
- **CẢNH BÁO QUAN TRỌNG, cần biết trước khi dùng nhiều**: thư viện ACadSharp đọc/ghi file dùng trong app **chỉ hỗ trợ ghi đối tượng TABLE ra file .dwg**. Khi lưu ra **.dxf, thư viện sẽ tự động BỎ QUA đối tượng TABLE** (không báo lỗi, nhưng bảng biến mất khỏi file .dxf đã lưu) - đã xác nhận bằng cách đọc code của cả 2 bộ ghi (DWG và DXF) trong thư viện. **Nếu bản vẽ có TABLE, chỉ nên lưu bằng .dwg, không dùng .dxf.**
- **Giới hạn đã biết (đơn giản hoá có chủ ý)**:
  - Chưa có hộp thoại Insert Table (chọn Table Style, tuỳ chọn tiêu đề/header row...) - mọi ô cùng 1 kiểu chữ/màu theo layer hiện hành.
  - Chưa merge ô, chưa chỉnh riêng độ rộng từng cột hoặc độ cao từng hàng (toàn bộ lưới chia đều theo đúng 1 chiều rộng/1 chiều cao).
  - **ROTATE và MIRROR chưa áp dụng được cho TABLE** - mô hình dữ liệu hiện tại (điểm góc + chiều rộng/cao) không có trường góc xoay, nên nếu chọn TABLE để ROTATE/MIRROR cùng các đối tượng khác, TABLE sẽ **giữ nguyên vị trí cũ, không xoay/lật theo** (các đối tượng khác trong cùng lượt chọn vẫn xoay/lật đúng bình thường) - cố ý để tránh làm sai lệch giữa hình hiển thị và dữ liệu lưu file, để làm đúng sau nếu cần. MOVE/COPY/SCALE không bị ảnh hưởng, vẫn đúng.
  - Mở lại 1 file đã có sẵn đối tượng TABLE (do AutoCAD thật tạo, hoặc do chính iStCAD lưu trước đó) trong iStCAD: đối tượng đó sẽ hiện tạm như "chưa hỗ trợ" (không đọc lại được thành bảng sửa được trong app) - không bị hỏng/mất khi lưu lại, chỉ là chưa sửa được trong app ở phiên đó (giống đúng giới hạn đã nói với MLEADER ở v1.0.224).
- Cập nhật `docs/AUTOCAD_COMMANDS.md`, thêm dòng #101 TABLE.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa/tạo mới (CadEntity.cs, MainWindow.Tables.cs [file mới], MainWindow.xaml.cs, MainWindow.Wizard.cs, MainWindow.Wizard2.cs, MainWindow.Rebuild.cs, MainWindow.Tools.cs, ACadSharpCadFileService.cs) - khớp 0. **Cần bạn kiểm tra**: gõ TABLE → nhập 3 cột, 3 hàng → kéo 1 hình chữ nhật → xem lưới hiện đúng theo kích thước kéo không; double-click vào vài ô, nhập chữ, xem hiện đúng không; chọn cả bảng, thử MOVE/COPY xem còn đúng lưới không; **lưu .dwg rồi mở bằng AutoCAD thật xem đúng ra đối tượng TABLE (không phải lỗi/proxy) không, chữ từng ô đúng vị trí không**; thử lưu .dxf xem app có báo gì không (theo thiết kế, bảng sẽ biến mất khỏi .dxf - cần xác nhận đúng như dự kiến, không bị lỗi khác).

## v1.0.224 - MLEADER: lưu thành đối tượng MULTILEADER thật của AutoCAD (không còn giả bằng LEADER cổ điển)
Theo yêu cầu "Làm chuẩn mleader" - đúng phần đã nói rõ sẽ để sau ở v1.0.223 (lúc đó MLEADER còn dùng chung LEADER cổ điển với QLEADER):

- **Trước đây**: MLEADER dùng chung cơ chế với QLEADER, lưu file ra đúng 1 đối tượng LEADER cổ điển (trước AutoCAD 2008) + 1 MTEXT rời - không phải đối tượng MULTILEADER hiện đại thật.
- **Đã sửa**: MLEADER giờ lưu đúng thành 1 đối tượng **MULTILEADER thật của AutoCAD** (có ContextData/LeaderRoot/LeaderLine đúng cấu trúc thư viện ACadSharp dùng để đọc/ghi DWG và DXF) - mở bằng AutoCAD thật sẽ có đúng kiểu MULTILEADER, đúng grips, không bị cảnh báo "proxy object"/đối tượng lạ. **QLEADER KHÔNG đổi gì** - vẫn lưu đúng như cũ bằng LEADER cổ điển (đúng theo yêu cầu: 2 lệnh phải khác nhau ở phần lưu file, QLEADER = LEADER cổ điển, MLEADER = MULTILEADER mới).
- **Cách nhập/vẽ/sửa trong app giữ NGUYÊN như cũ, không đổi** - đúng theo góp ý trước đây của bạn ("cách vẽ istcad có thể gọn/khác AutoCAD, miễn đối tượng lưu ra đúng native"): vẫn bấm điểm mũi tên → bấm điểm gập (landing) → nhập chữ bằng hộp thoại nhỏ, vẫn là 1 đối tượng duy nhất chọn/di chuyển/copy/xoay/scale/lấy đối xứng được, EXPLODE vẫn tách ra được thành đường + chữ. Khác QLEADER/MLEADER chỉ ở đúng 1 chỗ trong code (loại đối tượng ghi ra file lúc lưu).
- **Còn đơn giản hoá, chưa làm** (giữ an toàn, đúng mức rủi ro chấp nhận được cho lần này):
  - Chỉ 1 leader line/1 leader root (không có đa nhánh/nhiều mũi tên chụm về 1 chữ như MULTILEADER AutoCAD cho phép).
  - Chưa có nội dung dạng Block (chỉ có chữ MTEXT, giống QLEADER) và chưa có Options (Leader landing first/Content first...).
  - Style/LineType/TextStyle của MULTILEADER để ở giá trị mặc định an toàn của thư viện (Standard/ByLayer/Standard) - thư viện tự gắn đúng vào bản vẽ thật khi lưu, không cần tạo riêng.
- **Rủi ro còn mở, CẦN bạn kiểm tra bằng AutoCAD thật (quan trọng nhất)**: vẽ 1 MLEADER mới → lưu .dwg → mở bằng AutoCAD thật xem có đúng hiện ra là MULTILEADER (không phải lỗi/đối tượng trống), có đúng grips/mũi tên/đường gập/vị trí chữ như trên iStCAD không, có báo lỗi gì khi mở không.
- **Giới hạn đã biết, nói rõ để không hiểu nhầm**: mở lại 1 file đã có sẵn đối tượng MULTILEADER (do chính iStCAD lưu ra trước đó, hoặc do AutoCAD thật tạo) trong iStCAD - đối tượng đó sẽ hiện tạm như 1 đối tượng "chưa hỗ trợ" (placeholder), CHƯA đọc lại được thành dữ liệu sửa được trong app. Đây là **lựa chọn chủ ý để làm chắc phần lưu trước** (đúng giá trị, đã thấy 2 thư viện đọc/ghi DWG+DXF đều hỗ trợ đầy đủ MULTILEADER) - phần đọc lại (import) phức tạp hơn, để làm sau nếu cần, vì **đối tượng KHÔNG bị hỏng/mất khi lưu lại** (file giữ nguyên object MULTILEADER gốc, chỉ là chưa sửa được trong iStCAD ở phiên làm việc đó).
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #54 MLEADER.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa (CadEntity.cs, MainWindow.Leaders.cs, MainWindow.Tools.cs, MainWindow.xaml.cs, ACadSharpCadFileService.cs) - khớp 0. Phần lưu file (SyncMultiLeader/CreateNativeMultiLeader) viết dựa trên đọc kỹ mã nguồn thật của lớp MultiLeader/MultiLeaderObjectContextData/LeaderRoot/LeaderLine trong thư viện ACadSharp và xác nhận cả 2 bộ ghi DWG + DXF đều hỗ trợ đầy đủ (không như TABLE - DXF chưa hỗ trợ). **Cần bạn kiểm tra**: gõ MLEADER → bấm điểm mũi tên → bấm điểm gập → nhập chữ → lưu .dwg → mở bằng AutoCAD thật xem đúng ra MULTILEADER không; thử move/copy/erase MLEADER đó trong iStCAD xem vẫn hoạt động như 1 đối tượng như trước; gõ QLEADER so sánh xem vẫn lưu đúng kiểu cũ (LEADER cổ điển), không bị ảnh hưởng gì.

## v1.0.223 - ARRAY Path, REVCLOUD, WIPEOUT
Theo yêu cầu: "Làm path linh động dễ nhập hơn. Thêm lệnh 1,4,5,7" (trong danh sách đối tượng còn thiếu đã gửi: #4 WIPEOUT, #5 REVCLOUD làm được ngay; #1 MLEADER thật và #7 TABLE thật RẤT phức tạp, xem phần "chưa làm" cuối ghi chú này):

- **ARRAY Path** (gõ ARRAY → PA): chọn đối tượng cần mảng → bấm chọn 1 đường dẫn có sẵn (LINE/ARC/CIRCLE/POLYLINE/ELLIPSE/SPLINE) → nhập số lượng item → chọn có tự xoay theo hướng đường dẫn hay không (Y/N). Các bản copy chia đều theo chiều dài cung dọc đường dẫn. Cố ý đơn giản hơn AutoCAD gốc (không hỏi Divide/Measure/Align tangent to path riêng, không có Z direction) theo đúng hướng "dễ nhập hơn" bạn yêu cầu. Tiện thể đã bổ sung cho ARRAY Rectangular/Polar cũ: giờ cũng mảng được HATCH/ELLIPSE/SPLINE (trước đây bị bỏ sót, chỉ mảng được LINE/CIRCLE/ARC/POLYLINE/TEXT/MTEXT/DIMENSION/INSERT).
- **REVCLOUD**: click các điểm góc quanh vùng cần đánh dấu (giống kiểu click cũ của POLYGON region), Enter hoặc C đóng lại - tự động chia viền thành các đoạn cung lồi đều ra ngoài (cỡ cung tự tính theo kích thước vùng, khoảng 24 cung quanh vòng). Dùng đúng Bulge trên LWPOLYLINE thật (như PLINE Arc đã làm ở v1.0.221) nên mở AutoCAD thật vẫn đúng là polyline có cung, không phải nhiều đoạn thẳng xấp xỉ. Chưa làm Arc length/Object/Rectangular/Polygonal/Freehand/Style/Modify của AutoCAD gốc.
- **WIPEOUT**: click các điểm góc quanh vùng cần che, Enter/C đóng lại - lưu đúng thành 1 đối tượng **WIPEOUT thật của AutoCAD** (InsertPoint + U/V vector + Size + ClipBoundaryVertices, giữ U/V ở đúng 1 đơn vị vẽ = 1 "pixel" cho đơn giản). **Lưu ý quan trọng chưa kiểm được**: màn hình iStCAD hiện tại MỚI CHỈ vẽ đường viền (outline) của WIPEOUT, CHƯA thực sự che các đối tượng khác ngay trong iStCAD - nhưng mở file đã lưu bằng AutoCAD thật thì WIPEOUT sẽ che đúng (đây là đối tượng thật, không phải giả). Cũng chưa gắn kèm 1 "image definition" giả như 1 số file AutoCAD thật có - cần bạn kiểm tra kỹ xem AutoCAD có báo lỗi/thiếu ảnh gì không khi mở.
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #31 ARRAY, #15 REVCLOUD, #16 WIPEOUT.
- **Chưa làm (để sau, vì rất lớn và rủi ro nếu làm vội)**:
  - **MLEADER thật** (đối tượng MULTILEADER của AutoCAD, khác LEADER cổ điển đang dùng tạm) - thư viện đọc/ghi file CÓ hỗ trợ sẵn (đọc/ghi cả DWG và DXF), nhưng cấu trúc dữ liệu rất phức tạp (leader root/leader line, nội dung chữ hoặc block, nhiều cờ override, tham chiếu đến MLeaderStyle...) - cần làm riêng, cẩn thận, không vội trong lần này để tránh làm sai gây hỏng cả phần lưu file.
  - **TABLE thật** (bảng biểu) - thư viện CÓ class TableEntity và hỗ trợ ghi DWG, nhưng **KHÔNG hỗ trợ ghi ra DXF** (thư viện tự báo "chưa triển khai" khi ghi DXF) - nên nếu làm, bảng sẽ mất khi lưu dưới dạng .dxf. Cấu trúc ô/cột/dòng cũng rất phức tạp. Để làm sau, khi có nhiều thời gian hơn để làm kỹ.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa (CadEntity.cs, MainWindow.Rebuild.cs, MainWindow.Wizard.cs, MainWindow.ModifyCommands.cs, MainWindow.Tools.cs, MainWindow.xaml.cs, ACadSharpCadFileService.cs) - khớp 0. **Cần bạn kiểm tra**: (1) ARRAY Path - chọn vài đối tượng, gõ ARRAY → PA → bấm 1 đường tròn hoặc polyline làm đường dẫn → nhập số lượng, xem các bản copy có chia đều quanh đường dẫn không; (2) REVCLOUD - click vài điểm quanh 1 vùng, Enter, xem có ra hình mây cuộn đúng không; (3) WIPEOUT - click vài điểm, Enter, lưu .dwg rồi mở bằng AutoCAD thật xem có che đúng phần bên dưới không, có báo lỗi/thiếu ảnh gì không.

## v1.0.222 - SPLINE: lưu thành đối tượng SPLINE thật (không còn giả bằng polyline Catmull-Rom)
Theo yêu cầu "tạo tiếp các đối tượng còn thiếu so với native AutoCAD theo thứ tự ưu tiên" - tiếp tục đúng hướng đã làm cho ELLIPSE (v1.0.220), rà lại danh sách và thấy SPLINE bị đúng loại lỗi này, mức độ quan trọng cao (SPLINE là lệnh hay dùng):
- **Trước đây**: SPLINE vẽ ra một POLYLINE nhiều đoạn thẳng xấp xỉ đường cong (thuật toán Catmull-Rom tự viết) - nhìn mượt trên màn hình nhưng lưu file ra là polyline thật, KHÔNG phải đối tượng SPLINE của AutoCAD. Mở bằng AutoCAD sẽ thấy 1 polyline nhiều đỉnh, không có grips fit-point/control-point, không sửa được như spline thật.
- **Đã sửa**: SPLINE giờ lưu đúng thành 1 đối tượng **SPLINE thật của AutoCAD** - đường cong NURBS bậc 3 (control points + knot vector + weights). Điểm đáng tin: phần tính control points/knots/weights từ các điểm bấm (fit points) dùng ĐÚNG hàm `UpdateFromFitPoints()` có sẵn trong chính thư viện ACadSharp (thư viện đọc/ghi file dùng trong app) - không phải code tự viết lại công thức NURBS - nên kết quả khớp đúng kiểu AutoCAD tính, giảm rủi ro sai công thức. Cách nhập (bấm từng điểm, Enter kết thúc) giữ nguyên như cũ, không đổi.
  - Mọi lệnh MOVE/COPY/ROTATE/SCALE(đều)/MIRROR xử lý spline như 1 đối tượng duy nhất (không phải hàng chục đoạn thẳng), EXPLODE vẫn tách được ra thành polyline nếu cần.
  - Mở 1 file .dwg có sẵn SPLINE (do AutoCAD hoặc phần mềm khác tạo): giờ cũng được đọc đúng thành spline thật (trước đây sẽ hiện được hình nhưng không giữ lại dữ liệu spline gốc khi lưu lại).
- **Chưa làm**: các tùy chọn Method/Knots/Object/start Tangency/toLerance/Close lúc vẽ (chỉ làm kiểu "bấm từng fit point" mặc định, đơn giản, giống SPLINE cũ) - model đã lưu đủ control points/knots/weights nên nếu cần Close sau này sẽ nhanh.
- **Đã rà thêm và phát hiện DONUT có cùng loại vấn đề** (lưu ra 2 CIRCLE rời thay vì 1 POLYLINE có Width) nhưng CHƯA sửa lần này vì cần thêm trường Width vào model polyline trước (sẽ làm cùng lúc với PLINE/RECTANG Width ở lần sau) - đã ghi chú rõ trong docs/AUTOCAD_COMMANDS.md để không quên.
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #8 SPLINE và #12 DONUT (ghi chú lỗi phát hiện, chưa sửa).
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa (CadEntity.cs, MainWindow.Rebuild.cs, MainWindow.Wizard.cs, MainWindow.Wizard2.cs, MainWindow.ModifyCommands.cs, MainWindow.Tools.cs, MainWindow.xaml.cs, ACadSharpCadFileService.cs) - khớp 0. **Cần bạn kiểm tra**: gõ SPLINE → bấm 3-4 điểm không thẳng hàng → Enter kết thúc, xem đường cong có mượt qua đúng các điểm đã bấm không; lưu .dwg rồi mở bằng AutoCAD thật xem có đúng là 1 đối tượng SPLINE (có grips fit-point, không phải polyline nhiều đỉnh) không; thử mở 1 file .dwg có sẵn spline (vẽ từ AutoCAD) trong iStCAD rồi lưu lại, mở lại bằng AutoCAD xem còn đúng spline không.

## v1.0.221 - PLINE: thêm tùy chọn Arc (vẽ đoạn cung, bulge) khi đang vẽ polyline
Theo yêu cầu: "Làm arc và bulge trong polyline" - PLINE giờ chuyển được giữa đoạn thẳng và đoạn cung ngay trong lúc vẽ, giống tùy chọn Arc mặc định của AutoCAD (kiểu tiếp tuyến, chưa làm các tùy chọn con Angle/CEnter/Direction/Radius/Second pt):
- Đang vẽ PLINE, gõ **A** ở ô nhập để chuyển sang vẽ đoạn cung: từ điểm vừa bấm, cung sẽ tiếp tuyến (cùng hướng) với đoạn trước đó, rồi bấm điểm tiếp theo để kết thúc đoạn cung đó. Gõ **L** để chuyển lại về vẽ đoạn thẳng. Có thể đổi qua lại nhiều lần trong 1 lần vẽ PLINE.
- Xem trước (preview) khi rê chuột trong lúc ở chế độ Arc cũng vẽ đúng hình cung (không còn vẽ thẳng tới chuột) để dễ ngắm trước khi bấm điểm.
- **Phần quan trọng nhất (đúng theo góp ý "object tạo ra đúng native AutoCAD")**: đoạn cung được lưu bằng đúng cơ chế **Bulge** trên đỉnh polyline - cùng cơ chế LWPOLYLINE thật của AutoCAD dùng để lưu cung tròn trong polyline (đã dùng từ trước cho bo tròn góc của RECTANG). Đường ghi/đọc file native polyline (`CreatePolyline`, cập nhật polyline có sẵn, và đọc polyline từ file có sẵn) đã dùng đúng Bulge từ trước nên không cần sửa thêm - mở file đã lưu bằng AutoCAD thật sẽ thấy đúng là polyline có đoạn cung (LWPOLYLINE với bulge), không phải chuỗi đoạn thẳng xấp xỉ.
- Toán học: góc tiếp tuyến-dây cung ký hiệu φ (từ hướng đang đi tới điểm mới) → bulge = tan(φ/2); hướng đi mới sau đoạn cung = hướng cũ xoay thêm góc 2φ (để nối tiếp đoạn cung sau nếu bấm thêm điểm khi vẫn ở chế độ Arc).
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #2 PLINE.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên MainWindow.xaml.cs (file duy nhất sửa lần này) - khớp 0. **Cần bạn kiểm tra**: gõ PLINE → bấm 2-3 điểm → gõ A → bấm thêm 1-2 điểm để ra đoạn cung (xem cung có tiếp tuyến mượt với đoạn thẳng trước, không bị gãy khúc) → gõ L rồi bấm thêm điểm thẳng → Enter kết thúc; sau đó lưu .dwg và mở lại bằng AutoCAD thật xem đoạn cung còn đúng là cung tròn (không bị tách thành nhiều đoạn thẳng) không.

## v1.0.220 - ELLIPSE: lưu thành đối tượng ELLIPSE thật (không còn giả bằng polyline)
Theo đúng góp ý của bạn: "Cách vẽ istcad có thể khác gọn và thông minh hơn [AutoCAD] - Object tạo ra đúng với native autocad là ok". Áp dụng ngay cho ELLIPSE - lệnh đang ở mức "Có, chưa rà" và hoá ra có đúng kiểu vấn đề này:
- **Trước đây**: ELLIPSE vẽ ra một POLYLINE 120 đoạn thẳng xấp xỉ hình ellipse - nhìn gần đúng trên màn hình nhưng lưu file ra là polyline thật, KHÔNG phải đối tượng ELLIPSE của AutoCAD. Mở bằng AutoCAD sẽ thấy 1 polyline nhiều đỉnh, không có grips ellipse, không sửa bán trục/tỷ lệ được như ellipse thật.
- **Đã sửa**: ELLIPSE giờ lưu đúng thành 1 đối tượng **ELLIPSE thật của AutoCAD** (tâm, điểm cuối bán trục lớn, tỷ lệ bán trục nhỏ/lớn) - mở bằng AutoCAD thật sẽ có đúng grips ellipse, sửa được như ellipse gốc. Cách NHẬP (tâm → bán trục 1 → bán trục 2) vẫn giữ nguyên như trước - gọn hơn cách AutoCAD gốc (axis endpoint → endpoint → distance) - đây là **chủ ý theo đúng góp ý của bạn**: cách vẽ trong iStCAD không cần giống từng bước của AutoCAD, miễn đối tượng tạo ra là native AutoCAD thật.
  - Mọi lệnh MOVE/COPY/ROTATE/SCALE(đều)/MIRROR xử lý ellipse như 1 đối tượng duy nhất (không phải hàng trăm đoạn thẳng), EXPLODE vẫn tách được ra thành polyline nếu cần.
  - Mở 1 file .dwg có sẵn ELLIPSE (do AutoCAD hoặc phần mềm khác tạo): giờ cũng được đọc đúng thành ellipse thật (trước đây sẽ hiện được hình nhưng không giữ lại dữ liệu ellipse gốc khi lưu lại).
- **Chưa làm**: tuỳ chọn Arc (cung ellipse, chỉ vẽ 1 phần hình ellipse) và Rotation (xoay nhìn nghiêng kiểu phối cảnh) lúc vẽ - model đã có sẵn trường StartParameter/EndParameter cho cung ellipse nên làm thêm sau sẽ nhanh nếu cần.
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #7 ELLIPSE.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa (CadEntity.cs, MainWindow.Rebuild.cs, MainWindow.xaml.cs, MainWindow.Wizard.cs, MainWindow.ModifyCommands.cs, MainWindow.Tools.cs, MainWindow.MoreCommands.cs, ACadSharpCadFileService.cs) - khớp 0. **Cần bạn kiểm tra**: vẽ ELLIPSE → lưu .dwg → mở bằng AutoCAD thật, xem có đúng là 1 đối tượng ELLIPSE (có grips ellipse, không phải polyline) không; thử mở 1 file .dwg có sẵn ellipse (vẽ từ AutoCAD) trong iStCAD rồi lưu lại, mở lại bằng AutoCAD xem còn đúng ellipse không.

## v1.0.219 - SỬA LỖI quan trọng: POLYGON/POL trước đây KHÔNG phải lệnh đa giác đều thật
Trong lúc tiếp tục rà các lệnh "Có, chưa rà" (POLYGON, ELLIPSE, DONUT...), phát hiện 1 lỗi khá nghiêm trọng so với yêu cầu "lệnh phải giống AutoCAD":
- **Lỗi phát hiện**: gõ `POLYGON` hoặc `POL` (hoặc lệnh tiếng Việt "dagiac") từ trước đến nay **KHÔNG mở lệnh đa giác đều của AutoCAD** (số cạnh → tâm → bán kính) như bảng `docs/AUTOCAD_COMMANDS.md` vẫn ghi - mà lại mở một công cụ KHÁC, tự chế: vẽ 1 vùng kín bằng cách bấm từng điểm góc tùy ý (không theo số cạnh/bán kính). Lệnh đa giác đều THẬT lại bị để dưới tên khác `RPOLYGON`/`RPOL`, ít ai nghĩ để gõ tên đó.
- **Đã sửa**: `POLYGON`/`POL` giờ gọi đúng lệnh đa giác đều thật (giống `RPOLYGON`/`RPOL` cũ, vẫn giữ nguyên 2 tên đó để không hỏng gì đang dùng): nhập số cạnh → bấm tâm → bấm bán kính (hoặc gõ số, thêm chữ C ở cuối để lấy theo Circumscribed thay vì Inscribed mặc định, ví dụ gõ "40C").
- Công cụ "vùng kín tự do" cũ không mất đi tính năng gì: dùng **PLINE** rồi gõ **C** để đóng kín là ra đúng cùng kết quả (PLINE đã hỗ trợ Close từ trước).
- Đây là lỗi **có khả năng đã ảnh hưởng mọi lần bạn hoặc ai dùng thử gõ POLYGON từ trước đến nay** - xin lỗi vì để sót, và cảm ơn việc rà lại các lệnh "Có, chưa rà" đã phát hiện ra. Nếu bạn đã quen dùng RPOLYGON để vẽ đa giác đều, lệnh đó vẫn hoạt động y như cũ, không ảnh hưởng.
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #6 POLYGON, ghi rõ lỗi cũ + cách sửa.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Chỉ sửa 1 dòng trong MainWindow.AutoCadMore.cs (đổi routing của POLYGON/POL) - đã kiểm số dấu `{ } ( ) [ ]`, khớp 0. **Cần bạn kiểm tra ngay**: gõ POLYGON → nhập số cạnh (ví dụ 6) → bấm tâm → bấm hoặc gõ bán kính, xem có ra đúng hình lục giác đều không.

## v1.0.218 - RECTANG: thêm Chamfer/Fillet (bắt đầu rà các lệnh "Có, chưa rà")
Tiếp tục theo đúng thứ tự đã đặt ra: rà lại các lệnh "Có, chưa rà" trước khi làm lệnh mới khác.
- **RECTANG (REC)** - đã có "first corner → opposite corner" nhưng thiếu các tùy chọn của AutoCAD gốc. Đã thêm:
  - **Chamfer (C)**: gõ C tại bước "Specify first corner" → nhập khoảng cách vát 1 → khoảng cách vát 2 → vẽ như cũ. 4 góc chữ nhật được vát chéo theo 2 khoảng cách này (cạnh ngang dùng khoảng cách 1, cạnh đứng dùng khoảng cách 2 - đúng quy ước AutoCAD cho chữ nhật không xoay).
  - **Fillet (F)**: gõ F → nhập bán kính bo tròn → vẽ như cũ, 4 góc được bo tròn bằng cung đúng bán kính đó.
  - Giống AutoCAD: **giá trị Chamfer/Fillet được NHỚ LẠI cho lần vẽ RECTANG tiếp theo** trong cùng phiên làm việc (không cần nhập lại nếu muốn vẽ nhiều hình chữ nhật vát/bo giống nhau liên tiếp); đặt Chamfer sẽ tự tắt Fillet và ngược lại (2 tùy chọn loại trừ nhau, đúng như AutoCAD).
  - Nếu khoảng cách vát/bo lớn hơn nửa cạnh, tự giảm xuống còn đúng nửa cạnh đó để hình không bị méo/tự cắt nhau, thay vì báo lỗi.
  - **Chưa làm** (giữ đơn giản, ít dùng với bản vẽ 2D thông thường): Elevation/Thickness (thuộc tính 3D - app chỉ vẽ 2D), Width (polyline có độ rộng riêng từng cạnh - model chưa hỗ trợ, đã ghi nhận từ các bản trước), và Area/Dimensions/Rotation (cách nhập kích thước khác thay vì bấm điểm góc thứ 2 - ít dùng, có thể bổ sung sau nếu cần).
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #5 RECTANG: từ "Có, chưa rà" sang "Một phần" kèm ghi chú rõ phần đã làm/chưa làm.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Đã kiểm số dấu `{ } ( ) [ ]` trên MainWindow.xaml.cs (file duy nhất sửa lần này) - khớp 0. **Cần bạn kiểm tra**: gõ RECTANG → gõ C → nhập 2 khoảng cách → vẽ, xem 4 góc có vát đúng không; tương tự với F (bo tròn); vẽ thêm 1 hình chữ nhật nữa ngay sau đó (không gõ lại C/F) xem có tự áp dụng lại đúng giá trị cũ không.

## v1.0.217 - ATTDEF/BLOCK + INSERT block nội bộ (theo yêu cầu "hoàn thiện attribute block và liên quan")
- **ATTDEF (ATT)** - lệnh mới: hộp thoại nhập Tag/Prompt/Default value/Invisible/Justify/Text height/Rotation (dùng đúng danh sách Justify giống Enhanced Attribute Editor đã có) → bấm 1 điểm để đặt. Hiển thị chữ TAG trên bản vẽ (giống AutoCAD với attribute definition chưa thuộc block nào). Di chuyển/copy/xoay/scale/lấy đối xứng như 1 đối tượng độc lập cho tới khi được đưa vào BLOCK.
- **BLOCK (B)** - lệnh mới: hộp thoại nhập Tên block → bấm 1 điểm làm base point → chọn các đối tượng muốn đưa vào block (LINE/CIRCLE/ARC/POLYLINE/TEXT/MTEXT/ATTDEF, giống đúng nhóm đối tượng INSERT file ngoài đang hỗ trợ, cộng thêm ATTDEF) → Enter. Các đối tượng đã chọn bị thay bằng 1 block tham chiếu (INSERT) duy nhất tại base point; nếu trong số đối tượng chọn có ATTDEF, mỗi ATTDEF trở thành 1 thuộc tính (ATTRIB) của block đó, giá trị ban đầu = Default value đã khai báo - sửa giá trị sau này dùng đúng hộp thoại Enhanced Attribute Editor (double-click vào block) đã có sẵn từ trước, không cần sửa gì thêm.
  - Định nghĩa block được lưu trong bộ nhớ của bản vẽ đang mở (chưa phải WBLOCK ra file .dwg riêng - xem mục "chưa làm" dưới).
  - Nếu đặt tên trùng với block đã có, iStCAD sẽ ĐỊNH NGHĨA LẠI (ghi đè) block đó, giống AutoCAD.
- **INSERT (I)** - mở rộng: trước khi mở hộp thoại chọn file ngoài như cũ, nếu bản vẽ đang mở đã có block tự định nghĩa (bằng BLOCK), sẽ hiện hộp thoại chọn: gõ/chọn tên 1 block có sẵn trong bản vẽ, hoặc bấm "Browse..." để chèn file ngoài như trước (không đổi). Nếu bản vẽ chưa có block nào, INSERT vẫn chạy đúng như cũ (chỉ hỏi file).
  - Nếu block được chọn có thuộc tính (ATTDEF), sẽ hiện thêm 1 hộp thoại đơn giản hỏi giá trị từng thuộc tính (mặc định = Default value) TRƯỚC khi chọn điểm chèn - khác với Enhanced Attribute Editor (hộp thoại đó dùng để SỬA LẠI sau khi đã chèn, đầy đủ hơn với cả Text Options/Properties).
- **Lưu file (để giống AutoCAD khi mở lại thật - đúng yêu cầu trước đó "quan trọng save/load được trong AutoCAD")**: đã đọc lại các lớp `BlockRecord`, `Block`, `AttributeDefinition` thật của thư viện ACadSharp. Khi lưu, với mỗi block tự định nghĩa (qua BLOCK) mà INSERT có tham chiếu tới, nếu file chưa có sẵn BlockRecord tên đó, iStCAD sẽ tạo mới 1 BlockRecord thật (kèm các đối tượng hình học + AttributeDefinition bên trong) rồi mới ghi các INSERT - mở bằng AutoCAD thật sẽ thấy đúng block, đúng thuộc tính, sửa được giá trị thuộc tính bình thường.
  - Tiện sửa luôn 1 lỗi tiềm ẩn phát hiện khi làm phần này: trước đây, 1 block-có-thuộc-tính MỚI TẠO, lần lưu ĐẦU TIÊN sẽ ghi theo giá trị MẶC ĐỊNH của block (không phải giá trị người dùng đã nhập) - do thư viện tự sinh ATTRIB mặc định khi tạo INSERT, còn bước đồng bộ giá trị thật chỉ chạy ở các lần lưu SAU. Đã sửa: lưu lần đầu cũng đồng bộ giá trị thật ngay.
- **Chủ ý CHƯA làm trong lần này** (để sau, không phải thiếu sót quên):
  - **WBLOCK**: ghi 1 block ra file .dwg riêng (xuất độc lập khỏi bản vẽ hiện tại) - block hiện chỉ dùng được trong chính bản vẽ đã tạo ra nó.
  - **BATTMAN**: hộp thoại riêng để sửa ĐỊNH NGHĨA thuộc tính của block (đổi Tag/Prompt/Default/thứ tự... áp dụng cho MỌI lần chèn block đó về sau) - khác với việc sửa GIÁ TRỊ thuộc tính trên 1 lần chèn cụ thể (đã có sẵn qua Enhanced Attribute Editor).
  - **BEDIT** (block editor đầy đủ, sửa trực tiếp hình trong block), dynamic block (tham số co giãn/xoay riêng), và tùy chọn ATTDIA (tắt/mở hỏi thuộc tính khi chèn) - đều là tính năng lớn riêng, để làm sau nếu cần.
- Cập nhật `docs/AUTOCAD_COMMANDS.md`: #74 BLOCK, #75 INSERT, #77 ATTDEF chuyển sang "Khớp" kèm ghi chú giới hạn; #76 WBLOCK vẫn "Chưa có"; thêm dòng BATTMAN "Chưa có".
- **Vẫn chưa biên dịch/chạy thử lần nào** (môi trường này không có trình dịch .NET - mọi thay đổi viết dựa trên đọc kỹ mã nguồn thật của ACadSharp và kiểm tra thủ công). Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa/tạo mới (CadEntity.cs, CadDrawing.cs, MainWindow.Rebuild.cs, MainWindow.xaml.cs, MainWindow.Wizard.cs, MainWindow.ModifyCommands.cs, MainWindow.Tools.cs, MainWindow.Blocks.cs (file mới), ACadSharpCadFileService.cs) - đều khớp 0. **Điều quan trọng nhất cần bạn kiểm tra**: tạo ATTDEF → BLOCK (gồm cả ATTDEF) → lưu file .dwg → mở bằng AutoCAD thật, xem block có đúng hình, đúng thuộc tính không, rồi chèn lại (INSERT) block đó vào đúng bản vẽ đang mở trong iStCAD xem có hỏi giá trị thuộc tính và chèn đúng vị trí không.

## v1.0.216 - LEADER/QLEADER/MLEADER (theo yêu cầu ưu tiên, cần giống AutoCAD)
- **QLEADER (LE)** và **MLEADER (MLD)** - lệnh mới, dùng CHUNG một cách vẽ (cùng hình dạng mũi tên + đường gập + chữ): bấm điểm đặt mũi tên → bấm điểm gập (landing) → hộp thoại nhỏ hiện ra để nhập chữ chú thích. Cả mũi tên/đường dẫn/đường gập/chữ là MỘT đối tượng duy nhất (chọn, di chuyển, copy, xoay, scale, lấy đối xứng như 1 đối tượng, giống cách HATCH/DIMENSION đang làm) - EXPLODE vẫn dùng được nếu cần tách ra thành đường + chữ riêng.
- **Để giống AutoCAD khi lưu file** (đúng yêu cầu lần này): đã đọc kỹ cách thư viện ACadSharp ghi đối tượng LEADER (cổ điển, trước AutoCAD 2008) ra file DWG/DXF thật, rồi viết thêm phần lưu - leader tạo trong iStCAD sẽ ghi thành đúng 1 đối tượng LEADER gốc (mũi tên, các đoạn thẳng, đường gập) kèm 1 MTEXT chữ chú thích đặt đúng vị trí - mở bằng AutoCAD thật sẽ thấy đúng hình, không bị mất khi lưu+mở lại (học từ bài học HATCH trước: không để lệnh mới nào "biến mất khi lưu" nữa).
- **2 giới hạn đã biết, nói rõ để không hiểu nhầm:**
  - Phần chữ được lưu là 1 MTEXT đặt cạnh đường gập, KHÔNG gắn liên kết chính thức với LEADER ở cấp độ file (do thư viện ACadSharp chỉ cho phép việc gắn này từ chính bộ đọc file của nó, ứng dụng không gọi được) - AutoCAD vẫn hiển thị đúng mũi tên + đường + chữ, chỉ là về mặt dữ liệu file chúng là 2 đối tượng riêng, không phải 1 "nhóm" liên kết như AutoCAD tự tạo.
  - MLEADER trong iStCAD hiện vẽ giống QLEADER (1 đoạn gấp khúc cố định, chưa có nhiều đoạn, chưa có Settings/Options, chưa chọn Content-first) và lưu file dưới dạng LEADER cổ điển, chưa phải đối tượng MLEADER hiện đại của AutoCAD. Đây là lựa chọn chủ ý để làm chắc, đơn giản trước - nếu cần đúng MLEADER hiện đại (nhiều đoạn, nhiều kiểu landing...) báo lại để làm tiếp.
- Cập nhật `docs/AUTOCAD_COMMANDS.md` dòng #53 QLEADER, #54 MLEADER: từ "Chưa có" sang "Một phần" kèm ghi chú rõ các giới hạn trên.
- **Vẫn chưa biên dịch/chạy thử lần nào.** Phần lưu file (SyncLeader/CreateLeaderNatives trong ACadSharpCadFileService.cs) được viết dựa trên đọc kỹ mã nguồn thật của bộ ghi DWG và DXF cho đối tượng LEADER (giống cách đã làm kỹ cho HATCH ở bản trước), và dùng lại đúng cơ chế đã có sẵn cho DIMENSION (SyncDimension) để đảm bảo leader không bị xóa nhầm hay ghi trùng qua nhiều lần lưu. Đã kiểm số dấu `{ } ( ) [ ]` trên toàn bộ file đã sửa (CadEntity.cs, MainWindow.Rebuild.cs, MainWindow.xaml.cs, MainWindow.Wizard.cs, MainWindow.ModifyCommands.cs, MainWindow.Leaders.cs (file mới), MainWindow.Tools.cs, MainWindow.Dimensions.cs, ACadSharpCadFileService.cs) - đều khớp. Việc kiểm tra chắc chắn nhất vẫn là bạn build rồi thử: tạo LEADER → di chuyển/copy → lưu .dwg → mở bằng AutoCAD thật.
