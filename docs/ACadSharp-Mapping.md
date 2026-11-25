# DWG entity support audit

ACadSharp is compiled from `vendor/ACadSharp/src/ACadSharp/ACadSharp.csproj` and referenced by `iStCAD_1.Infras`. The file adapter is an in-process .NET component; no network service is used.

| iStCAD_1 operation | ACadSharp mapping |
|---|---|
| Open `.dwg` / `.dxf` | `DwgReader` / `DxfReader` → `ICadReader.Read()` → `CadDocument` (extension is selected case-insensitively) |
| Import line | `ACadSharp.Entities.Line` ↔ `LineEntity` |
| Import circle | `ACadSharp.Entities.Circle` ↔ `CircleEntity` |
| Import arc | `ACadSharp.Entities.Arc` ↔ `ArcEntity` (radians converted to degrees in Core) |
| Import lightweight polyline | `ACadSharp.Entities.LwPolyline` ↔ `PolylineEntity` (per-vertex bulge and closed flag retained) |
| Import text | `TextEntity` / `MText` ↔ `CadTextEntity` / `CadMTextEntity`; current text edit dialog can update these model entities |
| Read spaces | Every `Layout.AssociatedBlock.Entities` collection is enumerated; layouts can be selected in the UI. Paper-space viewport transforms and clipping are not yet applied to model-space rendering. |
| Native identity | Loaded entities now carry the hexadecimal ACadSharp handle in `CadEntity.NativeHandle`; the editor GUID is deterministically derived from that handle when available. The Properties palette exposes the native handle. Newly created/copied entities have no original handle until written by ACadSharp. |
| Database audit | On open, the command line reports counts for layers, block records, layouts, linetypes, text/dimension styles, UCS/view records, registered apps, reachable native objects, XData groups/records, XRecords and extension dictionaries. These are inventory counts, not proof that every feature is rendered or round-trips without loss. |
| Preserve native document | The in-memory ACadSharp `CadDocument` remains the save source. The adapter tracks model IDs back to original native entities, so unedited native properties, including properties not represented in Core, are not intentionally reconstructed from scratch. This is not yet verified as a lossless round trip for every DWG feature. |
| Save `.dwg` / `.dxf` | ACadSharp `DwgWriter` / `DxfWriter` writes the retained document after mapped-entity updates. |
| Other/unknown entity | Kept as an unsupported `ProxyCadEntity` in the UI model while its native ACadSharp object remains in the document. Preview uses extracted geometry, proxy graphics or extents where available. It is not a semantic/editable representation. |

## Requested entity coverage (source audit)

“Native read” means the current ACadSharp reader constructs an entity in the retained document. “Preview” describes the current 2D WPF approximation, not AutoCAD-equivalent display. “Editable” means a corresponding Core type is mapped and can participate in supported editor operations; it does not mean every native property is editable.

