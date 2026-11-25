# iStCAD v1.0.49.21 — Edge toolbar, transform previews, PLINE and viewport grid

## Implemented in source
- Edge toolbar can be pinned open or return to hover auto-hide.
- COPY and MOVE show a transformed selected-geometry preview from base point to cursor before commit.
- ROTATE command and toolbar control preview selected geometry around a picked base point; angle can be supplied in degrees or by pointing.
- PLINE is a distinct command: click/type vertices, Enter finishes, C closes, Backspace removes the last vertex. The preview follows the cursor and one PolylineEntity is committed.
- Grid uses a consistent charcoal background, subdued minor/major lines, red X axis and green Y axis.
- Grid image remains visible and is transformed during pan/zoom rather than being hidden and rebuilt at the end of every gesture, reducing the grid flash.

## Notes / limitations
- Transform previews are capped at 5,000 selected entities per frame to bound UI work; all selected entities are still transformed on commit.
- Proxy entities remain read-only for MOVE/COPY/ROTATE.
- Rotation preview uses the vector from the base point to the pointer as an angle from the positive X axis; typed numeric values are interpreted as degrees.
- This environment has no .NET SDK, so the WPF solution has not been compiled or interactively tested here.
