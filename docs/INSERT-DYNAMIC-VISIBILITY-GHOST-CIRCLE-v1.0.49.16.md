# iStCAD v1.0.49.16 — suppress unresolved Dynamic INSERT previews

## Feedback from v1.0.49.15

The follow-up screenshot still shows multiple large circular/arc paths around the same INSERT after the v1.0.49.15 change. The previous change prevented traversal of a dynamic source block when its active visibility state was unresolved, but then selected the INSERT's proxy graphics as the fallback. The screenshot demonstrates that this fallback is not safe for this drawing: the proxy stream may itself contain paths for more than one visibility state, or otherwise contain stale/overlaid preview geometry.

## Change

- Treat a Dynamic Block as unresolved when a `BlockVisibilityParameter` with one or more named states exists on either the referenced block record or its anonymous block's `Source` record. Do not assume every anonymous `*U` record is a fully resolved visibility snapshot.
- For an INSERT tree with unresolved visibility, suppress **both** native child tessellation and the INSERT's proxy paths/text. Do not allow proxy fallback to reintroduce the unwanted circles.
- Keep only the INSERT's existing bounds fallback for viewport representation. The underlying native INSERT, block definitions, attributes, and proxy data remain in the document; nothing is deleted or rewritten.
- Count affected top-level INSERTs by block name and add a read diagnostic stating that a bounds-only preview was selected because the active Visibility State is unresolved.
- Preserve the existing native/proxy single-source rule for INSERTs whose visibility state is not unresolved.

## Trade-off

This is intentionally conservative. A Dynamic Block with unresolved visibility may appear as a dashed bounds rectangle instead of its full visible geometry. This avoids rendering geometry from inactive states. Full fidelity requires resolving the active visibility state per INSERT using the DWG evaluation data; this change does not claim to implement the full AutoCAD Dynamic Block evaluator.

## Validation status

Static source checks and ZIP integrity checks only. The same Windows/WPF application must be built and opened with the affected DWG to confirm that the rings disappear and to verify whether bounds-only fallback is acceptable. .NET SDK/WPF runtime is not available in the current environment, so no measured render result or executable build is claimed.
