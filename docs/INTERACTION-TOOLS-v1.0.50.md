# iStCAD v1.0.50 — drafting precision and command input

## Changes
- Extended the drafting cursor crosshair across the full viewport while retaining the pickbox gap.
- Matched the editable DYN input panel width to the dynamic prompt hint width, capped at 245 DIPs.
- Added typo-tolerant command suggestions using edit distance after exact, prefix, and alias matches.
- Made Space and Enter submit the same command-line action.
- Expanded the Properties palette into General, 3D Visualization, View, and Misc groups with selection-aware metadata.
- Added parallel and perpendicular relation OSNAP modes. Relation snaps use nearby indexed straight segments and the current construction reference point.
- Improved polar tracking with a 15-degree increment and added object tracking alignment guides from OSNAP points acquired while hovering during the active command.

## Notes
- Parallel/perpendicular relation snaps currently use LINE and straight POLYLINE segments; curved segments and 3D relations are not included.
- OTRACK uses acquired OSNAP points as temporary alignment anchors; full AutoCAD-style tracking acquisition/release timing is not yet implemented.
- Property rows are currently display-oriented; editing all DWG native properties is not part of this change.
- Build and interactive Windows validation are still required.
