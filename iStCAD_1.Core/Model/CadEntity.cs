using iStCAD_1.Core.Geometry;

namespace iStCAD_1.Core.Model;

public abstract record CadEntity(Guid Id, string Layer)
{
    /// <summary>Native DWG/DXF handle in hexadecimal. Null for entities created in iStCAD_1.</summary>
    public string? NativeHandle { get; init; }
    public CadColor? DisplayColor { get; init; }
    /// <summary>Null means the native CAD object uses ByLayer color.</summary>
    public CadColor? ExplicitColor { get; init; }
    /// <summary>Resolved DXF linetype pattern, in drawing units; null means continuous.</summary>
    public IReadOnlyList<double>? LineTypePattern { get; init; }
    /// <summary>True when the native color is ByBlock (ExplicitColor stays null).</summary>
    public bool ColorByBlock { get; init; }
    /// <summary>Native linetype name: null = ByLayer, otherwise "ByBlock", "Continuous" or a table name (as in AutoCAD).</summary>
    public string? LineTypeName { get; init; }
    /// <summary>Explicit lineweight in mm; null = ByLayer / ByBlock / Default.</summary>
    public double? LineWeightMm { get; init; }
    public double LineTypeScale { get; init; } = 1;
    public string TextFontFamily { get; init; } = "Arial";
    public bool TextBold { get; init; }
    public bool TextItalic { get; init; }
    public double TextObliqueAngleRadians { get; init; }
    public double TextWidthFactor { get; init; } = 1;
}
public readonly record struct CadColor(byte R, byte G, byte B);
public sealed record LineEntity(Point2 Start, Point2 End, Guid EntityId, string EntityLayer = "0") : CadEntity(EntityId, EntityLayer);
public sealed record CircleEntity(Point2 Center, double Radius, Guid EntityId, string EntityLayer = "0") : CadEntity(EntityId, EntityLayer);
public sealed record ArcEntity(Point2 Center, double Radius, double StartAngleDegrees, double EndAngleDegrees, Guid EntityId, string EntityLayer = "0") : CadEntity(EntityId, EntityLayer);
public sealed record ProxyPreviewPath(IReadOnlyList<Point2> Points, bool Closed = false);
public sealed record ProxyPreviewText(string Text, Point2 Position, double Height, double RotationRadians = 0,
    double Width = 0, double LineSpacing = 1, int AttachmentPoint = 1,
    string? FontFamily = null, bool Bold = false, bool Italic = false, double Oblique = 0, double WidthFactor = 1,
    bool IsMText = false, string? Formatted = null, int HAlign = 0, int VAlign = 0, string? Tag = null);
/// <summary>Parametric definition of one complete DIMENSION object. Kind: LINEAR, ALIGNED, RADIUS, DIAMETER, ANGULAR.
/// Points: LINEAR/ALIGNED [p1,p2,lineLocation]; RADIUS/DIAMETER [center,textPoint] + Radius; ANGULAR [l1a,l1b,l2a,l2b,arcPoint].</summary>
public sealed record CadDimensionData(string Kind, IReadOnlyList<Point2> Points, double Radius, CadDimensionStyleDefinition Style, string? TextOverride = null);
/// <summary>Block reference (INSERT) of an opened drawing: lets the app move/copy/rotate/scale it as a whole and write it back as the same block.</summary>
/// <summary>One ATTRIB of a block reference. The properties after <paramref name="Value"/> mirror AutoCAD's Enhanced Attribute Editor
/// (Text Options / Properties tabs); they are only written back when <paramref name="Detailed"/> is set (read from the DWG).
/// HAlign: 0 Left, 1 Center, 2 Right, 3 Aligned, 4 Middle, 5 Fit. VAlign: 0 Baseline, 1 Bottom, 2 Middle, 3 Top.
/// Color: "ByLayer", "ByBlock", an AutoCAD color index, or "#RRGGBB". LineWeight: "ByLayer", "ByBlock", "Default" or millimetres.</summary>
public sealed record CadInsertAttribute(string Tag, string Value, string? Prompt = null, string? TextStyle = null, int HAlign = 0, int VAlign = 0,
    bool Backwards = false, bool UpsideDown = false, double Height = 0, double WidthFactor = 1, double RotationDegrees = 0, double ObliqueDegrees = 0,
    string? Layer = null, string? LineType = null, string? Color = null, string? LineWeight = null, bool Detailed = false);
