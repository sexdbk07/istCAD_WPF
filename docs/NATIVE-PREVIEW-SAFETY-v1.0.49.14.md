# iStCAD v1.0.49.14 — Native DWG preview and viewport safety

## Viewport scheduling

- Mouse/command preview work is coalesced through a render-priority 16 ms dispatcher timer.
- Wheel transform updates are coalesced through a 16 ms timer; the final camera is committed after the 140 ms wheel debounce.
- The grid is hidden during wheel/pan motion and rebuilt once at the interaction boundary. Resetting the transient wheel transform no longer unconditionally invalidates and redraws a grid that the commit already restored.
- A canceled geometry worker records pending viewport requests and schedules the latest key after completion. It does not leave the viewport waiting on a canceled worker when zoom-out/zoom-in returns to an earlier camera key.

## Query and geometry cache

- `CadSpatialIndex.QueryAdaptive` performs a single R-tree traversal. It preserves entity order for ordinary views and skips the result sort for far-LOD queries over the configured candidate threshold.
- `CadGeometryCache` remains bounded by entry count and estimated geometry size. Exact content signatures share frozen WPF geometry for duplicate lines, circles, arcs, small polylines, and bounded proxy/block previews.
- Proxy cache matching is capped at 8,192 path points and 64 text records; larger proxy previews stay identity-cached to avoid making signature generation itself a large CPU task. Hash matches are followed by exact geometry comparison.

## DWG INSERT/proxy preview

- Nested block definitions are walked directly with the `Insert` transform. The preview path does not call `Insert.Explode()` and does not clone all block children.
- A per-drawing work budget limits entities, points, paths, texts and proxy items, in addition to per-INSERT and per-proxy limits. When the limit is reached, only preview data is clipped; native DWG objects remain preserved in the document context for editing and saving.
- Recursion is tracked using active `BlockRecord` references. Circular references and over-depth blocks stop at the offending branch.
- Child entity visibility, layer Off and Frozen state are checked before preview extraction. Layer-0 entities inherit the nearest owning INSERT layer. The native-preview classification cache is keyed by block and inherited layer.
- INSERT native preview and proxy graphics are alternative display sources. A block is rendered from its native children only when all visible children can be represented; otherwise proxy graphics are used as a whole. This prevents overlaying native circles and proxy circles for the same INSERT.
- Degenerate and full-turn ARC entities are skipped as arcs; full circles must be represented by CIRCLE/closed circle geometry rather than synthesized from a zero-sweep ARC.

## Safety limits

| Scope | Limit |
|---|---:|
| Nested INSERT depth | 12 |
| Entities per INSERT traversal | 50,000 |
| Points per INSERT preview | 24,000 |
| Paths per INSERT preview | 8,000 |
| Text items per INSERT preview | 512 |
| Points per proxy entity | 8,192 |
| Paths per proxy entity | 2,048 |
| Drawing-wide preview entities | 1,500,000 |
| Drawing-wide preview points | 2,000,000 |
| Drawing-wide preview paths | 200,000 |
| Drawing-wide preview texts | 100,000 |
| Drawing-wide proxy items | 500,000 |

The limits are deliberately on generated preview work, not the CAD database. They prevent unusually large blocks/proxies from allocating unbounded transient preview geometry. Diagnostic output notes when the drawing-wide budget is reached.

## Validation status

Source-level review and static packaging checks only. The current environment does not provide the .NET SDK or Windows WPF runtime, so this archive is **not a verified executable build**. Actual validation still requires opening the supplied DWGs in the Windows build, comparing the offending INSERTs with AutoCAD, and recording frame-time/FPS before and after.

Dynamic Block evaluation graphs and every `BLOCKVISIBILITYPARAMETER` state are not fully evaluated by this change. Do not interpret the absence of proxy/native duplication as proof that all AutoCAD dynamic visibility states are implemented.