| Entity family | Native read | Current model / display path | Editing and accuracy status |
|---|---|---|---|
| LINE | Yes | Direct `LineEntity` and WPF line geometry | Mapped and transform-editable; only XY is represented. |
| POINT | Yes | Proxy cross marker | Preview only; point style, size and PDMODE behavior are not implemented. |
| CIRCLE | Yes | Direct `CircleEntity` | Mapped and transform-editable; plane normal/elevation are not represented in the 2D model. |
| ARC | Yes | Direct `ArcEntity` | Mapped and transform-editable; normal/OCS orientation is lost in the current XY projection. |
| ELLIPSE | Yes | Tessellated into a proxy path | Preview only; not a native ellipse/edit handle representation. |
| SPLINE | Yes | Polygonal tessellation into a proxy path | Preview only; control points, knots and fit data are not editable. |
| SOLID (2D) | Yes | Four-corner proxy outline | Preview only; fill/solid semantics are not retained in the Core entity. |
| TRACE | Yes when ACadSharp decodes it | Generic proxy/extents fallback; no explicit Trace branch | Coverage depends on available proxy data; display is not verified. |
| LWPOLYLINE | Yes | Direct polyline; bulge is tessellated for drawing | Mapped and transform-editable; widths, elevation, thickness and OCS are not modeled. |
| POLYLINE / 3D POLYLINE | Yes | Proxy path from vertices | Preview only; current path flattens vertices to XY and does not preserve mesh/face semantics. |
| HATCH | Yes | Boundary edges converted to proxy paths | Boundary-only preview; pattern, scale, angle, islands/fill rules and gradient are not rendered. |
| REGION | Yes when ACadSharp decodes it | Generic proxy graphics/extents fallback | No region topology/Boolean or editable boundary model. |
| TEXT | Yes | Direct `CadTextEntity`; WPF glyph outlines | Basic content/placement editable. SHX falls back to Arial; font substitution, alignment and Unicode need fixture comparison. |
| MTEXT | Yes | Direct `CadMTextEntity`; partial MTEXT formatting parser | Content/placement editable. Formatting-code coverage and AutoCAD line wrapping are incomplete. |
| DIMENSION | Yes | Associated block graphics and nested text become a proxy preview | Static appearance only; measurement, dimension style, overrides and associative references are not modeled. |
| LEADER | Yes | Leader vertices become a proxy path | Static path only; arrowhead/style/annotation association are incomplete. |
| MLEADER | Yes when ACadSharp decodes it | Generic proxy graphics/block preview if exposed | No dedicated semantic mapping; exact content, context and style are not editable. |
| TOLERANCE | Yes when ACadSharp decodes it | Generic proxy/extents fallback | No dedicated tolerance-frame display or editing. |
| INSERT / nested block | Yes | Exploded preview paths and extracted text, bounded by recursion/point limits | Native block definitions remain in the retained document, but UI selection/editing is proxy-level; attributes and nested transforms are not independently editable. |
| Dynamic block | Native insert/block data is retained when decoded | Best-effort insert/proxy preview | Visibility states, dynamic properties and evaluation are not implemented. |
| ATTRIB / ATTDEF | Native data retained | Insert attributes may appear in exploded preview; standalone text-derived instances may map as text | Tag/prompt/constant semantics and attribute editing are not modeled. |
| XREF | References may be retained as native document data | Best-effort insert/proxy preview; external dependency resolution is not implemented | Path rebinding, reload, clipping and nested external-reference display are unsupported. |
| 3DSOLID / SURFACE | Native object retained when decoded | Proxy graphics or extents fallback | No solid/surface tessellation, topology or 3D viewport renderer. |
| MESH / polyface | Native object retained when decoded | Best-effort proxy graphics or extents; limited proxy-mesh path extraction | No general native mesh-face rendering, materials or 3D selection. |
| Layout / viewport / UCS | Tables and layout records are read in the native document | Layout names and entities per layout are exposed | Viewport scale/twist/clip, UCS state and paper-space composition are not yet reproduced. |
| XData / XRecord / extension dictionaries | Retained on original native objects where ACadSharp reads them | Not copied into the Core editing model or exposed in Properties | No application-level inspect/edit API; preservation still needs round-trip fixtures. |

The reader reports Model-Space-only entity type and layer counts separately from per-layout counts. `DocumentEntityCount` still includes entities across block records, including block definitions. These counts alone do not establish display fidelity. `DocumentEntityCount` includes entities across block records; `NativeEntityCount` reports model-space entities. For meaningful coverage metrics, fixtures should separately count model space, each layout, block definitions, nested inserts and external references. Handles are scoped to a drawing database; the deterministic editor GUID is only an in-memory lookup key and is not a substitute for handle/reference validation.

## Main gaps and implementation order

1. **Establish fixtures and measured baseline:** keep a source DWG/DXF for each family above, record per-space/type counts, open diagnostics, render screenshots and timings. Compare with AutoCAD at the same view state.
2. **Preserve semantic identity:** replace random session-only GUID mapping with stable native handles plus document identity; explicitly retain block/attribute/XData/XRecord relationships and surface unsupported objects in Properties.
3. **Expand Core geometry:** add 3D coordinates and entity-specific models for ellipse, spline, hatch, dimension, leader, insert/attributes, mesh and viewport state. Keep native records as the source of truth until a supported edit is committed.
4. **Specialized renderers:** implement OCS/WCS transforms, ACI/true color/transparency, line styles, text styles, dimension blocks, hatch pattern fills, block transforms and paper-space viewport composition as separate builders.
5. **Round-trip safety:** run open-save-reopen comparisons in AutoCAD and assert handles, entity counts, layers, blocks, styles, XData/XRecords and references. Never treat a successful writer call as proof of fidelity.

Do not select a new DWG SDK solely from the requested entity list. First run this fixture suite against ACadSharp to identify whether the missing data originates in file decoding, the Core projection, or rendering. If database access, proxy display or round-trip behavior remains insufficient, compare RealDWG and ODA Drawings SDK capabilities, supported platforms, licensing and redistribution constraints against those specific gaps.

The entity type summary and per-space counts are reported in the command line when a file opens. Preview support does not imply edit support or exact AutoCAD display fidelity; custom objects and several advanced graphics still depend on ACadSharp's native reader and proxy data.

ACadSharp's upstream version and commit are recorded in `vendor/ACadSharp/UPSTREAM.md`. Its upstream README contains the maintained DWG/DXF reader/writer compatibility table; a target version must be checked against that table when selecting an output format.