public sealed record CadInsertData(string BlockName, Point2 Position, double Rotation, double ScaleX, double ScaleY, IReadOnlyList<CadInsertAttribute>? Attributes = null, int AttributeDefinitions = 0);
/// <summary>HATCH: one or more closed boundary loops (world points, one list per loop) filled with parallel pattern lines
/// at AngleDegrees, Spacing apart. The actual pattern line segments for display/hit-test live in ProxyGraphicsPaths (computed
/// once when the hatch is created or transformed); this record is only the compact data used to rebuild a real native
/// HATCH entity on save, and to keep the pattern in sync when the hatch is moved/rotated/scaled/mirrored as a whole.</summary>
public sealed record CadHatchData(IReadOnlyList<IReadOnlyList<Point2>> Boundaries, double AngleDegrees, double Spacing);
/// <summary>LEADER/MLEADER: one straight leader segment from Points[0] (arrowhead) to Points[^1] (where the short horizontal
/// landing line starts), with Text placed at the end of the landing. Shared by both the QLEADER and MLEADER commands in this
/// app (same visual shape and in-app editing/rendering code); saved as a real native LEADER entity (the pre-2008 "classic"
/// leader) when <see cref="IsMultiLeader"/> is false (QLEADER), or as a true native MULTILEADER entity when it is true
/// (MLEADER) - the two commands differ only at the native-save step, not in how they're drawn/edited in this app.</summary>
public sealed record CadLeaderData(IReadOnlyList<Point2> Points, string Text, double TextHeight, double ArrowSize, bool IsMultiLeader = false);
/// <summary>ATTDEF: an attribute-definition placeholder (not yet part of a block). Shown in the drawing as its Tag text
/// (matching AutoCAD, which displays the Tag string for an un-blocked attribute definition). Height/Rotation/alignment mirror
/// a normal TEXT entity; HAlign/VAlign use the same codes as <see cref="CadInsertAttribute"/>. Once the containing objects are
/// turned into a block via BLOCK, every ATTDEF among the selection becomes an AttributeDefinition member of that block, and any
/// INSERT of the block creates one ATTRIB (see <see cref="CadInsertAttribute"/>) per definition, defaulted to <see cref="DefaultValue"/>.</summary>
public sealed record CadAttributeDefData(string Tag, string Prompt, string DefaultValue, bool Invisible, double Height,
    double RotationDegrees = 0, int HAlign = 0, int VAlign = 0, double WidthFactor = 1);
/// <summary>ELLIPSE: a true parametric ellipse (or elliptical arc), stored exactly like AutoCAD's own native ELLIPSE entity -
/// center, major-axis endpoint (world point, not just a direction, for simplicity), ratio of minor to major axis, and the
/// start/end parameter in radians (0..2*PI for a full ellipse). Kept as ONE object (not a many-segment polyline approximation)
/// so MOVE/COPY/ROTATE/SCALE/MIRROR are exact and the file saves a real native ELLIPSE AutoCAD can edit with its own grips.</summary>
public sealed record CadEllipseData(Point2 Center, Point2 MajorAxisEndpoint, double RadiusRatio, double StartParameter = 0, double EndParameter = Math.Tau);
/// <summary>SPLINE: a true cubic NURBS curve through the picked fit points, stored exactly like AutoCAD's own native SPLINE
/// entity (control points + knot vector + weights, derived once from FitPoints/Degree/Closed the same way AutoCAD's own
/// "fit points" method does) instead of a many-segment polyline approximation. Kept as ONE object so MOVE/COPY/ROTATE/SCALE/
/// MIRROR are exact and the file saves a real native SPLINE AutoCAD can edit with its own control-point/fit-point grips.</summary>
public sealed record CadSplineData(IReadOnlyList<Point2> FitPoints, IReadOnlyList<Point2> ControlPoints, IReadOnlyList<double> Knots,
    IReadOnlyList<double> Weights, int Degree, bool Closed);
