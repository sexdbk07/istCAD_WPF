# iStCAD v1.0.49.15 — Dynamic INSERT visibility-state preview guard

## Symptom addressed

The reported preview shows several large, near-concentric circles/arcs around a single INSERT while AutoCAD does not show them. This pattern is consistent with walking a dynamic-block source definition as ordinary geometry when its `BLOCKVISIBILITYPARAMETER` contains multiple named states. Such a source can store entities belonging to different visibility states in the same block definition.

## Source finding

In `ACadSharpCadFileService`, the INSERT walker traversed every visible entity in `Insert.Block` and its nested blocks. The existing visibility test checked `IsInvisible`, Layer Off, and Frozen, but did not inspect `BlockRecord.EvaluationGraph` for `BlockVisibilityParameter` state groups. Layer filtering alone cannot choose the active dynamic visibility state.

## Change

- Detect unresolved `BlockVisibilityParameter` state groups through `BlockRecord.EvaluationGraph.Nodes`.
- Recursively inspect nested INSERT and DIMENSION block references, with cycle/depth protection and layer inheritance.
- Do not tessellate native block children for an INSERT tree containing an unresolved dynamic visibility state.
- Prefer the INSERT's own proxy graphics as the display source in that case; this lets the file's evaluated proxy representation supply the visible state rather than drawing every entity from the source definition.
- If no suitable proxy graphics are available, keep the safe extents fallback rather than fabricating all-state circles.
- Preserve native traversal for anonymous `*U` blocks linked to a source block because those are the per-reference evaluated representations, not the unresolved source definition.
- Avoid performing the native INSERT preview traversal at all when the tree is known to have unresolved visibility states, reducing unnecessary tessellation and allocations.

## Important limitation

This is a conservative display guard, not a full AutoCAD Dynamic Block evaluator. It does not evaluate stretch, lookup, flip, array, or all dynamic parameter/action expressions. The guard may display proxy graphics or a bounds fallback for a dynamic block whose active state cannot be resolved. It intentionally prefers avoiding false geometry over showing every state at once.

## Validation status

Source inspection and static packaging checks only. No .NET SDK or Windows WPF runtime is available in this environment, so this source archive is not a verified executable build. Validate by opening the same DWG, comparing the same INSERT with AutoCAD, and checking whether the unwanted rings disappear without losing the visible block geometry.