/// <summary>WIPEOUT: a closed boundary (world points) that masks whatever is behind it, stored as a real native WIPEOUT
/// entity (InsertPoint + U/V basis vectors + Size define a local "pixel" space, matching real AutoCAD's convention;
/// here U/V are kept at 1 world unit per "pixel" for simplicity, so ClipBoundaryVertices on save are just Boundary minus
/// the bounding-box corner). iStCAD only draws the boundary outline in its own canvas (does not yet actually mask
/// objects behind it on screen); AutoCAD itself will mask correctly since the saved object is a true WIPEOUT.</summary>
public sealed record CadWipeoutData(IReadOnlyList<Point2> Boundary);
/// <summary>TABLE: a simplified uniform grid of Columns x Rows cells with plain text, stored as a real native TABLE
/// entity (post-2008 AcDbTable/TABLECONTENT object). Deliberately simpler than real AutoCAD TABLE to draw/edit: no
/// Insert-from-style dialog, no merged cells, no per-row/per-column custom sizing - just pick the overall rectangle
/// (drag both width AND height at once, "kéo 2 phương", like RECTANG) and a column/row count, then the grid divides
/// evenly. Position+Columns+Rows+ColumnWidth+RowHeight fully determine the grid geometry; CellTexts is row-major
/// (index = row*Columns+col). NOTE: the vendored ACadSharp DXF writer does not support TABLE at all (DWG only) -
/// a table saved to .dxf will be dropped on write, so TABLE should only be relied on when saving .dwg.</summary>
public sealed record CadTableData(Point2 Position, int Columns, int Rows, double ColumnWidth, double RowHeight, IReadOnlyList<string> CellTexts, double TextHeight);
/// <summary>VIEWPORT: a paper-space "window" onto model space, stored as a real native Viewport entity (only valid on
/// a Layout/Paper-space tab, like AutoCAD's own MVIEW). Deliberately simpler to draw than real AutoCAD MVIEW: one
/// drag of a rectangle on the paper (Center/Width/Height, paper-space units) is enough - iStCAD fills in ViewCenter/
/// ViewHeight itself by fitting the current Model space extents into that rectangle (preserving aspect ratio), so the
/// viewport immediately shows the whole drawing; the user can MOVE/SCALE the rectangle afterward like any object.
/// ViewCenter/ViewHeight are in MODEL space units/coordinates (what part of the model the window looks at); Width/
/// Height/Center are in the paper space the viewport rectangle itself is drawn in. FrozenLayers is always empty for a
/// viewport iStCAD creates (no per-viewport layer-freeze UI yet; a value read back from a real AutoCAD file on import
/// is kept for display but cannot yet be edited here). ROTATE/MIRROR are not supported yet (the object stays in
/// place), same disclosed gap as TABLE/ELLIPSE's underlying axis-aligned model - MOVE/COPY/SCALE (uniform) all work.</summary>
public sealed record CadViewportData(Point2 Center, double Width, double Height, Point2 ViewCenter, double ViewHeight, IReadOnlyList<string> FrozenLayers);
public sealed record ProxyCadEntity(string NativeType, Point2 BoundsMin, Point2 BoundsMax, bool HasBounds, Guid EntityId, string EntityLayer = "0",
    IReadOnlyList<IReadOnlyList<Point2>>? PreviewPaths = null, bool PreviewClosed = false, string? PreviewText = null, double PreviewTextHeight = 0, double PreviewTextRotation = 0,
    IReadOnlyList<ProxyPreviewPath>? ProxyGraphicsPaths = null, IReadOnlyList<ProxyPreviewText>? ProxyGraphicsTexts = null,
    Point2? PreviewTextPosition = null, double PreviewTextWidth = 0, double PreviewTextLineSpacing = 1, int PreviewTextAttachment = 1,
    bool PreviewFilled = false, CadDimensionData? Dimension = null, CadInsertData? InsertData = null, CadHatchData? Hatch = null,
    CadLeaderData? Leader = null, CadAttributeDefData? AttributeDef = null, CadEllipseData? Ellipse = null, CadSplineData? Spline = null,
    CadWipeoutData? Wipeout = null, CadTableData? Table = null, CadViewportData? Viewport = null) : CadEntity(EntityId, EntityLayer);

public readonly record struct PolylineVertex(Point2 Point, double Bulge = 0.0);
public sealed record PolylineEntity(IReadOnlyList<PolylineVertex> Vertices, bool Closed, Guid EntityId, string EntityLayer = "0") : CadEntity(EntityId, EntityLayer);
public sealed record CadTextEntity(string Value, Point2 Position, double Height, double RotationRadians, double WidthFactor,
    int HorizontalAlignment, int VerticalAlignment, Guid EntityId, string EntityLayer = "0") : CadEntity(EntityId, EntityLayer);
public sealed record CadMTextEntity(string Value, string FormattedValue, Point2 Position, double Height, double RotationRadians,
    double Width, double LineSpacing, int AttachmentPoint, Guid EntityId, string EntityLayer = "0") : CadEntity(EntityId, EntityLayer);
