﻿using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using iStCAD_1.Application.Editing;
using iStCAD_1.Application.Files;
using iStCAD_1.Application.Input;
using iStCAD_1.Application.Spatial;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;
using iStCAD_1.Infras;
using iStCAD_1.Rendering;

namespace iStCAD_1;

public partial class MainWindow : Window
{
    private sealed class DrawingSession
    {
        public CadEditor Editor { get; } = new();
        public string? Path { get; set; }
        public bool IsDirty { get; set; }
        public long Revision { get; set; }
        public bool IsAutosaving { get; set; }
        public bool TrackInitialViewport { get; set; }
        public long InitialViewportStartTimestamp { get; set; }
        public string? RecoveryPath { get; set; }
        public CadSpatialIndex? SpatialIndex { get; set; }
        /// <summary>Spatial index of Model space used to draw layout viewports; rebuilt lazily after edits.</summary>
        public CadSpatialIndex? ModelViewportIndex { get; set; }
        /// <summary>Views saved with VIEW S (center + scale), per drawing tab, for this session.</summary>
        public Dictionary<string, (Point2 Center, double Scale)> NamedViews { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long GeometryRevision { get; set; }
        // Snapshot color/visibility metadata once per geometry revision, not on every zoom.
        public long RenderMetadataRevision { get; set; } = -1;
        public HashSet<Guid>? RenderHiddenSnapshot { get; set; }
        public Dictionary<string, CadColor>? RenderColorSnapshot { get; set; }
        public HashSet<Guid> SelectedEntityIds { get; } = [];
        public string DisplayName => Path is null ? $"{Editor.Drawing.Name}.dwg" : IOPath.GetFileName(Path);
        public DrawingSession(CadDrawing? drawing = null, string? path = null)
        {
            Path = path;
            if (drawing is not null) Editor.SetDrawing(drawing);
        }
    }

    public sealed record CommandHint(string Name, string Aliases, string Description)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Aliases) ? Name : $"{Name}   [{Aliases}]";
    }

    private sealed record FrequentCommand(string Command, string Caption, string Icon);

    private sealed record CadColorOption(string Key, string DisplayText, Brush SwatchBrush);
    private sealed record CadLineStyleOption(string Key, string Display) { public override string ToString() => Display; }

    private static readonly FrequentCommand[] FrequentCommandCatalog =
    [
        new("LINE", "Line", "╱"), new("PLINE", "Polyline", "⌁"), new("CIRCLE", "Circle", "○"), new("RECTANGLE", "Rectangle", "□"), new("ARC", "Arc", "⌒"),
        new("MOVE", "Move", "✥"), new("COPY", "Copy", "▣"), new("ROTATE", "Rotate", "⤾"), new("ERASE", "Erase", "×"), new("SELECT", "Select", "↖"),
        new("ZE", "Zoom extents", "⌕"), new("ZPREVIOUS", "Zoom previous", "↶"), new("PAN", "Pan", "✥"), new("UNDO", "Undo", "↶"),
        new("REDO", "Redo", "↷"), new("NEW", "New drawing", "＋"), new("OPEN", "Open drawing", "▱"), new("SAVE", "Save", "▣"),
        new("SAVEAS", "Save as", "↗"), new("PLOT", "Plot / PDF", "▤"), new("LAYOUT", "New layout", "▦"), new("REGEN", "Regenerate", "⟳")
    ];

    private static readonly IReadOnlyDictionary<OsnapMode, Geometry> OsnapMarkerGeometries = new Dictionary<OsnapMode, Geometry>
    {
        [OsnapMode.Endpoint] = CreateFrozenGeometry("M-5,-5 H5 V5 H-5 Z"),
        [OsnapMode.Midpoint] = CreateFrozenGeometry("M0,-6 L6,5 H-6 Z"),
        [OsnapMode.Center] = CreateFrozenGeometry("M-5,0 A5,5 0 1 0 5,0 A5,5 0 1 0 -5,0 M-7,0 H7 M0,-7 V7"),
        [OsnapMode.Quadrant] = CreateFrozenGeometry("M-5,-5 H5 V5 H-5 Z M-7,0 H7 M0,-7 V7"),
        [OsnapMode.Intersection] = CreateFrozenGeometry("M-5,-5 L5,5 M5,-5 L-5,5 M-7,0 H7 M0,-7 V7"),
        [OsnapMode.Insertion] = CreateFrozenGeometry("M0,-6 L6,0 L0,6 L-6,0 Z"),
        [OsnapMode.Parallel] = CreateFrozenGeometry("M-7,-3 H7 M-7,3 H7 M-4,-6 L-7,-3 L-4,0 M4,0 L7,3 L4,6"),
        [OsnapMode.Perpendicular] = CreateFrozenGeometry("M-7,6 H0 V-7 M-5,1 H0 V-4")
    };

    private static Geometry CreateFrozenGeometry(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }

    private static readonly CommandHint[] CommandHints =
    [
        new("LINE", "L", "Draw line segments; C = close, U = undo the last segment."),
        new("PLINE", "PL", "Polyline with line and tangent-arc segments: A = arc, L = line, C = close, U = undo."),
        new("CIRCLE", "C", "Circle from center and radius (D = diameter), 2P = two diameter ends, 3P = three points."),
        new("RECTANGLE", "REC, RECT", "Draw a rectangle from two corners."),
        new("ARC", "A", "Arc through three points (start, second, end); C = center, start, end."),
        new("ZOOM", "Z, ZE, ZEXTENTS", "Zoom: ZOOM E/A/P/W/O, ZOOM C x,y [h], ZOOM S n or nX."),
        new("OVERKILL", "CLEANUP", "Remove redundant/invalid objects: duplicates, zero-length/empty, repeated vertices, overlapping collinear lines. OVERKILL CHECK = report only; far-away stray objects are selected."),
        new("DIMSTYLE", "D, DST, DDIM", "Open the Dim Style table."),
        new("POLYGON", "POL", "Regular polygon from sides, center and radius (inscribed, or type a number ending in C for circumscribed); same as RPOLYGON."),
        new("RPOLYGON", "RPOL", "Regular polygon from sides, center and radius (inscribed or circumscribed)."),
        new("ELLIPSE", "EL", "Ellipse from center and two semi-axes (real native ELLIPSE, not a polyline)."),
        new("DONUT", "DO", "Two concentric circles from inside and outside diameter."),
        new("REVCLOUD", "", "Revision cloud: click the corner points of the area, Enter/C closes it; scalloped arcs are added automatically (real bulge-arc polyline)."),
        new("WIPEOUT", "", "Mask an area: click its corner points, Enter/C closes it (real native WIPEOUT; AutoCAD masks what's behind it, iStCAD's own view shows only the outline)."),
        new("TABLE", "TB", "Simple grid: enter columns, rows, then drag a rectangle to size the whole table (real native TABLE). Double-click a cell afterwards to type its text."),
        new("VIEWPORT", "MVIEW", "Layout tabs only: drag a rectangle to place a paper-space window onto Model space (real native VIEWPORT). Fills in the view automatically from Model's current extents."),
        new("LENGTHEN", "LEN", "Lengthen or shorten lines, arcs and polyline ends: DElta, Percent, Total or DYnamic (drag the endpoint)."),
        new("SELECTSIMILAR", "SSIM", "Select all objects of the same type and layer as the selection."),
        new("PEDIT", "PE", "Polyline edit: C close, O open, R reverse, J join selected lines."),
        new("REVERSE", "", "Reverse the direction of selected polylines."),
        new("COPYCLIP", "COPYBASE", "Copy selected objects to the clipboard."),
        new("CUTCLIP", "", "Cut selected objects to the clipboard."),
        new("PASTECLIP", "PASTE", "Paste clipboard objects at a picked point."),
        new("DWGCHECK", "FOREIGNDWG", "Explain how to stop AutoCAD showing \"Foreign DWG File\" for files saved here (AutoCAD setting DWGCHECK = 0, or save as DXF)."),
        new("VNFIX", "FIXVIET, VIETFIX", "Repair Vietnamese text whose encoding does not match its font (TCVN3 / VNI / Unicode). VNFIX CHECK = report only; VNFIX UNICODE = convert to Unicode + Arial; VNFIX TCVN3|VNI forces the source; VNFIX INFO = diagnostics; default = keep the font of the first character when installed (decode content to correct Unicode, re-encode for that font), else substitute an installed font; VNFIX AUTO = always substitute."),
        new("RENDERMODE", "RM", "Display quality: RENDERMODE AUTO | QUALITY | BALANCED | FAST. Auto picks by drawing size and measured speed."),
        new("NATIVESTATUS", "NATIVE", "Report whether the optional Rust native engine (native/istcad_native, for heavy-file spatial indexing) is built and loaded, with a quick self-test."),
        new("VIEW", "-VIEW", "Named views: VIEW S name (save), VIEW R name (restore), VIEW D name, VIEW (list)."),
        new("ZOOMIN", "ZI", "Zoom in 2x."), new("ZOOMOUT", "ZOUT", "Zoom out 2x."), new("ZW", "ZWINDOW", "Zoom window by two corner clicks."),
        new("ZPREVIOUS", "ZP", "Restore the previous view."),
        new("PAN", "", "Pan with the middle mouse button."),
        new("MOVE", "M", "Move selected objects."),
        new("COPY", "CO", "Copy selected objects; keeps placing copies until Enter/Esc (Exit); U = undo the last copy placed."),
        new("ROTATE", "RO", "Rotate selected objects; C = rotate a copy, R = reference angle."),
        new("ERASE", "E, DELETE, DEL", "Erase selected objects."),
        new("SELECT", "", "Select drawing objects."),
        new("UNDO", "U", "Undo the last drawing change."),
        new("REDO", "Y", "Redo the last undone change."),
        new("NEW", "QNEW", "Create a new drawing."),
        new("OPEN", "", "Open a DWG or DXF drawing."),
        new("SAVE", "", "Save the current drawing."),
        new("SAVEAS", "SAVE AS", "Save the drawing to a new DWG or DXF file."),
        new("PLOT", "PDF, EXPORTPDF", "Choose paper settings and export a PDF."),
        new("LAYOUT", "NEWLAYOUT", "Create a paper layout."),
        new("REGEN", "", "Redraw the current view."),
        new("PERF", "PERFORMANCE", "Show drawing performance and resource measurements."),
        new("BENCH", "BENCHMARK", "Benchmark viewport queries and object picking on the open drawing."),
        new("SKIPTEXT", "", "Toggle TEXT/MTEXT viewport rendering off/on for performance testing."),
        new("OFFSET", "O", "Offset lines, arcs, circles and polylines: distance or Through/Erase/Layer; then Exit/Multiple/Undo."),
        new("TRIM", "TR", "Trim lines, arcs, circles and polylines at the nearest crossing objects; Shift+click extends."),
        new("EXTEND", "EX", "Extend the picked end of a line, arc or polyline to the next boundary; Shift+click trims."),
        new("FILLET", "F", "Round or join two lines, or every corner of a picked polyline: pick-first or pick-after; R = radius, T = Trim/No trim, U = undo the last one (remembered)."),
        new("CHAMFER", "CHA", "Bevel two lines, or every corner of a picked polyline: D = distance, T = Trim/No trim, U = undo the last one (remembered)."),
        new("MIRROR", "MI", "Mirror selected objects about a line by two points; asks whether to erase the source objects."),
        new("SCALE", "SC", "Scale selected objects by a factor; C = scale a copy, R = reference length."),
        new("STRETCH", "S, STR", "Stretch with a crossing window: vertices inside move."),
        new("ARRAY", "AR", "Rectangular or polar array of selected objects (also ARRAYRECT, ARRAYPOLAR)."),
        new("EXPLODE", "X", "Explode polylines (lines and arcs), blocks and dimensions into basic objects."),
        new("JOIN", "J", "Join collinear lines into one line; lines, arcs and polylines that touch end to end into a polyline."),
        new("BREAK", "BR", "Break a line, arc, circle or polyline between two points (F = first point, @ = same point)."),
        new("HATCH", "H", "Fill a closed boundary with parallel pattern lines (angle; spacing)."),
        new("TEXT", "DT", "Create single-line text."),
        new("MTEXT", "T", "Create multiline text."),
        new("TEXTEDIT", "ED", "Edit a TEXT/MTEXT's content, height, rotation and color (same dialog as double-click)."),
        new("QLEADER", "LE", "Leader: arrow point, landing point, then type the annotation text."),
        new("MLEADER", "MLD", "Same input as QLEADER (arrow, landing, text) but saved as a true native MULTILEADER object."),
        new("DIMLINEAR", "DLI, DIMLIN, DIM", "Linear dimension: two extension origins, then dimension line location."),
        new("DIMALIGNED", "DAL", "Aligned dimension parallel to the two picked points."),
        new("DIMRADIUS", "DRA", "Radius dimension for a circle or arc."),
        new("DIMDIAMETER", "DDI", "Diameter dimension for a circle or arc."),
        new("DIMANGULAR", "DAN", "Angular dimension between two lines."),
        new("DIMCONTINUE", "DCO", "Chain a new linear dimension from the last (or a picked) one's second origin, same dimension line."),
        new("DIMBASELINE", "DBA", "Stack a new linear dimension from the first dimension's first origin, offset further out each time."),
        new("DIST", "DI", "Measure distance between two points."),
        new("ALIGN", "AL", "Move/rotate selected objects by one or two pairs of source and destination points."),
        new("QSELECT", "QSEL", "Select objects by type, layer and color."),
        new("FILTER", "FI", "Pick types, layers, colors and linetypes in one compact form; live selection with smart counts."),
        new("LIST", "LIST", "Show the properties of an object (length, area, handle, ...)."),
        new("SPLINE", "SPL", "Smooth curve through picked points (stored as a fine polyline)."),
        new("XLINE", "XL", "Construction line: two points, or Hor/Ver/Ang/Bisect/Offset."),
        new("DIMCENTER", "DCE", "Draw a small cross at the center of a selected arc or circle."),
        new("COLOR", "COL", "Set the current color by name, ACI number or R,G,B (typed, no dialog)."),
        new("LTSCALE", "LTS", "Global linetype scale (default 100, for the standard 1:100 plot); updates existing objects too."),
        new("LAYOFF", "", "Turn off the layer of each picked object."),
        new("LAYON", "", "Turn on all layers."),
        new("LAYFRZ", "", "Freeze the layer of each picked object."),
        new("LAYTHW", "", "Thaw all layers."),
        new("LAYLCK", "", "Lock the layer of a picked object."),
        new("LAYULK", "", "Unlock the layer of a picked object."),
        new("LAYISO", "", "Isolate the layers of the selected objects (other layers off)."),
        new("LAYUNISO", "", "Restore the layers hidden by LAYISO."),
        new("LAYMCUR", "", "Make the layer of a picked object current."),
        new("LAYCUR", "", "Move the selected objects to the current layer."),
        new("OOPS", "", "Restore the objects removed by the last ERASE."),
        new("ORTHO", "", "Toggle Ortho mode."),
        new("OSNAP", "OS", "Toggle running object snaps."),
        new("GRID", "", "Toggle the grid."),
        new("DIVIDE", "DIV", "Place tick marks that divide an object into equal segments."),
        new("MEASURE", "MEASURE", "Place tick marks at a fixed distance along an object."),
        new("AREA", "AA", "Measure area of selected objects."),
        new("INSERT", "I", "Insert another DWG/DXF file at a picked point."),
        new("BLOCK", "B", "Block tools."),
        new("MATCHPROP", "MA", "Copy properties from one object to others."),
        new("PROPERTIES", "PR", "Open the editable Properties palette."),
        new("PURGE", "PU", "Remove unused definitions."),
        new("OVERKILL", "OV", "Remove redundant/invalid objects. OVERKILL CHECK = report only."),
        new("ALIASES", "SHORTCUTS", "Edit custom command shortcuts, including Vietnamese accents."),
        new("QUIT", "EXIT", "Close iStCAD_1.")
    ];

    private readonly List<DrawingSession> _drawingSessions = [];
    private readonly Dictionary<string, int> _commandUsage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _pendingCommandUsage = new(StringComparer.OrdinalIgnoreCase);
    private string? _commandUsagePath;
    private bool _commandUsageLoaded;
    private DrawingSession _activeSession = new();
    private CadEditor _editor => _activeSession.Editor;
    private readonly ACadSharpCadFileService _cadFiles = new();
    private readonly CadDocumentCoordinator _cadDocuments;
    // Viewport visuals live in CadViewportControl; these aliases keep document/tool logic independent of its XAML tree.
    private Canvas DrawingCanvas => CadViewport.DrawingCanvas;
    private Image GridDotsImage => CadViewport.GridDotsImage;
    private Image DrawingGeometryImage => CadViewport.DrawingGeometryImage;
    private Image SelectionOverlayImage => CadViewport.SelectionOverlayImage;
    private Image DrawingPreviewImage => CadViewport.DrawingPreviewImage;
    private Rectangle SelectionDragOverlay => CadViewport.SelectionDragOverlay;
    private Border DynamicInputPanel => CadViewport.DynamicInputPanel;
    private Border DynamicHintPanel => CadViewport.DynamicHintPanel;
    private TextBlock DynamicPromptText => CadViewport.DynamicPromptText;
    private DockPanel DynamicInputEntryPanel => CadViewport.DynamicInputEntryPanel;
    private TextBox DynamicValueTextBox => CadViewport.DynamicValueTextBox;
    private System.Windows.Shapes.Path OsnapMarker => CadViewport.OsnapMarker;
    private TextBlock OsnapMarkerLabel => CadViewport.OsnapMarkerLabel;
    private CadSpatialIndex _spatialIndex = new(Array.Empty<CadEntity>());
    private CadSpatialIndex? _preparedSpatialIndex;
    private string? _activePath { get => _activeSession.Path; set => _activeSession.Path = value; }
    private Point2 _viewCenter;
    private const double MinViewportScale = 1e-12;
    private const double MaxViewportScale = 1e12;
    private double _viewScale = 1.0;
    private double _fitScale = 1.0;
    private Point _lastPointer;
    private bool _isPanning;
    private bool _viewportClosed;
    private bool _panPreviewActive;
    private readonly HashSet<OsnapMode> _osnapModes = [OsnapMode.Endpoint, OsnapMode.Midpoint, OsnapMode.Center, OsnapMode.Quadrant, OsnapMode.Intersection, OsnapMode.Insertion, OsnapMode.Parallel, OsnapMode.Perpendicular];
    private OsnapResult? _activeOsnap;
    private Point _panAnchor;
    private Point2 _panCenterAtStart;
    private readonly TranslateTransform _panPreviewTransform = new();
    private bool _updatingZoomControls;
    private readonly Stack<(Point2 Center, double Scale)> _previousViews = new();
    private HashSet<Guid> _selectedEntityIds => _activeSession.SelectedEntityIds;
    private enum SelectionAction { None, Erase, Move, Copy, Rotate, Corner, Stretch, Hatch, Callback }
    private SelectionAction _pendingSelectionAction;
    private bool _transformActive;
    private bool _transformCopy;
    private Point2? _transformBasePoint;
    private CadEntity[] _transformEntities = Array.Empty<CadEntity>();
    private readonly List<CadEntity[]> _copyPlacedStack = new(); // one entry per copy placed, for COPY's Undo option
    private CadEntity[] _rotateEntities = Array.Empty<CadEntity>();
    private bool _rotateActive;
    private bool _mirrorPointActive;
    private Point2? _mirrorAxisStart;
    private bool _distancePointActive;
    private Point2? _distanceStart;
    private Point2? _rotateBasePoint;
    private bool _polylineActive;
    /// <summary>POLYGON: pick points freely; the result is a closed region (closed polyline).</summary>
    private bool _polygonRegionMode;
    private readonly List<PolylineVertex> _polylineVertices = new();
    /// <summary>PLINE's Arc option (type A to switch, L to switch back to Line): while true, each new point creates a true
    /// arc segment (Bulge on the previous vertex) tangent to the direction the polyline is currently heading in, instead
    /// of a straight segment - same math AutoCAD uses for its default (non-Angle/Radius/etc.) PLINE arc mode.</summary>
    private bool _polylineArcMode;
    /// <summary>Direction the polyline is "heading" at its last vertex - the tangent used as the start direction of the next
    /// arc segment if Arc mode is on. Updated after every vertex (line or arc). Null until at least one segment exists.</summary>
    private Point2? _polylineTangent;
    /// <summary>REVCLOUD: reuses the same free-click closed-region flow as POLYGON's old tool (_polygonRegionMode), but
    /// FinishPolyline converts the straight-edged boundary the user clicked into a scalloped revision-cloud polyline
    /// (many short, outward-bulging arcs) instead of keeping it as straight edges.</summary>
    private bool _revCloudMode;
    /// <summary>WIPEOUT: reuses the same free-click closed-region flow as POLYGON's old tool/REVCLOUD, but FinishPolyline
    /// saves the boundary as a real native WIPEOUT object (CadWipeoutData) instead of a plain polyline.</summary>
    private bool _wipeoutMode;
    private bool _toolbarPinned = true;
    private bool _rectangleActive;
    private Point2? _rectangleFirstCorner;
    /// <summary>RECTANG Chamfer/Fillet options (AutoCAD keeps these between uses of the command, so they are not reset
    /// in BeginRectangleCommand). Chamfer and Fillet are mutually exclusive, like in AutoCAD: setting one clears the other.</summary>
    private double _rectangleChamfer1, _rectangleChamfer2, _rectangleFilletRadius;
    private string? _rectanglePendingOption;
    private bool _arcActive;
    private Point2? _arcCenter;
    private Point2? _arcStartPoint;
    private string _activeLayout = "Model";
    private bool _emptyViewRecovered;
    // Geometry-build profiling (cache misses only) shown by PERF; budget keeps a cold frame responsive.
    private const double GeometryBuildBudgetMilliseconds = 500;
    private int _lastBuildCalls; private double _lastBuildMilliseconds; private string _slowestBuild = "-"; private int _lastDegradedItems;
    private int _frameProgressPasses; private int _lastDirectFigures, _lastDirectStyles; private static volatile int s_lastRasterMilliseconds; private static double s_rasterMsPerUnit;
    private readonly List<string> _layoutNames = ["Layout1", "Layout2"];
    private bool _propertiesVisible;
    private bool _selectionDragActive;
    private bool _objectDragActive, _objectDragMoved;
    private Point _objectDragStart;
    private bool _selectionDragThresholdMet;
    private bool _selectionDragAdds;
    private Point _selectionDragStart;
    private bool _windowSelectionAwaitingSecondPoint;
    private Point _windowSelectionFirstPoint;
    private bool _dynamicInputEnabled = true;
    private string _lastCommand = string.Empty;
    private bool _controlNameMode;
    private FrameworkElement? _lastNamedControl;
    private bool _maximizedTitleDragPending;
    private Point _maximizedTitleDragStartScreen;
    private Point _maximizedTitleDragStartInWindow;
    private double _maximizedTitleDragStartWidth;
    private readonly CadGeometryCache _geometryCache = new();
    private readonly SemaphoreSlim _cadFileGate = new(1, 1);
    private readonly CancellationTokenSource _snapCandidateLifetime = new();
    private DrawingSession? _pendingSnapCandidateSession;
    private bool _snapCandidatePumpRunning;
    private CancellationTokenSource? _openDrawingCancellation;
    private long _openDrawingGeneration;
    private readonly Stopwatch _startupTimer = Stopwatch.StartNew();
    private double _processStartupMilliseconds;
    private double _windowReadyMilliseconds;
    private double _lastFirstViewportMilliseconds;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _viewportPreviewTimer;
    private readonly DispatcherTimer _wheelDebounceTimer;
    private readonly DispatcherTimer _sliderDebounceTimer;
    private readonly DispatcherTimer _autosaveTimer;
    private readonly DispatcherTimer _spatialIndexDebounceTimer;
    private bool _previewUpdatePending;
    private bool _recoveryPromptShown;
    private bool _pendingWheelZoom;
    private bool _wheelZoomPreviewActive;
    private Matrix _wheelZoomPreviewMatrix = Matrix.Identity;
    private readonly MatrixTransform _wheelZoomPreviewTransform = new();
    private long _drawingRevision;
    private RenderImageKey? _lastRenderImageKey;
    private DrawingImage? _lastRenderImage;
    private int _lastVisibleEntityCount;
    private int _lastGeometryCacheHits;
    private int _lastSkippedTextMTextCount;
    private bool _skipTextMTextForPerfTest;
    private readonly Queue<double> _renderBuildSamples = new();
    private double _lastRenderMilliseconds;
    private double _lastIndexMilliseconds;
    private double _lastBoundsIndexMilliseconds;
    private double _lastSnapCandidateBuildMilliseconds;
    private double _lastOsnapQueryMilliseconds;
    private double _lastOpenMilliseconds;
    private double _lastOpenReadMilliseconds;
    private double _lastOpenMapMilliseconds;
    private double _lastOpenCpuMilliseconds;
    private double _lastOpenMemoryDeltaMb;
    private double _lastPickMilliseconds;
    private double _lastPanUpdateMilliseconds;
    private double _lastSelectionQueryMilliseconds;
    private double _lastSelectionOverlayMilliseconds;
    private double _lastGeometryBuildMilliseconds;
    private double _lastQueryStageMilliseconds, _lastGeometryStageMilliseconds, _lastComposeStageMilliseconds;
    private int _lastFrameItemCount;
    private double _lastBenchmarkQueryMilliseconds;
    private double _lastBenchmarkPickMilliseconds;
    private double _lastBenchmarkOsnapMilliseconds;
    private readonly Queue<double> _pickSamples = new();
    private readonly Queue<double> _osnapSamples = new();
    private readonly Queue<double> _viewportFrameIntervals = new();
    private long _lastInteractionRenderTimestamp;
    private Rect _lastDirtySelectionRegion = Rect.Empty;
    private int _selectionOverlayIncrementalDepth;
    private readonly HashSet<Guid> _renderedSelectionIds = [];
    private SelectionOverlayKey? _lastSelectionOverlayKey;
    private GridImageKey? _lastGridImageKey;
    private DrawingImage? _lastGridImage;
    private bool _autosaveCycleActive;
    private string? _lastAutosaveError;
    private Task<bool>? _autosaveRun;
    private bool _closingAfterAutosave;
    private bool _sliderChangePending;
    private bool _geometryBuildInProgress;
    private bool _renderRequestedDuringBuild;
    private long _geometryBuildGeneration;
    private CancellationTokenSource? _geometryBuildCancellation;
    private RenderImageKey? _geometryBuildKey;
    private DrawingSession? _pendingIndexSession;
    private bool _indexBuildInProgress;
    private const int BackgroundIndexEntityThreshold = 5_000;
    private const int OrderedViewportCandidateLimit = 8_000;
    private const int DenseViewportCandidateLimit = 12_000;
    // Compose sizeable viewports away from the UI thread so zoom input stays responsive.

    private readonly record struct RenderImageKey(long Revision, double CenterX, double CenterY,
        double Scale, double Width, double Height, bool SkipTextMTextForPerfTest);
    private readonly record struct SelectionOverlayKey(long Revision, int SelectionHash, double CenterX, double CenterY,
        double Scale, double Width, double Height);
    private readonly record struct GridImageKey(double CenterX, double CenterY, double Scale, double Width, double Height, string Paper = "");

    public MainWindow()
    {
        InitializeComponent();
        ToolbarPinButton.IsChecked = true; ExpandEdgeToolbar(); // edge toolbar starts pinned open
        Loaded += (_, _) => StartUiWatchdog();
        LoadCustomCommandAliases();
        CadViewport.BackendChanged += (_, _) =>
        {
            _geometryBuildCancellation?.Cancel();
            _lastRenderImage = null; _lastRenderImageKey = null; ClearFrameCache();
            if (IsLoaded && !_viewportClosed) RenderDrawing(fitToDrawing: false);
        };
        DrawingCanvas.MouseMove += DrawingCanvas_MouseMove;
        DrawingCanvas.PreviewMouseLeftButtonDown += DrawingCanvas_PreviewMouseLeftButtonDown;
        DrawingCanvas.MouseLeftButtonDown += DrawingCanvas_MouseLeftButtonDown;
        DrawingCanvas.MouseLeftButtonUp += DrawingCanvas_MouseLeftButtonUp;
        DrawingCanvas.MouseDown += DrawingCanvas_MouseDown;
        DrawingCanvas.MouseUp += DrawingCanvas_MouseUp;
        DrawingCanvas.MouseWheel += DrawingCanvas_MouseWheel;
        DrawingCanvas.KeyDown += DrawingCanvas_KeyDown;
        DynamicInputPanel.PreviewMouseLeftButtonDown += DynamicInputPanel_PreviewMouseLeftButtonDown;
        DynamicValueTextBox.GotFocus += DynamicValueTextBox_GotFocus;
        DynamicValueTextBox.KeyDown += DynamicValueTextBox_KeyDown;
        DynamicValueTextBox.TextChanged += DynamicValueTextBox_TextChanged;
        DynamicValueTextBox.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (DynamicValueTextBox.IsKeyboardFocusWithin || CommandSuggestionsList.IsMouseOver) return;
            _dynamicCommandEntry = false; _dynamicIdleVisible = false;
            DynamicInputPanel.Visibility = Visibility.Collapsed;
            CommandSuggestionPopup.IsOpen = false;
        }));
        PreviewMouseDown += (_, args) =>
        {
            if (!CommandSuggestionPopup.IsOpen || CommandSuggestionsList.IsMouseOver) return;
            var input = _suggestionTarget;
            if (input?.IsMouseOver != true) CommandSuggestionPopup.IsOpen = false;
        };
        _cadDocuments = new CadDocumentCoordinator(_cadFiles);
        Title = $"iStCAD_1 {BuildInfo.Version}";
        AppTitleVersionText.Text = $" {BuildInfo.Version}";
        AppTitle.ToolTip = $"iStCAD_1 build {BuildInfo.Version}";
        _drawingSessions.Add(_activeSession);
        AttachSession(_activeSession);
        CommandInputTextBox.Clear();
        // Coalesce transient cursor/command previews to the render cadence (about 60 Hz).
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            if (_previewUpdatePending && !_isPanning && !_wheelDebounceTimer.IsEnabled) UpdateDrawingPreviewAtPointer();
        };
        // Coalesce wheel-preview transform writes to the render cadence instead of
        // updating three WPF Image elements for every high-resolution wheel event.
        _viewportPreviewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _viewportPreviewTimer.Tick += (_, _) =>
        {
            _viewportPreviewTimer.Stop();
            if (_wheelZoomPreviewActive) UpdateSafeWheelPreview();
        };
        // Wheel events can arrive faster than the viewport can be rebuilt. Keep preview immediate,
        // then render once after the wheel settles instead of repeatedly rebuilding mid-gesture.
        _wheelDebounceTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(110) };
        _wheelDebounceTimer.Tick += (_, _) =>
        {
            _wheelDebounceTimer.Stop();
            CommitPendingWheelZoom();
        };
        _sliderDebounceTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(45) };
        _sliderDebounceTimer.Tick += (_, _) =>
        {
            _sliderDebounceTimer.Stop();
            _sliderChangePending = false;
            RenderDrawing(fitToDrawing: false);
        };
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autosaveTimer.Tick += async (_, _) =>
        {
            _autosaveRun = AutosaveDirtySessionsAsync();
            try { await _autosaveRun; }
            finally { _autosaveRun = null; }
        };
        _autosaveTimer.Start();
        _spatialIndexDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _spatialIndexDebounceTimer.Tick += async (_, _) => { await RebuildPendingSpatialIndexAsync(); };
        DrawingCanvas.SizeChanged += (_, _) => { _lastRenderImageKey = null; _lastSelectionOverlayKey = null; RenderDrawing(fitToDrawing: false); };
        Loaded += (_, _) =>
        {
            UpdateDynamicInputMode();
            ApplyIdleBitmapCache();
            RenderDrawing(fitToDrawing: true);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await OfferRecoveryAsync()));
        };
        ContentRendered += (_, _) =>
        {
            if (_windowReadyMilliseconds > 0) return;
            _startupTimer.Stop();
            _windowReadyMilliseconds = _startupTimer.Elapsed.TotalMilliseconds;
            using (var process = Process.GetCurrentProcess())
            {
                process.Refresh();
                _processStartupMilliseconds = Math.Max(0, (DateTime.Now - process.StartTime).TotalMilliseconds);
            }
            AppendCommandHistory($"Startup: first window frame {_processStartupMilliseconds:0} ms from process start; {_windowReadyMilliseconds:0} ms from window creation.");
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await LoadCommandUsageAsync()));
        };
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _viewportClosed = true;
            CompositionTarget.Rendering -= MainWindow_CompositionTarget_Rendering;
            _snapCandidateLifetime.Cancel();
            CancelGeometryBuild();
            _wheelDebounceTimer.Stop(); _previewTimer.Stop(); _viewportPreviewTimer.Stop();
        };
        CompositionTarget.Rendering += MainWindow_CompositionTarget_Rendering;
        ZoomSlider.ValueChanged += ZoomSlider_ValueChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewTextInput += WindowDynamicTextInput;
        SourceInitialized += (_, _) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(WorkAreaHook);
        UpdateMaximizeButton();
        RefreshDrawingTabs();
        RefreshLayoutTabs();
        RefreshPropertiesPalette();
        RefreshFrequentCommandsToolbar();
        InitializeDraftingCursor();
        RefreshCadStyles();
    }

    private void AttachSession(DrawingSession session)
    {
        session.Editor.DrawingChanged += (_, _) =>
        {
            session.IsDirty = true;
            session.Revision++;
            session.ModelViewportIndex = null;
            if (!ReferenceEquals(session, _activeSession)) return;
            session.GeometryRevision++;
            _drawingRevision++;
            _geometryBuildCancellation?.Cancel();

            _lastRenderImageKey = null;
            _lastSelectionOverlayKey = null;
            var delta = session.Editor.LastEntityDelta;
            var incrementalIndex = delta is { } d && _preparedSpatialIndex is null && !_spatialIndexDebounceTimer.IsEnabled && session.SpatialIndex is { } currentIndex
                ? currentIndex.WithChanges(d.Removed, d.Added) : null;
            // Incremental path: only the changed entities leave/enter the cache and index (no O(n) rebuild, no blank viewport).
            if (incrementalIndex is not null && delta is { } applied) _geometryCache.Invalidate(applied.Removed);
            else PruneGeometryCache(session.Editor.Drawing.Entities);
            if (incrementalIndex is not null)
            {
                _spatialIndex = session.SpatialIndex = incrementalIndex;
            }
            else if (_preparedSpatialIndex is not null)
            {
                _spatialIndex = session.SpatialIndex = _preparedSpatialIndex;
            }
            else if (session.Editor.Drawing.Entities.Count >= BackgroundIndexEntityThreshold)
            {
                session.SpatialIndex = null;
                _spatialIndex = new CadSpatialIndex(Array.Empty<CadEntity>());
                QueueSpatialIndexRebuild(session);
            }
            else
            {
                _spatialIndex = session.SpatialIndex = new CadSpatialIndex(session.Editor.Drawing.Entities, excludedEntityIds: session.Editor.Drawing.HiddenEntityIds);
            }
            _preparedSpatialIndex = null;
            if (ReferenceEquals(session, _activeSession)) TrackSpatialIndexMetrics(session.SpatialIndex);
            RefreshDrawingTabs();
            RefreshPropertiesPalette();
            if (!_spatialIndexDebounceTimer.IsEnabled) RenderDrawing(fitToDrawing: false);
        };
    }

    private void QueueSpatialIndexRebuild(DrawingSession session)
    {
        _pendingIndexSession = session;
        _spatialIndexDebounceTimer.Stop();
        _spatialIndexDebounceTimer.Start();
    }

    private async Task RebuildPendingSpatialIndexAsync()
    {
        _spatialIndexDebounceTimer.Stop();
        if (_indexBuildInProgress || _pendingIndexSession is null) return;
        var session = _pendingIndexSession;
        _pendingIndexSession = null;
        if (!ReferenceEquals(session, _activeSession)) return;
        var sourceRevision = _drawingRevision;
        var entitySnapshot = session.Editor.Drawing.Entities.ToArray();
        var hiddenEntityIds = session.Editor.Drawing.HiddenEntityIds.ToHashSet();
        _indexBuildInProgress = true;
        var indexTimer = Stopwatch.StartNew();
        try
        {
            var index = await Task.Run(() => new CadSpatialIndex(entitySnapshot, excludedEntityIds: hiddenEntityIds, buildSnapCandidates: false));
            indexTimer.Stop();
            _lastIndexMilliseconds = indexTimer.Elapsed.TotalMilliseconds;
            _lastBoundsIndexMilliseconds = index.BoundsIndexDuration.TotalMilliseconds;
            _lastSnapCandidateBuildMilliseconds = index.SnapCandidateBuildDuration.TotalMilliseconds;
            if (ReferenceEquals(session, _activeSession) && sourceRevision == _drawingRevision)
            {
                session.SpatialIndex = _spatialIndex = index;
                _lastRenderImageKey = null;
                RenderDrawing(fitToDrawing: false);
                AppendCommandHistory($"Spatial index ready for {entitySnapshot.Length:N0} entities.");
                QueueSnapCandidateBuild(session);
            }
            else if (ReferenceEquals(session, _activeSession)) QueueSpatialIndexRebuild(session);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(session, _activeSession)) AppendCommandHistory($"Spatial index rebuild failed: {ex.Message}");
        }
        finally
        {
            _indexBuildInProgress = false;
            if (_pendingIndexSession is not null && ReferenceEquals(_pendingIndexSession, _activeSession)) _spatialIndexDebounceTimer.Start();
        }
    }

    private void RefreshDrawingTabs()
    {
        if (DrawingTabsHost is null) return;
        foreach (var old in DrawingTabsHost.Children.OfType<Border>().ToArray()) DrawingTabsHost.Children.Remove(old);
        foreach (var session in _drawingSessions)
        {
            var active = ReferenceEquals(session, _activeSession);
            var tab = new Border
            {
                Background = new SolidColorBrush(active ? Color.FromRgb(78, 85, 95) : Color.FromRgb(49, 55, 64)),
                BorderBrush = new SolidColorBrush(active ? Color.FromRgb(112, 119, 129) : Color.FromRgb(77, 84, 94)),
                BorderThickness = new Thickness(1, 1, 1, 0), CornerRadius = new CornerRadius(4, 4, 0, 0),
                Padding = new Thickness(8, 1, 5, 1), Margin = new Thickness(0, active ? 0 : 1, 3, 0),
                ToolTip = $"{session.DisplayName}{(session.IsDirty ? " — unsaved changes" : string.Empty)}", Tag = session
            };
            AutomationProperties.SetAutomationId(tab, active ? "DrawingTabActive" : "DrawingTab");
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(session.IsDirty ? Color.FromRgb(231, 178, 89) : Color.FromRgb(190, 196, 205)), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = session.DisplayName, FontSize = 11, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brushes.White, MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis });
            var close = new Button { Content = "×", Width = 21, Height = 21, FontSize = 13, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0), Tag = session, ToolTip = "Close drawing tab", Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(198, 208, 211)) };
            close.Click += CloseDrawingTab_Click;
            row.Children.Add(close); tab.Child = row; tab.MouseLeftButtonUp += DrawingTab_Click;
            DrawingTabsHost.Children.Insert(DrawingTabsHost.Children.IndexOf(NewDrawingTabButton), tab);
        }
    }

    private void DrawingTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: DrawingSession session } && !ReferenceEquals(session, _activeSession)) ActivateSession(session, fitToDrawing: true);
    }

    private void ActivateSession(DrawingSession session, bool fitToDrawing, CadSpatialIndex? spatialIndex = null)
    {
        _wheelDebounceTimer.Stop(); _pendingWheelZoom = false;
        ResetWheelZoomPreview();
        CancelGeometryBuild();
        _activeSession = session;
        _geometryCache.Clear(); _lastRenderImage = null; _lastRenderImageKey = null; ClearFrameCache(); _lastSelectionOverlayKey = null;
        _drawingRevision++;
        _preparedSpatialIndex = null;
        _spatialIndexDebounceTimer.Stop(); _pendingIndexSession = null;
        var existingIndex = spatialIndex ?? session.SpatialIndex;
        if (existingIndex is not null) session.SpatialIndex = existingIndex;
        _spatialIndex = existingIndex ?? (session.Editor.Drawing.Entities.Count < BackgroundIndexEntityThreshold
            ? session.SpatialIndex = new CadSpatialIndex(session.Editor.Drawing.Entities, excludedEntityIds: session.Editor.Drawing.HiddenEntityIds)
            : new CadSpatialIndex(Array.Empty<CadEntity>()));
        TrackSpatialIndexMetrics(_spatialIndex);
        _activeLayout = session.Editor.Drawing.ActiveSpaceName;
        _emptyViewRecovered = false;
        _previousViews.Clear();
        _viewCenter = new Point2(0, 0); _viewScale = _fitScale = 1;
        RefreshDrawingTabs(); RefreshLayoutTabs(); RefreshPropertiesPalette();
        RefreshCadStyles();
        if (fitToDrawing) FitView();
        UpdateZoomControls(); RenderDrawing(false);
        if (session.SpatialIndex is null && session.Editor.Drawing.Entities.Count >= BackgroundIndexEntityThreshold) QueueSpatialIndexRebuild(session);
        if (!session.TrackInitialViewport) QueueSnapCandidateBuild(session);
    }

    private void NewDrawingTabButton_Click(object sender, RoutedEventArgs e) => FileNew_Click(sender, e);

    private void DrawingTabMenuButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DrawingTabMenuButton, Placement = PlacementMode.Bottom };
        foreach (var session in _drawingSessions)
        {
            var item = new MenuItem
            {
                Header = $"{session.DisplayName}{(session.IsDirty ? " *" : string.Empty)}",
                IsCheckable = true,
                IsChecked = ReferenceEquals(session, _activeSession),
                Tag = session
            };
            item.Click += (_, _) => ActivateSession(session, fitToDrawing: true);
            menu.Items.Add(item);
        }
        menu.IsOpen = menu.Items.Count > 0;
        e.Handled = true;
    }

    private async void CloseDrawingTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DrawingSession session }) return;
        if (session.IsDirty)
        {
            var answer = MessageBox.Show(this, $"Save changes to {session.DisplayName} before closing?", "Close drawing", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                var path = session.Path ?? PromptSavePath(session);
                if (path is null || !await SaveToAsync(path, session)) return;
            }
            else if (session.RecoveryPath is not null)
            {
                if (session.IsAutosaving)
                {
                    await _cadFileGate.WaitAsync();
                    _cadFileGate.Release();
                }
                try { File.Delete(session.RecoveryPath); session.RecoveryPath = null; } catch { }
            }
        }
        var closedIndex = _drawingSessions.IndexOf(session);
        var wasActive = ReferenceEquals(session, _activeSession);
        _drawingSessions.Remove(session);
        if (_drawingSessions.Count == 0)
        {
            var fresh = new DrawingSession(); _drawingSessions.Add(fresh); AttachSession(fresh);
        }
        if (wasActive) ActivateSession(_drawingSessions[Math.Clamp(closedIndex, 0, _drawingSessions.Count - 1)], true);
        else RefreshDrawingTabs();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            var fromAppTitle = ReferenceEquals(sender, AppTitle);
            if (fromAppTitle) e.Handled = true;
            if (WindowState == WindowState.Maximized)
            {
                _maximizedTitleDragPending = true;
                _maximizedTitleDragStartInWindow = e.GetPosition(this);
                _maximizedTitleDragStartScreen = PointToScreen(_maximizedTitleDragStartInWindow);
                _maximizedTitleDragStartWidth = Math.Max(1, ActualWidth);
                if (sender is UIElement element) Mouse.Capture(element);
                return;
            }
            DragMove();
        }
    }

    private void MainWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_maximizedTitleDragPending && Mouse.LeftButton == MouseButtonState.Pressed)
        {
            var currentScreen = PointToScreen(e.GetPosition(this));
            if (Math.Abs(currentScreen.X - _maximizedTitleDragStartScreen.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(currentScreen.Y - _maximizedTitleDragStartScreen.Y) >= SystemParameters.MinimumVerticalDragDistance)
            {
                _maximizedTitleDragPending = false;
                Mouse.Capture(null);
                var restore = RestoreBounds;
                var restoreWidth = restore.Width > 0 ? restore.Width : Width;
                var horizontalRatio = Math.Clamp(_maximizedTitleDragStartInWindow.X / Math.Max(1, _maximizedTitleDragStartWidth), 0, 1);
                WindowState = WindowState.Normal;
                Left = currentScreen.X - restoreWidth * horizontalRatio;
                Top = currentScreen.Y - _maximizedTitleDragStartInWindow.Y;
                DragMove();
                e.Handled = true;
            }
        }
        if (_controlNameMode && Stopwatch.GetElapsedTime(_lastControlHintTimestamp).TotalMilliseconds >= 100)
        {
            _lastControlHintTimestamp = Stopwatch.GetTimestamp();
            ShowControlName(e.OriginalSource as DependencyObject);
        }
    }

    private void MainWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_maximizedTitleDragPending) return;
        _maximizedTitleDragPending = false;
        Mouse.Capture(null);
    }

    private void MainWindow_MouseLeave(object sender, MouseEventArgs e) { ControlNamePopup.IsOpen = false; _lastNamedControl = null; }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text == "AI Assistant...") SearchBox.Clear();
        SearchBox.Foreground = Brushes.White;
    }

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            SearchBox.Text = "AI Assistant...";
            SearchBox.Foreground = new SolidColorBrush(Color.FromRgb(194, 198, 206));
        }
        else SearchBox.Foreground = Brushes.White;
    }

    private long _lastControlHintTimestamp;

    private void ShowControlName(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, this))
        {
            if (source is FrameworkElement element)
            {
                var name = AutomationProperties.GetAutomationId(element);
                if (string.IsNullOrWhiteSpace(name)) name = element.Name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (!ReferenceEquals(_lastNamedControl, element) || !string.Equals(ControlNamePopupText.Text, name, StringComparison.Ordinal))
                    {
                        ControlNamePopupText.Text = name;
                        _lastNamedControl = element;
                    }
                    ControlNamePopup.IsOpen = true;
                    return;
                }
            }
            source = VisualTreeHelper.GetParent(source) ?? LogicalTreeHelper.GetParent(source);
        }
        _lastNamedControl = null;
        ControlNamePopup.IsOpen = false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButton();
    }

    private void UpdateMaximizeButton()
    {
        if (MaximizeButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private double _lastPropertiesWidth = 296;

    private void TogglePropertiesPalette()
    {
        _propertiesVisible = !_propertiesVisible;
        PropertiesPanel.Visibility = _propertiesVisible ? Visibility.Visible : Visibility.Collapsed;
        PropertiesResizeSplitter.Visibility = _propertiesVisible ? Visibility.Visible : Visibility.Collapsed;
        PropertiesSplitterColumn.Width = _propertiesVisible ? new GridLength(6) : new GridLength(0);
        // Preserve the last width set by dragging the splitter when the palette is toggled.
        if (_propertiesVisible)
            PropertiesColumn.Width = new GridLength(Math.Clamp(_lastPropertiesWidth, 230, Math.Max(230, DrawingWorkspace.ActualWidth - 240)));
        else
        {
            if (PropertiesColumn.ActualWidth >= 230) _lastPropertiesWidth = PropertiesColumn.ActualWidth;
            PropertiesColumn.Width = new GridLength(0);
        }
        AppendCommandHistory($"Properties palette: {(_propertiesVisible ? "shown" : "hidden")}.");
        if (_propertiesVisible) RefreshPropertiesPalette();
        RenderDrawing(false);
    }

    private void AutoHidePanel_MouseEnter(object sender, MouseEventArgs e) => ExpandEdgeToolbar();

    private void AutoHidePanel_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_toolbarPinned) return;
        CollapseEdgeToolbar();
    }

    private void ToolbarPin_Click(object sender, RoutedEventArgs e)
    {
        _toolbarPinned = ToolbarPinButton.IsChecked == true;
        if (_toolbarPinned) ExpandEdgeToolbar();
        else if (!AutoHidePanel.IsMouseOver) CollapseEdgeToolbar();
        AppendCommandHistory($"Edge toolbar {(_toolbarPinned ? "pinned" : "auto-hide") }.");
    }

    private void ExpandEdgeToolbar()
    {
        AutoHidePanel.Width = 66;
        AutoHideHandle.Visibility = Visibility.Collapsed;
        AutoHideButtons.Visibility = Visibility.Visible;
    }

    private void CollapseEdgeToolbar()
    {
        AutoHideButtons.Visibility = Visibility.Collapsed;
        AutoHideHandle.Visibility = Visibility.Visible;
        AutoHidePanel.Width = 12;
    }

    private void CommandExpand_Click(object sender, RoutedEventArgs e)
    {
        var expanded = CommandExpandedPanel.Visibility != Visibility.Visible;
        SetCommandHistoryExpanded(expanded);
    }

    private void SetCommandHistoryExpanded(bool expanded)
    {
        CommandExpandedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CommandExpandButton.Content = expanded ? "⌄" : "⌃";
        CommandExpandButton.ToolTip = expanded ? "Collapse command history" : "Expand command history upward";
        if (expanded) CommandHistoryTextBox.ScrollToEnd();
    }

    private void CommandInputTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        _dynamicIdleVisible = false;
        _dynamicCommandEntry = false;
        UpdateDynamicInputMode();
        SetCommandDockExpanded(true);
    }

    private void CommandInputTextBox_LostFocus(object sender, RoutedEventArgs e) => SetCommandDockExpanded(false);

    private void SetCommandDockExpanded(bool expanded)
    {
        if (CommandDockRow is null || CommandTabsRowDefinition is null || StatusToolsRowDefinition is null) return;
        CommandDockRow.Height = new GridLength(86);
        CommandTabsRowDefinition.Height = new GridLength(30);
        StatusToolsRowDefinition.Height = new GridLength(30);
        LayoutTabsHost.Visibility = Visibility.Visible;
        if (!expanded) SetCommandHistoryExpanded(false);
    }

    private void FileNew_Click(object sender, RoutedEventArgs e)
    {
        var session = new DrawingSession();
        _drawingSessions.Add(session); AttachSession(session); ActivateSession(session, fitToDrawing: true);
        AppendCommandHistory("Created a new drawing tab.");
    }

    private async void FileOpen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|DWG (*.dwg)|*.dwg|DXF (*.dxf)|*.dxf",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!ConfirmOpenMemory(dialog.FileName)) return;
        _openDrawingCancellation?.Cancel();
        var openCancellation = new CancellationTokenSource();
        _openDrawingCancellation = openCancellation;
        var generation = ++_openDrawingGeneration;
        AppendCommandHistory($"Opening {IOPath.GetFileName(dialog.FileName)}...");
        Mouse.OverrideCursor = Cursors.Wait;
        var openTimer = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var memoryBefore = process.WorkingSet64;
        try
        {
            var prepared = await RunWithLowLatencyGc(() => _cadDocuments.OpenAsync(dialog.FileName, openCancellation.Token), dialog.FileName);
            openCancellation.Token.ThrowIfCancellationRequested();
            var drawing = prepared.Drawing;
            var preparedIndex = prepared.SpatialIndex;
            _lastIndexMilliseconds = prepared.IndexDuration.TotalMilliseconds;
            _lastBoundsIndexMilliseconds = preparedIndex.BoundsIndexDuration.TotalMilliseconds;
            _lastSnapCandidateBuildMilliseconds = preparedIndex.SnapCandidateBuildDuration.TotalMilliseconds;
            openTimer.Stop(); process.Refresh();
            _lastOpenMilliseconds = openTimer.Elapsed.TotalMilliseconds;
            _lastOpenCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            _lastOpenMemoryDeltaMb = (process.WorkingSet64 - memoryBefore) / 1024d / 1024d;
            _lastOpenReadMilliseconds = drawing.OpenMetrics?.ReadDuration.TotalMilliseconds ?? 0;
            _lastOpenMapMilliseconds = drawing.OpenMetrics?.MappingDuration.TotalMilliseconds ?? 0;
            var session = new DrawingSession(drawing, dialog.FileName);
            session.TrackInitialViewport = true;
            session.InitialViewportStartTimestamp = Stopwatch.GetTimestamp();
            _drawingSessions.Add(session); AttachSession(session); ActivateSession(session, fitToDrawing: true, spatialIndex: preparedIndex);
            CommandInputTextBox.Clear();
            CommandInputTextBox.ToolTip = drawing.UnsupportedEntityCount == 0
                ? $"Opened {IOPath.GetFileName(dialog.FileName)}"
                : $"Opened {IOPath.GetFileName(dialog.FileName)}; {drawing.UnsupportedEntityCount} native entities are not editable yet";
            ScheduleMemoryCleanup();
            s_lastRasterMilliseconds = 0; s_rasterMsPerUnit = 0; // new drawing: forget the previous drawing's speed measurements
            AppendCommandHistory($"Read {drawing.DocumentEntityCount} native entities across model space, layouts and block definitions.");
            if (drawing.OpenMetrics is { } openMetrics)
                AppendCommandHistory($"Mapped {openMetrics.MappedEntityCount:N0} top-level space entities; nested block children remain native definitions and are not added as editable drawing objects.");
            AppendCommandHistory($"Loaded spaces: {string.Join(", ", drawing.SpaceNames.Select(space => $"{space} ({drawing.GetSpaceEntityCount(space):N0})"))}.");
            try
            {
                var extentsText = _spatialIndex.TryGetExtents(out var ix) ? $"index extents ({ix.MinX:0.##}, {ix.MinY:0.##}) - ({ix.MaxX:0.##}, {ix.MaxY:0.##})" : "index has no extents";
                AppendCommandHistory($"View check: active space '{drawing.ActiveSpaceName}' has {drawing.Entities.Count:N0} entities ({drawing.HiddenEntityIds.Count:N0} hidden); view center ({_viewCenter.X:0.##}, {_viewCenter.Y:0.##}) scale {_viewScale:0.####}; canvas {DrawingCanvas.ActualWidth:0}x{DrawingCanvas.ActualHeight:0}; {extentsText}.");
            }
            catch (Exception viewEx) { AppendCommandHistory("View check failed: " + viewEx.Message); }
            if (drawing.DatabaseSummary is { } database)
            {
                AppendCommandHistory($"DWG tables: {database.LayerCount:N0} layers, {database.BlockRecordCount:N0} block records, {database.LayoutCount:N0} layouts, {database.LineTypeCount:N0} linetypes, {database.TextStyleCount:N0} text styles, {database.DimensionStyleCount:N0} dimension styles, {database.UcsCount:N0} UCS, {database.ViewCount:N0} views, {database.ViewportCount:N0} viewports, {database.RegisteredApplicationCount:N0} registered apps.");
                AppendCommandHistory($"DWG data: {database.ReachableObjectCount:N0} entities across block records; deep XData/Reactor scan skipped during open for responsiveness.");
            }
            AppendCommandHistory($"Open/index: {_lastOpenMilliseconds:0} ms wall, {_lastOpenCpuMilliseconds:0} ms CPU; RAM change {_lastOpenMemoryDeltaMb:+0.0;-0.0;0.0} MB, total {process.WorkingSet64 / 1024d / 1024d:0} MB.");
            AppendCommandHistory($"Open stages: read {_lastOpenReadMilliseconds:0} ms; map/database inventory {_lastOpenMapMilliseconds:0} ms; bounds/R-tree {_lastBoundsIndexMilliseconds:0} ms; OSNAP candidates {_lastSnapCandidateBuildMilliseconds:0} ms.");
            var proxyPreviewCount = drawing.Entities.OfType<ProxyCadEntity>().Count(proxy => proxy.HasBounds);
            AppendCommandHistory($"Model space: {drawing.NativeEntityCount} objects; {drawing.UnsupportedEntityCount} retained as native, read-only objects; {proxyPreviewCount} have geometry or bounding-box previews.");
            var typeSummary = string.Join(", ", drawing.NativeEntityTypes.OrderByDescending(pair => pair.Value).Take(12).Select(pair => $"{pair.Key}: {pair.Value:N0}"));
            if (!string.IsNullOrEmpty(typeSummary)) AppendCommandHistory($"Model Space entity types: {typeSummary}");
            var modelLayerSummary = string.Join(", ", drawing.NativeModelSpaceLayerCounts.OrderByDescending(pair => pair.Value).Take(10)
                .Select(pair => $"{pair.Key}: {pair.Value:N0}"));
            if (!string.IsNullOrEmpty(modelLayerSummary)) AppendCommandHistory($"Model Space layers (top 10): {modelLayerSummary}");
            foreach (var spaceTypeCounts in drawing.NativeEntityTypesBySpace.Where(pair => !pair.Key.Equals("Model", StringComparison.OrdinalIgnoreCase)))
            {
                var layoutTypes = string.Join(", ", spaceTypeCounts.Value.OrderByDescending(pair => pair.Value).Take(5)
                    .Select(pair => $"{pair.Key}: {pair.Value:N0}"));
                AppendCommandHistory($"{spaceTypeCounts.Key} entity types: {layoutTypes}");
            }
            var directCircles = drawing.Entities.OfType<CircleEntity>().Count();
            var directArcs = drawing.Entities.OfType<ArcEntity>().Count();
            var proxyTypeSummary = string.Join(", ", drawing.Entities.OfType<ProxyCadEntity>()
                .GroupBy(proxy => proxy.NativeType, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).Take(5)
                .Select(group => $"{group.Key}: {group.Count()}"));
            AppendCommandHistory($"Viewport geometry sources: {directCircles:N0} native CIRCLE, {directArcs:N0} native ARC, {drawing.Entities.OfType<ProxyCadEntity>().Count():N0} proxy objects.");
            if (!string.IsNullOrEmpty(proxyTypeSummary)) AppendCommandHistory($"Proxy object types: {proxyTypeSummary}");
            var readerNotes = drawing.ReadDiagnostics.OrderByDescending(d => d.Contains("Could not", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var diagnostic in readerNotes.Take(8)) AppendCommandHistory($"DWG reader: {diagnostic}");
            if (readerNotes.Count > 8)
            {
                var notesPath = "";
                try
                {
                    var dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1");
                    Directory.CreateDirectory(dir);
                    notesPath = IOPath.Combine(dir, "reader-notes.log");
                    File.WriteAllLines(notesPath, new[] { $"{DateTime.Now:O} {drawing.Name}" }.Concat(readerNotes));
                }
                catch { notesPath = ""; }
                AppendCommandHistory($"DWG reader: {readerNotes.Count - 8} more notifications" + (notesPath.Length > 0 ? $" saved to {notesPath}" : " omitted from the command history") + ".");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _openDrawingGeneration) ShowCadError("Could not open this drawing", ex); }
        finally
        {
            if (generation == _openDrawingGeneration)
            {
                Mouse.OverrideCursor = null;
                if (ReferenceEquals(_openDrawingCancellation, openCancellation)) _openDrawingCancellation = null;
            }
            openCancellation.Dispose();
        }
    }

    private async void FileSave_Click(object sender, RoutedEventArgs e)
    {
        if (_activePath is null) { SaveAs(); return; }
        await SaveToAsync(_activePath, _activeSession);
    }

    private void FileSaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void RefreshLayoutTabs()
    {
        if (LayoutTabsHost is null) return;
        foreach (var old in LayoutTabsHost.Children.OfType<Border>().Where(b => b != ModelTab).ToArray()) LayoutTabsHost.Children.Remove(old);
        var names = _cadFiles.GetLayouts(_editor.Drawing).Where(name => !name.Equals("Model", StringComparison.OrdinalIgnoreCase)).ToList();
        if (names.Count == 0) names = _layoutNames.ToList();
        _layoutNames.Clear(); _layoutNames.AddRange(names);
        foreach (var name in _layoutNames)
        {
            var selected = string.Equals(_activeLayout, name, StringComparison.OrdinalIgnoreCase);
            var tab = new Border
            {
                Background = new SolidColorBrush(selected ? Color.FromRgb(86, 93, 103) : Color.FromRgb(65, 71, 80)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(93, 100, 110)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 5, 0),
                ToolTip = $"Paper layout: {name}", Tag = name
            };
            tab.Child = new TextBlock { Text = name, FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            tab.MouseLeftButtonUp += LayoutTab_Click;
            LayoutTabsHost.Children.Insert(LayoutTabsHost.Children.IndexOf(NewLayoutButton), tab);
        }
        ModelTab.Background = new SolidColorBrush(string.Equals(_activeLayout, "Model", StringComparison.OrdinalIgnoreCase) ? Color.FromRgb(86, 93, 103) : Color.FromRgb(65, 71, 80));
    }

    private void ModelTab_Click(object sender, MouseButtonEventArgs e)
    {
        SwitchActiveSpace("Model");
    }

    private void LayoutTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string name })
        {
            SwitchActiveSpace(name);
        }
    }

    private void SwitchActiveSpace(string name)
    {
        if (!_editor.Drawing.ActivateSpace(name)) return;
        _activeLayout = _editor.Drawing.ActiveSpaceName;
        _selectedEntityIds.Clear();
        _geometryCache.Clear(); _lastRenderImage = null; _lastRenderImageKey = null; ClearFrameCache(); _lastSelectionOverlayKey = null;
        _drawingRevision++; _activeSession.GeometryRevision++;
        _spatialIndexDebounceTimer.Stop(); _pendingIndexSession = null;
        _activeSession.SpatialIndex = null;
        var entities = _editor.Drawing.Entities;
        _spatialIndex = entities.Count < BackgroundIndexEntityThreshold
            ? _activeSession.SpatialIndex = new CadSpatialIndex(entities, excludedEntityIds: _editor.Drawing.HiddenEntityIds)
            : new CadSpatialIndex(Array.Empty<CadEntity>());
        TrackSpatialIndexMetrics(_spatialIndex);
        _previousViews.Clear();
        RefreshLayoutTabs(); RefreshPropertiesPalette();
        FitView(); UpdateZoomControls(); RenderDrawing(false);
        if (_activeSession.SpatialIndex is null) QueueSpatialIndexRebuild(_activeSession);
        QueueSnapCandidateBuild(_activeSession);
        AppendCommandHistory($"Current space: {_activeLayout}; {entities.Count:N0} objects loaded.");
    }

    private void NewLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = _cadFiles.CreateLayout(_editor.Drawing);
            _activeSession.IsDirty = true;
            _activeSession.Revision++;
            SwitchActiveSpace(name);
            RefreshDrawingTabs();
            AppendCommandHistory($"Created paper layout {name}. Set its page and plot style with PLOT.");
        }
        catch (Exception ex) { ShowCadError("Could not create layout", ex); }
    }

    private void PlotPdf_Click(object sender, RoutedEventArgs e) => ShowPlotDialog();

    private bool _foreignDwgHinted;

    private void SaveAs()
    {
        var path = PromptSavePath(_activeSession);
        if (path is not null) _ = SaveToAsync(path, _activeSession);
    }

    private string? PromptSavePath(DrawingSession session)
    {
        var dialog = new SaveFileDialog { FileName = session.Editor.Drawing.Name, DefaultExt = ".dwg", Filter = "DWG drawing (*.dwg)|*.dwg|DXF drawing (*.dxf)|*.dxf", AddExtension = true };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async Task<bool> SaveToAsync(string path, DrawingSession session)
    {
        var drawing = session.Editor.Drawing;
        var saveRevision = session.Revision;
        var snapshot = drawing.Snapshot();
        try
        {
            if (ReferenceEquals(session, _activeSession)) AppendCommandHistory($"Saving {IOPath.GetFileName(path)}...");
            if (ReferenceEquals(session, _activeSession)) Mouse.OverrideCursor = Cursors.Wait;
            await _cadFileGate.WaitAsync();
            try { await Task.Run(() => _cadFiles.Save(snapshot, path)); }
            finally { _cadFileGate.Release(); }
            session.Editor.Drawing.NativeContext = snapshot.NativeContext;
            foreach (var note in _cadFiles.LastSaveNotes) if (ReferenceEquals(session, _activeSession)) AppendCommandHistory(note);
            session.Path = path; session.IsDirty = session.Revision != saveRevision;
            if (session.RecoveryPath is not null && !IOPath.GetFullPath(session.RecoveryPath).Equals(IOPath.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(session.RecoveryPath); session.RecoveryPath = null; } catch { }
            }
            if (ReferenceEquals(session, _activeSession))
            {
                CommandInputTextBox.Clear(); CommandInputTextBox.ToolTip = $"Saved {IOPath.GetFileName(path)}";
                AppendCommandHistory($"Saved {IOPath.GetFileName(path)}.");
                if (path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase) && !_foreignDwgHinted)
                {
                    _foreignDwgHinted = true;
                    AppendCommandHistory("Tip: AutoCAD may show \"Foreign DWG File\" for DWG saved here. Type DWGCHECK for how to stop it (or save as DXF).");
                }
            }
            RefreshDrawingTabs();
            return true;
        }
        catch (Exception ex) { ShowCadError("Could not save this drawing", ex); return false; }
        finally { if (ReferenceEquals(session, _activeSession)) Mouse.OverrideCursor = null; }
    }

    private async Task<bool> AutosaveDirtySessionsAsync()
    {
        if (_autosaveCycleActive) return false;
        _autosaveCycleActive = true;
        var allSaved = true;
        try
        {
            foreach (var session in _drawingSessions.Where(session => session.IsDirty && !session.IsAutosaving).ToArray())
            {
                session.IsAutosaving = true;
                try
                {
                    var recoveryPath = session.RecoveryPath ??= GetRecoveryPath(session);
                    var snapshot = session.Editor.Drawing.Snapshot();
                    await _cadFileGate.WaitAsync();
                    try { await Task.Run(() => _cadFiles.Save(snapshot, recoveryPath)); }
                    finally { _cadFileGate.Release(); }
                    session.Editor.Drawing.NativeContext = snapshot.NativeContext;
                    if (ReferenceEquals(session, _activeSession)) AppendCommandHistory($"Recovery autosave written for {session.DisplayName}.");
                }
                catch (Exception ex)
                {
                    allSaved = false;
                    _lastAutosaveError = $"{session.DisplayName}: {ex.GetType().Name}: {ex.Message}";
                    try
                    {
                        var logDirectory = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1");
                        Directory.CreateDirectory(logDirectory);
                        File.AppendAllText(IOPath.Combine(logDirectory, "autosave-errors.log"), $"{DateTime.Now:O} {session.DisplayName} -> {session.RecoveryPath}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
                    }
                    catch { }
                    AppendCommandHistory($"Autosave failed for {session.DisplayName}: {ex.Message}");
                }
                finally { session.IsAutosaving = false; }
            }
        }
        finally { _autosaveCycleActive = false; }
        return allSaved;
    }

    private static string GetRecoveryPath(DrawingSession session)
    {
        var directory = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "Recovery");
        Directory.CreateDirectory(directory);
        var safeName = string.Concat(session.DisplayName.Select(character => IOPath.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        return IOPath.Combine(directory, $"{Guid.NewGuid():N}_{safeName}.recovery.dwg");
    }

    private async Task OfferRecoveryAsync()
    {
        if (_recoveryPromptShown) return;
        _recoveryPromptShown = true;
        var directory = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "Recovery");
        if (!Directory.Exists(directory)) return;
        var paths = Directory.GetFiles(directory, "*.recovery.dwg");
        if (paths.Length == 0) return;
        // No startup popup: just note it on the command line (the autosave files stay in the Recovery folder).
        AppendCommandHistory($"{paths.Length} autosaved drawing(s) found in {directory}.");
        if (paths.Length >= 0) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            DrawingSession? lastRecovered = null;
            foreach (var path in paths)
            {
                try
                {
                    if (!ConfirmOpenMemory(path)) continue;
                    var prepared = await RunWithLowLatencyGc(() => _cadDocuments.OpenAsync(path), path);
                    var drawing = prepared.Drawing;
                    var spatialIndex = prepared.SpatialIndex;
                    var session = new DrawingSession(drawing) { RecoveryPath = path, IsDirty = true, TrackInitialViewport = true, InitialViewportStartTimestamp = Stopwatch.GetTimestamp() };
                    _drawingSessions.Add(session); AttachSession(session); lastRecovered = session;
                    if (ReferenceEquals(lastRecovered, session)) _preparedSpatialIndex = spatialIndex;
                    AppendCommandHistory($"Recovered {drawing.Name}: {drawing.NativeEntityCount} model-space entities.");
                }
                catch (Exception ex) { AppendCommandHistory($"Could not recover {IOPath.GetFileName(path)}: {ex.Message}"); }
            }
            if (lastRecovered is not null) ActivateSession(lastRecovered, fitToDrawing: true, spatialIndex: _preparedSpatialIndex);
        }
        finally { _preparedSpatialIndex = null; Mouse.OverrideCursor = null; }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _openDrawingCancellation?.Cancel();
        _autosaveTimer.Stop(); _previewTimer.Stop(); _viewportPreviewTimer.Stop(); _wheelDebounceTimer.Stop(); _sliderDebounceTimer.Stop(); _spatialIndexDebounceTimer.Stop();
        if (_closingAfterAutosave || !_drawingSessions.Any(session => session.IsDirty)) return;
        e.Cancel = true;
        var recoverySucceeded = _autosaveRun is { } activeAutosave
            ? await activeAutosave
            : await AutosaveDirtySessionsAsync();
        if (!recoverySucceeded)
        {
            _autosaveTimer.Start();
            if (_pendingIndexSession is not null) _spatialIndexDebounceTimer.Start();
            var closeAnyway = MessageBox.Show(this,
                $"At least one recovery file could not be written.{Environment.NewLine}{Environment.NewLine}Reason: {_lastAutosaveError ?? "unknown"}{Environment.NewLine}(details: %LOCALAPPDATA%\\iStCAD_1\\autosave-errors.log){Environment.NewLine}{Environment.NewLine}Close anyway and discard unsaved changes? Choose No to stay open and save manually.",
                "Autosave failed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (closeAnyway == MessageBoxResult.Yes) { _closingAfterAutosave = true; Close(); }
            return;
        }
        _closingAfterAutosave = true;
        Close();
    }

    private void ShowPerformanceMetrics()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuPercent = process.TotalProcessorTime.TotalMilliseconds /
            Math.Max(1, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds * Environment.ProcessorCount) * 100;
        var pickP95 = _pickSamples.Count == 0 ? 0 : _pickSamples.OrderBy(value => value).ElementAt((int)Math.Ceiling(_pickSamples.Count * .95) - 1);
        var osnapP95 = _osnapSamples.Count == 0 ? 0 : _osnapSamples.OrderBy(value => value).ElementAt((int)Math.Ceiling(_osnapSamples.Count * .95) - 1);
        var frameP95 = _viewportFrameIntervals.Count == 0 ? 0 : _viewportFrameIntervals.OrderBy(value => value).ElementAt((int)Math.Ceiling(_viewportFrameIntervals.Count * .95) - 1);
        var dirtyPixels = _lastDirtySelectionRegion.IsEmpty ? 0 : Math.Max(0, _lastDirtySelectionRegion.Width * _lastDirtySelectionRegion.Height);
        var estimatedFrameRate = frameP95 > 0 ? 1000 / frameP95 : 0;
        var buildP95 = _renderBuildSamples.Count == 0 ? 0 : _renderBuildSamples.OrderBy(value => value).ElementAt((int)Math.Ceiling(_renderBuildSamples.Count * .95) - 1);
        var buildAverage = _renderBuildSamples.Count == 0 ? 0 : _renderBuildSamples.Average();
        AppendCommandHistory($"PERF-BUILD — geometry cache misses {_lastBuildCalls:N0} built in {_lastBuildMilliseconds:0} ms; slowest {_slowestBuild}; placeholders in last frame {_lastDegradedItems:N0}; progressive passes {_frameProgressPasses}; direct-stream figures {_lastDirectFigures:N0} in {_lastDirectStyles} style(s); pixel lines via {(s_skiaUnavailable ? "CPU Bresenham (Skia unavailable)" : "Skia CPU")}, {GpuStatusText()} ({s_lastSkiaSegments:N0} segments last frame); render tier {RenderTierName(s_lastFrameTier)}{(s_renderModeSetting == 0 ? " (auto)" : "")}; last raster {s_lastRasterMilliseconds} ms{(s_lastDegradedFills > 0 ? $" [draft: {s_lastDegradedFills:N0} text/hatch drawn as boxes]" : "")} (learned {s_rasterMsPerUnit:0.00} ms/unit); UI stalls {_uiStallCount} (worst {_uiWorstStallMs:0} ms); GC pause total {GC.GetTotalPauseDuration().TotalMilliseconds:0} ms (GCs gen0/1/2: {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}).");
        AppendCommandHistory($"PERF — process-to-window first frame {_processStartupMilliseconds:0} ms; window creation-to-frame {_windowReadyMilliseconds:0} ms; drawing first viewport {_lastFirstViewportMilliseconds:0} ms; entities: {_editor.Drawing.Entities.Count:N0}; indexed: {_spatialIndex.IndexedEntityCount:N0}; visible: {_lastVisibleEntityCount:N0}; geometry cache: {_geometryCache.Count:N0} entries / {_geometryCache.EstimatedUnits:N0} estimated units (hits {_lastGeometryCacheHits:N0}); LOD {CurrentGeometryLod}; viewport render {_lastRenderMilliseconds:0.00} ms [last frame: query {_lastQueryStageMilliseconds:0} ms, geometry {_lastGeometryStageMilliseconds:0} ms, compose+raster {_lastComposeStageMilliseconds:0} ms, {_lastFrameItemCount:N0} items]; geometry build {_lastGeometryBuildMilliseconds:0.00} ms (sample avg/build p95 {buildAverage:0.00}/{buildP95:0.00} ms, n={_renderBuildSamples.Count}); TEXT/MTEXT render skip {(_skipTextMTextForPerfTest ? "ON" : "OFF")} (last frame skipped {_lastSkippedTextMTextCount:N0}); interaction frame p95 {frameP95:0.00} ms (~{estimatedFrameRate:0} FPS); pan input {_lastPanUpdateMilliseconds:0.000} ms; selection query {_lastSelectionQueryMilliseconds:0.000} ms; selection overlay {_lastSelectionOverlayMilliseconds:0.00} ms; pick {_lastPickMilliseconds:0.000} ms (p95 {pickP95:0.000}); OSNAP query {_lastOsnapQueryMilliseconds:0.000} ms (p95 {osnapP95:0.000}); dirty selection region {dirtyPixels:N0} px; open read {_lastOpenReadMilliseconds:0} ms, map {_lastOpenMapMilliseconds:0} ms, bounds/R-tree {_lastBoundsIndexMilliseconds:0} ms, OSNAP ready {_spatialIndex.SnapCandidatesReady}, build {_lastSnapCandidateBuildMilliseconds:0} ms, total open {_lastOpenMilliseconds:0} ms wall / {_lastOpenCpuMilliseconds:0} ms CPU; open RAM Δ {_lastOpenMemoryDeltaMb:+0.0;-0.0;0.0} MB; RAM {process.WorkingSet64 / 1024d / 1024d:0.0} MB; CPU avg {cpuPercent:0.0}%.");
    }

    private void TrackSpatialIndexMetrics(CadSpatialIndex? index)
    {
        _lastBoundsIndexMilliseconds = index?.BoundsIndexDuration.TotalMilliseconds ?? 0;
        _lastSnapCandidateBuildMilliseconds = index?.SnapCandidateBuildDuration.TotalMilliseconds ?? 0;
        _lastIndexMilliseconds = _lastBoundsIndexMilliseconds + _lastSnapCandidateBuildMilliseconds;
    }

    private void QueueSnapCandidateBuild(DrawingSession session)
    {
        // Idle pointer movement/navigation never needs OSNAP data. Build only when the
        // user starts a CAD command that can consume snaps; otherwise large drawings would
        // keep a background worker walking every entity immediately after opening.
        if (!IsDynamicCommandActive) return;
        if (session.TrackInitialViewport)
        {
            _pendingSnapCandidateSession = session;
            return;
        }
        if (session.SpatialIndex is not { SnapCandidatesReady: false }) return;
        _pendingSnapCandidateSession = session;
        if (_snapCandidatePumpRunning || _snapCandidateLifetime.IsCancellationRequested) return;
        _ = RunPendingSnapCandidateBuildsAsync();
    }

    private async Task RunPendingSnapCandidateBuildsAsync()
    {
        _snapCandidatePumpRunning = true;
        try
        {
            while (_pendingSnapCandidateSession is { } session && !_snapCandidateLifetime.IsCancellationRequested)
            {
                _pendingSnapCandidateSession = null;
                var index = session.SpatialIndex;
                if (index is null || index.SnapCandidatesReady || !ReferenceEquals(session, _activeSession)) continue;
                AppendCommandHistory($"Preparing OSNAP points for {session.DisplayName}...");
                try
                {
                    await Task.Run(() => index.BuildSnapCandidates(_snapCandidateLifetime.Token), _snapCandidateLifetime.Token);
                    if (ReferenceEquals(session, _activeSession) && ReferenceEquals(session.SpatialIndex, index))
                    {
                        TrackSpatialIndexMetrics(index);
                        AppendCommandHistory($"OSNAP points ready in {index.SnapCandidateBuildDuration.TotalMilliseconds:0} ms.");
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (ReferenceEquals(session, _activeSession)) AppendCommandHistory($"OSNAP index build failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _snapCandidatePumpRunning = false;
            if (_pendingSnapCandidateSession is { } pending && !_snapCandidateLifetime.IsCancellationRequested)
                QueueSnapCandidateBuild(pending);
        }
    }

    private void MainWindow_CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        UpdateDraftingCursor();
        if (_isPanning)
        {
            var panUpdateStart = Stopwatch.GetTimestamp();
            _panPreviewTransform.X = _lastPointer.X - _panAnchor.X;
            _panPreviewTransform.Y = _lastPointer.Y - _panAnchor.Y;
            // The grid is cheap: redraw it at its true position every frame instead of bitmap-shifting it (no flash on commit).
            RenderDrawingGrid(force: true, new Point2(_panCenterAtStart.X - _panPreviewTransform.X / _viewScale, _panCenterAtStart.Y + _panPreviewTransform.Y / _viewScale));
            _lastPanUpdateMilliseconds = Stopwatch.GetElapsedTime(panUpdateStart).TotalMilliseconds;
        }
        if (!_isPanning && !_panPreviewActive && !_wheelZoomPreviewActive && !_selectionDragActive && !_windowSelectionAwaitingSecondPoint && !IsDynamicCommandActive)
        { _lastInteractionRenderTimestamp = 0; return; }
        var now = Stopwatch.GetTimestamp();
        if (_lastInteractionRenderTimestamp > 0)
        {
            _viewportFrameIntervals.Enqueue(Stopwatch.GetElapsedTime(_lastInteractionRenderTimestamp, now).TotalMilliseconds);
            while (_viewportFrameIntervals.Count > 120) _viewportFrameIntervals.Dequeue();
        }
        _lastInteractionRenderTimestamp = now;
    }

    private void RunDrawingBenchmark()
    {
        if (_editor.Drawing.Entities.Count == 0)
        {
            AppendCommandHistory("BENCH: open a drawing before running the viewport benchmark.");
            return;
        }
        if (!_spatialIndex.SnapCandidatesReady)
        {
            AppendCommandHistory("BENCH: wait for OSNAP candidate preparation to finish, then run BENCH again.");
            return;
        }

        var boundsList = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        foreach (var entity in _editor.Drawing.Entities)
            if (_spatialIndex.TryGetBounds(entity.Id, out var entityBounds)) boundsList.Add(entityBounds);
        var bounds = boundsList.ToArray();
        if (bounds.Length == 0)
        {
            AppendCommandHistory("BENCH: the drawing has no spatially indexed entities.");
            return;
        }

        var minX = bounds.Min(b => b.MinX); var minY = bounds.Min(b => b.MinY);
        var maxX = bounds.Max(b => b.MaxX); var maxY = bounds.Max(b => b.MaxY);
        var spanX = Math.Max(maxX - minX, 1e-6); var spanY = Math.Max(maxY - minY, 1e-6);
        const int iterations = 500;
        var timer = Stopwatch.StartNew();
        var candidateCount = 0;
        for (var i = 0; i < iterations; i++)
        {
            var x = minX + spanX * ((i * 137) % iterations) / iterations;
            var y = minY + spanY * ((i * 251) % iterations) / iterations;
            candidateCount += _spatialIndex.QueryUnordered(x - spanX / 100, y - spanY / 100, x + spanX / 100, y + spanY / 100).Count();
        }
        timer.Stop(); _lastBenchmarkQueryMilliseconds = timer.Elapsed.TotalMilliseconds / iterations;

        var canvasWidth = Math.Max(1, DrawingCanvas.ActualWidth); var canvasHeight = Math.Max(1, DrawingCanvas.ActualHeight);
        timer.Restart();
        for (var i = 0; i < iterations; i++)
            FindEntityAt(new Point(canvasWidth * ((i * 137) % iterations) / iterations,
                canvasHeight * ((i * 251) % iterations) / iterations), recordSample: false);
        timer.Stop(); _lastBenchmarkPickMilliseconds = timer.Elapsed.TotalMilliseconds / iterations;
        const int snapIterations = 250;
        var snapModes = _osnapModes.ToHashSet();
        timer.Restart();
        for (var i = 0; i < snapIterations; i++)
        {
            var point = new Point2(minX + spanX * ((i * 137) % snapIterations) / snapIterations,
                minY + spanY * ((i * 251) % snapIterations) / snapIterations);
            _spatialIndex.TryFindSnap(point, Math.Max(spanX, spanY) / 1000, snapModes, out _);
        }
        timer.Stop(); _lastBenchmarkOsnapMilliseconds = timer.Elapsed.TotalMilliseconds / snapIterations;
        using var process = Process.GetCurrentProcess(); process.Refresh();
        AppendCommandHistory($"BENCH ({iterations:N0} query/pick; {snapIterations:N0} snap iterations) — R-tree query avg {_lastBenchmarkQueryMilliseconds:0.000} ms; pick avg {_lastBenchmarkPickMilliseconds:0.000} ms; OSNAP avg {_lastBenchmarkOsnapMilliseconds:0.000} ms; query candidates {candidateCount:N0}; indexed entities {_spatialIndex.IndexedEntityCount:N0}; RAM {process.WorkingSet64 / 1024d / 1024d:0.0} MB.");
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { _editor.Undo(); AppendCommandHistory("Undo."); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _editor.Redo(); AppendCommandHistory("Redo."); }
    private void LineCommand_Click(object sender, RoutedEventArgs e) => BeginLineCommand();
    private void PolylineCommand_Click(object sender, RoutedEventArgs e) => PlineCommand();
    private void CopyTool_Click(object sender, RoutedEventArgs e) => BeginMove(copy: true);
    private void RotateTool_Click(object sender, RoutedEventArgs e) => BeginRotate();
    private void CircleCommand_Click(object sender, RoutedEventArgs e) => CircleCommand();
    private void RectangleCommand_Click(object sender, RoutedEventArgs e) => BeginRectangleCommand();
    private void ArcCommand_Click(object sender, RoutedEventArgs e) => ArcCommand();
    private void ZoomExtents_Click(object sender, RoutedEventArgs e) { SavePreviousView(); FitView(); RenderDrawing(false); AppendCommandHistory("Zoomed to drawing extents."); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { ZoomAt(new Point(DrawingCanvas.ActualWidth / 2, DrawingCanvas.ActualHeight / 2), 1.5); AppendCommandHistory($"Zoom {ZoomLevelText.Text}."); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { ZoomAt(new Point(DrawingCanvas.ActualWidth / 2, DrawingCanvas.ActualHeight / 2), 1.0 / 1.5); AppendCommandHistory($"Zoom {ZoomLevelText.Text}."); }
    private void DraftingToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, StatusOrthoToggle) && StatusOrthoToggle.IsChecked == true)
        {
            StatusPolarToggle.IsChecked = false;
            UpdatePolarGuide(null, null);
        }
        else if (ReferenceEquals(sender, StatusPolarToggle) && StatusPolarToggle.IsChecked == true)
            StatusOrthoToggle.IsChecked = false;

        if (sender is ToggleButton toggle) AppendCommandHistory($"{toggle.Content}: {(toggle.IsChecked == true ? "ON" : "OFF")}");
        if (ReferenceEquals(sender, StatusOsnapToggle) && StatusOsnapToggle.IsChecked != true) UpdateOsnapMarker(null);
        if (ReferenceEquals(sender, StatusOtrackToggle) && StatusOtrackToggle.IsChecked != true) UpdateTrackingGuide(null, null);
        if (ReferenceEquals(sender, StatusPolarToggle) && StatusPolarToggle.IsChecked != true) UpdatePolarGuide(null, null);
    }

    private void OsnapMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !Enum.TryParse<OsnapMode>(item.Tag?.ToString(), out var mode)) return;
        if (item.IsChecked) _osnapModes.Add(mode); else _osnapModes.Remove(mode);
        AppendCommandHistory($"OSNAP {mode.ToString().ToUpperInvariant()}: {(item.IsChecked ? "ON" : "OFF")}.");
        if (_lastPointer != default) ConstrainPointer(ScreenToWorld(_lastPointer));
    }

    private void GridToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_wheelZoomPreviewActive) ResetWheelZoomPreview();
        _lastGridImageKey = null;
        RenderDrawing(false);
        AppendCommandHistory($"GRID: {(GridToggleButton.IsChecked == true ? "ON" : "OFF")}");
    }

    private void DynamicInputToggle_Click(object sender, RoutedEventArgs e)
    {
        _dynamicInputEnabled = StatusDynamicInputToggle.IsChecked == true;
        if (!_dynamicInputEnabled) { _dynamicIdleVisible = false; _dynamicCommandEntry = false; CommandSuggestionPopup.IsOpen = false; }
        UpdateDynamicInputMode();
        AppendCommandHistory($"DYN: {(_dynamicInputEnabled ? "ON" : "OFF")}");
        if (_dynamicInputEnabled && IsDynamicCommandActive) { PositionDynamicInput(_lastPointer); FocusDynamicInput(); }
        else DrawingCanvas.Focus();
    }

    private async Task LoadCommandUsageAsync()
    {
        _commandUsagePath = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1", "command-usage.json");
        Dictionary<string, int>? saved = null;
        try
        {
            var path = _commandUsagePath;
            saved = await Task.Run<Dictionary<string, int>?>(() => File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                : null);
        }
        catch { }
        _commandUsage.Clear();
        if (saved is not null)
            foreach (var item in saved.Where(item => item.Value >= 0)) _commandUsage[item.Key] = item.Value;
        var hadPendingUsage = _pendingCommandUsage.Count > 0;
        foreach (var item in _pendingCommandUsage) _commandUsage[item.Key] = _commandUsage.GetValueOrDefault(item.Key) + item.Value;
        _pendingCommandUsage.Clear();
        _commandUsageLoaded = true;
        if (hadPendingUsage) PersistCommandUsage();
        RefreshFrequentCommandsToolbar();
    }

    private void RecordCommandUsage(string normalized)
    {
        var canonical = normalized switch
        {
            "LINE" or "L" or "PLINE" or "PL" => "LINE",
            "CIRCLE" or "C" => "CIRCLE",
            "RECTANGLE" or "REC" or "RECT" => "RECTANGLE",
            "ARC" or "A" => "ARC",
            "MOVE" or "M" => "MOVE",
            "COPY" or "CO" => "COPY",
            "ERASE" or "DELETE" or "DEL" or "E" => "ERASE",
            "SELECT" or "S" => "SELECT",
            "ZOOM" or "Z" or "ZE" or "Z E" or "ZOOM E" or "ZEXTENTS" or "ZOOMEXTENTS" or "ZOOM EXTENTS" => "ZE",
            "ZP" or "Z P" or "ZOOM P" or "ZPREVIOUS" => "ZPREVIOUS",
            "PAN" => "PAN", "UNDO" or "U" => "UNDO", "REDO" or "Y" => "REDO",
            "NEW" or "QNEW" => "NEW", "OPEN" => "OPEN", "SAVE" => "SAVE", "SAVEAS" or "SAVE AS" => "SAVEAS",
            "PLOT" or "EXPORTPDF" or "EXPORT PDF" or "PDF" => "PLOT",
            "LAYOUT" or "NEWLAYOUT" or "NEW LAYOUT" => "LAYOUT", "REGEN" => "REGEN", _ => null
        };
        if (canonical is null) return;
        _commandUsage[canonical] = _commandUsage.GetValueOrDefault(canonical) + 1;
        if (!_commandUsageLoaded) _pendingCommandUsage[canonical] = _pendingCommandUsage.GetValueOrDefault(canonical) + 1;
        if (_commandUsageLoaded) PersistCommandUsage();
        RefreshFrequentCommandsToolbar();
    }

    private void PersistCommandUsage()
    {
        try
        {
            if (_commandUsagePath is not null)
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(_commandUsagePath)!);
                File.WriteAllText(_commandUsagePath, JsonSerializer.Serialize(_commandUsage));
            }
        }
        catch { }
    }

    private void RefreshFrequentCommandsToolbar()
    {
        if (AutoHideCommandList is null) return;
        AutoHideCommandList.Children.Clear();
        var ranked = FrequentCommandCatalog.Select((command, order) => new { Command = command, Order = order,
                Count = _commandUsage.GetValueOrDefault(command.Command) })
            .OrderByDescending(item => item.Count).ThenBy(item => item.Order).Take(20);
        foreach (var item in ranked)
        {
            var button = new Button { Content = new TextBlock { Text = item.Command.Icon, FontSize = 19, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                Width = 34, Height = 32, Margin = new Thickness(0, 1, 0, 1), Padding = new Thickness(0), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = $"{item.Command.Caption} ({item.Command.Command}) — used {item.Count:N0} time(s)", Cursor = Cursors.Hand };
            button.Click += (_, _) => ProcessCommand(item.Command.Command);
            AutomationProperties.SetAutomationId(button, $"FrequentCommand_{item.Command.Command.Replace(" ", string.Empty)}");
            AutoHideCommandList.Children.Add(button);
        }
    }

    private void CommandInputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            var command = CommandInputTextBox.Text.Trim();
            if (CommandSuggestionPopup.IsOpen && CommandSuggestionsList.SelectedItem is CommandHint selected)
                command = selected.Name;
            CommandSuggestionPopup.IsOpen = false;
            CommandInputTextBox.Clear();
            if (!string.IsNullOrWhiteSpace(command)) ProcessCommand(command);
            else if (_pendingSelectionAction != SelectionAction.None) ProcessCommand(string.Empty);
            else if (!string.IsNullOrWhiteSpace(_lastCommand)) ProcessCommand(_lastCommand);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && CommandSuggestionPopup.IsOpen)
        {
            CommandSuggestionPopup.IsOpen = false; e.Handled = true; return;
        }
        if (CommandSuggestionPopup.IsOpen && e.Key is Key.Down or Key.Up)
        {
            MoveSuggestionSelection(e.Key == Key.Down ? 1 : -1); e.Handled = true; return;
        }
        if (e.Key == Key.Tab && CommandSuggestionPopup.IsOpen && CommandSuggestionsList.SelectedItem is CommandHint)
        {
            AcceptSelectedSuggestion(); e.Handled = true;
        }
    }

    private void CommandInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CommandInputTextBox is null || CommandSuggestionsList is null || CommandSuggestionPopup is null) return;
        UpdateCommandSuggestions(CommandInputTextBox.Text, CommandInputTextBox);
        CommandInputTextBox.ToolTip = CommandInputTextBox.Text.Length == 0 ? "Type a command; press Space or Enter to run" :
            CommandSuggestionsList.Items.Count == 0 ? "No matching command" : "Hover a suggestion and click to run; Space or Enter runs the selected command";
    }

    private void MoveSuggestionSelection(int offset)
    {
        var count = CommandSuggestionsList.Items.Count;
        if (count == 0) return;
        var next = Math.Clamp(CommandSuggestionsList.SelectedIndex + offset, 0, count - 1);
        CommandSuggestionsList.SelectedIndex = next;
        CommandSuggestionsList.ScrollIntoView(CommandSuggestionsList.SelectedItem);
    }

    private void AcceptSelectedSuggestion()
    {
        if (CommandSuggestionsList.SelectedItem is not CommandHint selected) return;
        var input = _suggestionTarget ?? CommandInputTextBox;
        input.Text = selected.Name;
        input.CaretIndex = input.Text.Length;
        CommandSuggestionPopup.IsOpen = false;
        input.Focus();
    }

    private void CommandSuggestionsList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private ListBoxItem? SuggestionRowAt(MouseEventArgs e)
    {
        var hit = CommandSuggestionsList.InputHitTest(e.GetPosition(CommandSuggestionsList)) as DependencyObject;
        return hit is null ? null : ItemsControl.ContainerFromElement(CommandSuggestionsList, hit) as ListBoxItem;
    }

    private void CommandSuggestionsList_MouseMove(object sender, MouseEventArgs e)
    {
        if (SuggestionRowAt(e) is { } row) CommandSuggestionsList.SelectedItem = row.DataContext;
    }

    private void CommandSuggestionsList_MouseClick(object sender, MouseButtonEventArgs e)
    {
        // Resolve the clicked row, so borders and scrollbars never execute a stale selection.
        if (e.ChangedButton != MouseButton.Left || SuggestionRowAt(e)?.DataContext is not CommandHint command) return;
        e.Handled = true; // Keep focus in the input while executing one click.
        ExecuteCommandSuggestion(command);
    }

    private void ExecuteCommandSuggestion(CommandHint command)
    {
        var fromDyn = _dynamicCommandEntry || Keyboard.FocusedElement == DynamicValueTextBox;
        CommandSuggestionPopup.IsOpen = false;
        _dynamicCommandEntry = false; _dynamicIdleVisible = false;
        DynamicValueTextBox.Clear(); CommandInputTextBox.Clear();
        ProcessCommand(command.Name);
        if (fromDyn && IsDynamicCommandActive)
        {
            // Command is now active: retain an editable input for coordinates or options.
            DynamicInputPanel.Visibility = Visibility.Visible;
            DynamicInputEntryPanel.Visibility = Visibility.Visible;
            DynamicValueTextBox.Focus();
        }
        else DrawingCanvas.Focus();
        UpdateDynamicInputMode();
    }

    private void DynamicValueTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_dynamicCommandEntry) UpdateCommandSuggestions(DynamicValueTextBox.Text, DynamicValueTextBox);
    }

    private TextBox? _suggestionTarget;

    private void UpdateCommandSuggestions(string value, TextBox target)
    {
        if (CommandSuggestionsList is null || CommandSuggestionPopup is null) return;
        var search = string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
        if (search.Length == 0)
        {
            CommandSuggestionPopup.IsOpen = false; CommandSuggestionsList.ItemsSource = null; return;
        }
        // Prefix/alias matches win, then typo-tolerant edit-distance matches.
        // This keeps the best command at the top (e.g. LNE -> LINE, CIRCEL -> CIRCLE).
        var forms = VietCommandForms(search);
        var strippedLower = StripVietnamese(search).ToLowerInvariant();
        var suggestions = CommandHints
            .Select(item => (Item: item, Result: ScoreCommandHint(item, forms, strippedLower)))
            .Where(result => result.Result.Matched)
            .OrderBy(result => result.Result.Score)
            .ThenBy(result => result.Item.Name.Length)
            .Take(8)
            .Select(result => result.Item)
            .ToArray();
        CommandSuggestionsList.ItemsSource = suggestions;
        CommandSuggestionsList.SelectedIndex = suggestions.Length == 0 ? -1 : 0;
        _suggestionTarget = target;
        if (ReferenceEquals(target, DynamicValueTextBox))
        {
            // The DYN box sits inside a canvas panel that can be collapsed/unplaced; anchoring the popup to it dropped the
            // list at the window's top edge. Anchor to the drawing canvas at the cursor-following DYN position instead.
            var left = Canvas.GetLeft(DynamicInputPanel); var top = Canvas.GetTop(DynamicInputPanel);
            if (double.IsNaN(left)) left = _lastPointer.X + 18;
            if (double.IsNaN(top)) top = _lastPointer.Y + 38;
            CommandSuggestionPopup.PlacementTarget = DrawingCanvas;
            CommandSuggestionPopup.Placement = PlacementMode.Relative;
            CommandSuggestionPopup.HorizontalOffset = left;
            CommandSuggestionPopup.VerticalOffset = top + Math.Max(23, DynamicInputPanel.ActualHeight) + 2;
        }
        else
        {
            CommandSuggestionPopup.PlacementTarget = target;
            CommandSuggestionPopup.Placement = PlacementMode.Bottom;
            CommandSuggestionPopup.HorizontalOffset = 0; CommandSuggestionPopup.VerticalOffset = 2;
        }
        CommandSuggestionPopup.StaysOpen = true;
        CommandSuggestionPopup.IsOpen = suggestions.Length > 0;
    }

    private static int CommandEditDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        if (Math.Abs(left.Length - right.Length) > 3) return Math.Max(left.Length, right.Length);
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private void CommandSuggestionsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) { AcceptSelectedSuggestion(); e.Handled = true; return; }
        if (e.Key == Key.Escape) { CommandSuggestionPopup.IsOpen = false; e.Handled = true; return; }
        if ((e.Key is Key.Enter or Key.Space) && CommandSuggestionsList.SelectedItem is CommandHint selected)
        { ExecuteCommandSuggestion(selected); e.Handled = true; }
    }

    private void ProcessCommand(string command)
    {
        if (TryHandleCornerInput(command)) return;
        if (string.IsNullOrWhiteSpace(command) && _pendingSelectionAction != SelectionAction.None)
        {
            CompleteSelectionStage();
            return;
        }
        if (!string.IsNullOrWhiteSpace(command)) AppendCommandHistory($"Command: {command.ToUpperInvariant()}");
        var normalized = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        if (normalized.Length > 0)
        {
            _lastCommand = normalized;
            _trackingAnchors.Clear();
            UpdateTrackingGuide(null, null);
        }
        normalized = ResolveVietnameseCommand(normalized);
        normalized = ResolveCommandAlias(normalized);
        RecordCommandUsage(normalized);
        if (TryHandleViewCommand(normalized)) return;
        if (TryHandleAutoCadCommand(normalized)) return;
        switch (normalized)
        {
            case "ALIASES": case "ALIASEDIT": case "SHORTCUTS": ShowCommandAliasManager(); break;
            case "PROPERTIES": case "PR": if (!_propertiesVisible) TogglePropertiesPalette(); else RefreshPropertiesPalette(); break;
            case "DIST": case "DI": BeginDistancePointMeasurement(); break;
            case "AREA": case "AA": MeasureSelectedEntities(normalized); break;
            case "OVERKILL": case "OV": RemoveDuplicateSelectedGeometry(); break;
            case "MATCHPROP": case "MA": MatchSelectedProperties(); break;
            case "BREAK": case "BR": BreakCommand(); break;
            case "ARRAY": case "AR": ArrayCommand(); break;
            case "ARRAYRECT": ArraySelectedGeometry(); break;
            case "ARRAYPOLAR": ArrayPolarCommand(); break;
            case "CHAMFER": case "CHA": BeginCorner(false); break;
            case "FILLET": case "F": BeginCorner(true); break;
            case "STRETCH": case "S": case "STR": BeginStretch(); break;
            case "HATCH": case "H": BeginHatch(); break;
            case "INSERT": case "I": BeginInsertBlockOrFile(); break;
            case "BLOCK": case "B": BeginBlockCommand(); break;
            case "ATTDEF": case "ATT": BeginAttDefCommand(); break;
            case "SCALE": case "SC": ScaleCommand(); break;
            case "MIRROR": case "MI": MirrorCommand(); break;
            case "OFFSET": case "O": OffsetSelectedGeometry(); break;
            case "TRIM": case "TR": BeginPickTool("TRIM", "TRIM: Select object to trim or shift-select to extend (cutting edges: all objects). Esc to end."); break;
            case "EXTEND": case "EX": BeginPickTool("EXTEND", "EXTEND: Select object to extend or shift-select to trim (boundaries: all objects). Esc to end."); break;
            case "DIM": case "DIMLINEAR": case "DLI": case "DIMLIN": BeginDimensionTool("DIMLINEAR"); break;
            case "DIMALIGNED": case "DAL": case "DIMALI": BeginDimensionTool("DIMALIGNED"); break;
            case "DIMRADIUS": case "DRA": case "DIMRAD": BeginDimensionTool("DIMRADIUS"); break;
            case "DIMDIAMETER": case "DDI": case "DIMDIA": BeginDimensionTool("DIMDIAMETER"); break;
            case "DIMANGULAR": case "DAN": case "DIMANG": BeginDimensionTool("DIMANGULAR"); break;
            case "DIMCONTINUE": case "DCO": BeginContinueOrBaseline("DIMCONTINUE"); break;
            case "DIMBASELINE": case "DBA": BeginContinueOrBaseline("DIMBASELINE"); break;
            case "TEXTEDIT": case "ED": BeginTextEditCommand(); break;
            case "QLEADER": case "LE": case "LEADER": BeginLeaderTool("QLEADER"); break;
            case "MLEADER": case "MLD": BeginLeaderTool("MLEADER"); break;
            case "ID": BeginPickTool("ID", "ID: Click a point. Esc to end."); break;
            case "LIST": case "LI": ListSelectedEntities(); break;
            case "EXPLODE": case "X": ExplodeSelectedPolylines(); break;
            case "JOIN": case "J": RequireSelection("JOIN", JoinSelectedCurves); break;
            case "PURGE": case "PU": PurgeUnusedCadDefinitions(); break;
            case "TEXT": case "DT": CreateSimpleCadText(false); break;
            case "MTEXT": case "T": CreateSimpleCadText(true); break;
            case "LAYER": case "LA": case "-LAYER": OpenCadStyleManager(0); break;
            case "TEXTSTYLE": case "STYLE": case "ST": OpenCadStyleManager(1); break;
            case "DIMSTYLE": case "D": case "DST": case "DDIM": OpenCadStyleManager(2); break;
            case "LINESTYLE": case "LINETYPE": case "LT": OpenCadStyleManager(3); break;
            case "LINE": case "L": BeginLineCommand(); break;
            case "PLINE": case "PL": PlineCommand(); break;
            case "REVCLOUD": RevCloudCommand(); break;
            case "WIPEOUT": WipeoutCommand(); break;
            case "CIRCLE": case "C": CircleCommand(); break;
            case "RECTANGLE": case "REC": case "RECT": BeginRectangleCommand(); break;
            case "ARC": case "A": ArcCommand(); break;
            case "ZOOM": case "Z": case "ZE": case "Z E": case "ZOOM E": case "ZEXTENTS": case "ZOOMEXTENTS": case "ZOOM EXTENTS": ZoomExtents_Click(this, new RoutedEventArgs()); break;
            case "ZP": case "Z P": case "ZOOM P": case "ZPREVIOUS": ZoomPrevious(); break;
            case "ERASE": case "DELETE": case "DEL": case "E": EraseSelection(); break;
            case "MOVE": case "M": BeginMove(copy: false); break;
            case "COPY": case "CO": BeginMove(copy: true); break;
            case "ROTATE": case "RO": BeginRotate(); break;
            case "SELECT": SelectCommand(); break;
            case "UNDO": case "U": Undo_Click(this, new RoutedEventArgs()); break;
            case "REDO": case "Y": Redo_Click(this, new RoutedEventArgs()); break;
            case "NEW": FileNew_Click(this, new RoutedEventArgs()); break;
            case "OPEN": FileOpen_Click(this, new RoutedEventArgs()); break;
            case "SAVE": FileSave_Click(this, new RoutedEventArgs()); break;
            case "SAVEAS": case "SAVE AS": FileSaveAs_Click(this, new RoutedEventArgs()); break;
            case "PLOT": case "EXPORTPDF": case "EXPORT PDF": case "PDF": ShowPlotDialog(); break;
            case "LAYOUT": case "NEWLAYOUT": case "NEW LAYOUT": NewLayout_Click(this, new RoutedEventArgs()); break;
            case "QNEW": FileNew_Click(this, new RoutedEventArgs()); break;
            case "REGEN": RenderDrawing(false); break;
            case "PERF": case "PERFORMANCE": ShowPerformanceMetrics(); break;
            case "BENCH": case "BENCHMARK": RunDrawingBenchmark(); break;
            case "SKIPTEXT": ToggleTextMTextRenderSkipForPerfTest(); break;
            case "QUIT": case "EXIT": Close(); break;
            case "": break;
            case "PAN": AppendCommandHistory("Pan with the middle mouse button; use the wheel to zoom."); break;
            default:
                if (CommandHints.Any(x => x.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                    AppendCommandHistory($"{normalized}: Lệnh có trong danh mục nhưng chưa triển khai chức năng.");
                else { CommandInputTextBox.ToolTip = $"Unknown command: {command}"; AppendCommandHistory($"Unknown command: {command}"); }
                break;
        }
    }

    private void ToggleTextMTextRenderSkipForPerfTest()
    {
        _skipTextMTextForPerfTest = !_skipTextMTextForPerfTest;
        _lastRenderImage = null; ClearFrameCache();
        _lastRenderImageKey = null;
        _renderBuildSamples.Clear();
        _viewportFrameIntervals.Clear();
        _lastInteractionRenderTimestamp = 0;
        _geometryBuildCancellation?.Cancel();
        AppendCommandHistory(_skipTextMTextForPerfTest
            ? "SKIPTEXT ON: TEXT/MTEXT are excluded from viewport geometry/rendering. DWG import, stored entity data, pick and OSNAP are unchanged. Run PERF after zoom/pan to compare."
            : "SKIPTEXT OFF: TEXT/MTEXT rendering restored. Run PERF after zoom/pan to compare.");
        RenderDrawing(fitToDrawing: false);
    }

    private void BeginLineCommand()
    {
        EndLineCommand();
        _editor.BeginLine();
        AppendCommandHistory("Specify first point:");
        DynamicPromptText.Text = "Specify first point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        FocusDynamicInput();
    }

    private void BeginPolygonRegionCommand()
    {
        BeginPolylineCommand();
        _polygonRegionMode = true;
        AppendCommandHistory("POLYGON: pick the corner points; Enter, Esc or C closes the region (Backspace removes the last point).");
        DynamicPromptText.Text = "Specify first corner of the polygon:";
    }

    /// <summary>REVCLOUD: click the rough corner points of the area to mark up - same simple free-click input as the old
    /// POLYGON region tool - then FinishPolyline automatically resamples the closed boundary into evenly spaced,
    /// outward-bulging arcs (the classic scalloped look), using the Bulge mechanism already proven for PLINE/RECTANG.
    /// Deliberately simpler input than real AutoCAD's REVCLOUD (no Arc length/Style prompts, no freehand dragging) per
    /// the "cách vẽ có thể khác, object tạo ra đúng native AutoCAD là ok" guidance - arc size is picked automatically
    /// from the size of the area clicked, and the saved object is a genuine closed LWPOLYLINE with real arc bulges.</summary>
    private void RevCloudCommand()
    {
        BeginPolygonRegionCommand();
        _revCloudMode = true;
        AppendCommandHistory("REVCLOUD: click the corner points around the area to mark up; Enter or C closes it - scalloped arcs are added automatically.");
        DynamicPromptText.Text = "Specify start point of revision cloud:";
    }

    /// <summary>WIPEOUT: same simple free-click boundary input as REVCLOUD/old POLYGON region tool; FinishPolyline saves
    /// the result as a real native WIPEOUT object (masks whatever is behind it) instead of a plain polyline. iStCAD's
    /// own canvas only draws the boundary outline for now (see CadWipeoutData) - the actual masking happens in AutoCAD.</summary>
    private void WipeoutCommand()
    {
        BeginPolygonRegionCommand();
        _wipeoutMode = true;
        AppendCommandHistory("WIPEOUT: click the corner points of the area to mask; Enter or C closes it.");
        DynamicPromptText.Text = "Specify start point of wipeout boundary:";
    }

    /// <summary>Resamples a closed boundary loop into evenly-spaced-by-arc-length points - same arc-length-walk approach
    /// as ARRAY Path's PathStations, but for a closed loop only and returning points, not stations+tangent.</summary>
    private static List<Point2> ResampleClosedLoop(IReadOnlyList<Point2> boundary, double arcLength)
    {
        var raw = new List<Point2>(boundary);
        if (raw.Count < 3) return raw;
        if (Point2.Distance(raw[0], raw[^1]) > 1e-9) raw.Add(raw[0]);
        var cum = new double[raw.Count];
        for (var i = 1; i < raw.Count; i++) cum[i] = cum[i - 1] + Point2.Distance(raw[i - 1], raw[i]);
        var total = cum[^1];
        if (total < 1e-6) return boundary.ToList();
        var count = Math.Clamp((int)Math.Round(total / Math.Max(arcLength, 1e-6)), 3, 500);
        var result = new List<Point2>(count);
        for (var i = 0; i < count; i++)
        {
            var target = total * i / count;
            var seg = 1; while (seg < cum.Length - 1 && cum[seg] < target) seg++;
            var segStart = cum[seg - 1]; var segLen = cum[seg] - segStart;
            var t = segLen > 1e-12 ? (target - segStart) / segLen : 0;
            var a0 = raw[seg - 1]; var a1 = raw[seg];
            result.Add(new Point2(a0.X + (a1.X - a0.X) * t, a0.Y + (a1.Y - a0.Y) * t));
        }
        return result;
    }

    /// <summary>Builds the final revision-cloud vertices: resamples the clicked boundary into ~24 equal arcs (arc length
    /// picked automatically from the boundary's own perimeter, so no extra prompt is needed) and gives every segment the
    /// SAME bulge magnitude, with its SIGN chosen from the boundary's winding direction (shoelace signed area) so every
    /// arc bulges outward, away from the inside of the cloud, regardless of which way the user happened to click around it.</summary>
    private static PolylineVertex[] BuildRevCloudVertices(IReadOnlyList<Point2> boundary)
    {
        double perimeter = 0;
        for (var i = 0; i < boundary.Count; i++) perimeter += Point2.Distance(boundary[i], boundary[(i + 1) % boundary.Count]);
        var arcLength = Math.Max(perimeter / 24, 1e-3);
        var pts = ResampleClosedLoop(boundary, arcLength);
        double signedArea = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            signedArea += a.X * b.Y - b.X * a.Y;
        }
        var bulge = signedArea >= 0 ? 1.0 : -1.0;
        return pts.Select(p => new PolylineVertex(p, bulge)).ToArray();
    }

    private void BeginPolylineCommand()
    {
        EndLineCommand();
        _polygonRegionMode = false;
        _polylineActive = true;
        _polylineVertices.Clear();
        _polylineArcMode = false;
        _polylineTangent = null;
        _editor.MovePointer(ScreenToWorld(_lastPointer));
        AppendCommandHistory("Specify first polyline point; Enter finishes, C closes, A switches to Arc segments:");
        DynamicPromptText.Text = "Specify first polyline point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        FocusDynamicInput();
    }

    private void AddPolylineVertex(Point2 point)
    {
        if (!_polylineActive) return;
        if (_polylineVertices.Count == 0 || Point2.Distance(_polylineVertices[^1].Point, point) > 1e-10)
        {
            if (_polylineArcMode && _polylineVertices.Count > 0)
            {
                var from = _polylineVertices[^1].Point;
                var (bulge, exitTangent) = ComputeArcBulge(from, point, _polylineTangent);
                _polylineVertices[^1] = _polylineVertices[^1] with { Bulge = bulge };
                _polylineTangent = exitTangent;
            }
            else if (_polylineVertices.Count > 0)
            {
                var from = _polylineVertices[^1].Point;
                var len = Point2.Distance(from, point);
                if (len > 1e-10) _polylineTangent = new Point2((point.X - from.X) / len, (point.Y - from.Y) / len);
            }
            _polylineVertices.Add(new PolylineVertex(point));
        }
        _editor.MovePointer(point);
        DynamicPromptText.Text = _polylineArcMode ? "Specify endpoint of arc or [Line]:" : "Specify next point or [Arc/Close]:";
        DynamicValueTextBox.Clear();
        RenderPreview();
    }

    /// <summary>Computes the (Bulge, exit tangent) of a PLINE arc segment from <paramref name="from"/> to <paramref name="to"/>
    /// that is tangent to <paramref name="entryTangent"/> at its start - AutoCAD's default PLINE Arc behavior (continues
    /// tangent to whatever the polyline was already doing, rather than asking for Angle/Center/Direction/Radius/Second point).
    /// Falls back to a rightward tangent (1,0) for the very first segment of a polyline, when no direction exists yet.</summary>
    private static (double Bulge, Point2 ExitTangent) ComputeArcBulge(Point2 from, Point2 to, Point2? entryTangent)
    {
        var t = entryTangent ?? new Point2(1, 0);
        var chord = new Point2(to.X - from.X, to.Y - from.Y);
        var chordLen = Math.Sqrt(chord.X * chord.X + chord.Y * chord.Y);
        if (chordLen < 1e-10) return (0, t);
        var phi = Math.Atan2(t.X * chord.Y - t.Y * chord.X, t.X * chord.X + t.Y * chord.Y); // signed angle from tangent to chord
        var bulge = Math.Tan(phi / 2);
        var theta = 2 * phi; // total included arc angle, same sign convention as the bulge
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        var exit = new Point2(t.X * cos - t.Y * sin, t.X * sin + t.Y * cos);
        return (bulge, exit);
    }

    /// <summary>Tessellates a single bulge-curved segment from "from" to "to" into intermediate points (not including
    /// "from" itself, but including "to" as the last point) - same formula as PolylineTessellator.Tessellate, kept in
    /// sync with it, used here only for the live preview of the in-progress PLINE arc segment while dragging.</summary>
    private static IEnumerable<Point2> SampleBulgeSegment(Point2 from, Point2 to, double bulge, int maxArcSteps = 72)
    {
        if (Math.Abs(bulge) < 1e-10) { yield return to; yield break; }
        var theta = 4 * Math.Atan(bulge);
        var dx = to.X - from.X; var dy = to.Y - from.Y;
        var chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-10) { yield return to; yield break; }
        var offset = chord / (2 * Math.Tan(theta / 2));
        var center = new Point2((from.X + to.X) / 2 - dy / chord * offset, (from.Y + to.Y) / 2 + dx / chord * offset);
        var startAngle = Math.Atan2(from.Y - center.Y, from.X - center.X);
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(theta) / (Math.PI / Math.Max(4, maxArcSteps / 2))), 2, maxArcSteps);
        var radius = Point2.Distance(center, from);
        for (var step = 1; step <= steps; step++)
        {
            var angle = startAngle + theta * step / steps;
            yield return new Point2(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        }
    }

    private void FinishPolyline(bool close = false)
    {
        if (!_polylineActive) return;
        _polylineArcMode = false; _polylineTangent = null;
        var region = _polygonRegionMode;
        _polygonRegionMode = false;
        var revCloud = _revCloudMode;
        _revCloudMode = false;
        var wipeout = _wipeoutMode;
        _wipeoutMode = false;
        if (region && _polylineVertices.Count < 3) { _polylineVertices.Clear(); _polylineActive = false; _editor.EndCommand(); AppendCommandHistory(revCloud ? "REVCLOUD cancelled: at least three points are required." : wipeout ? "WIPEOUT cancelled: at least three points are required." : "POLYGON cancelled: at least three points are required."); UpdateDynamicInputMode(); DrawingCanvas.Focus(); RenderDrawing(false); return; }
        if (revCloud && _polylineVertices.Count >= 3)
        {
            var boundary = _polylineVertices.Select(v => v.Point).ToArray();
            var cloudVertices = BuildRevCloudVertices(boundary);
            _polylineActive = false;
            _polylineVertices.Clear();
            _editor.EndCommand();
            _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), [_editor.StyleNew(new PolylineEntity(cloudVertices, true, Guid.NewGuid()))]);
            AppendCommandHistory($"REVCLOUD: revision cloud created ({cloudVertices.Length} arcs).");
            UpdateDynamicInputMode(); DrawingCanvas.Focus(); RenderDrawing(false);
            return;
        }
        if (wipeout && _polylineVertices.Count >= 3)
        {
            var boundary = _polylineVertices.Select(v => v.Point).ToArray();
            double minX = boundary.Min(p => p.X), minY = boundary.Min(p => p.Y), maxX = boundary.Max(p => p.X), maxY = boundary.Max(p => p.Y);
            var proxy = (ProxyCadEntity)_editor.StyleNew(new ProxyCadEntity("WIPEOUT", new Point2(minX, minY), new Point2(maxX, maxY), true, Guid.NewGuid(), _editor.Drawing.CurrentLayer,
                ProxyGraphicsPaths: new[] { new ProxyPreviewPath(boundary, true) }, Wipeout: new CadWipeoutData(boundary)));
            _polylineActive = false;
            _polylineVertices.Clear();
            _editor.EndCommand();
            _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), [proxy]);
            AppendCommandHistory("WIPEOUT: wipeout boundary created (real native WIPEOUT - masks what's behind it in AutoCAD; iStCAD's own view currently only shows its outline).");
            UpdateDynamicInputMode(); DrawingCanvas.Focus(); RenderDrawing(false);
            return;
        }
        if (_polylineVertices.Count >= 2)
        {
            var vertices = _polylineVertices.ToArray();
            close = (close || region) && vertices.Length > 2;
            if (close && Point2.Distance(vertices[0].Point, vertices[^1].Point) < 1e-10)
                vertices = vertices[..^1];
            _polylineActive = false;
            _polylineVertices.Clear();
            _editor.EndCommand();
            _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), [_editor.StyleNew(new PolylineEntity(vertices, close, Guid.NewGuid()))]);
            AppendCommandHistory($"Polyline created: {vertices.Length} vertices{(close ? ", closed" : string.Empty)}.");
        }
        else
        {
            _polylineActive = false;
            _polylineVertices.Clear();
            _editor.EndCommand();
            AppendCommandHistory("Polyline cancelled: at least two points are required.");
        }
        UpdateDynamicInputMode();
        DrawingCanvas.Focus();
        RenderDrawing(false);
    }

    private void BeginCircleCommand()
    {
        EndLineCommand();
        _editor.BeginCircle();
        AppendCommandHistory("Specify center point:");
        DynamicPromptText.Text = "Specify center point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        FocusDynamicInput();
    }

    private void BeginRectangleCommand()
    {
        EndLineCommand();
        _rectangleActive = true;
        _rectangleFirstCorner = null;
        _rectanglePendingOption = null;
        var prompt = RectangleFirstCornerPrompt();
        DynamicPromptText.Text = prompt;
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        AppendCommandHistory(prompt);
        FocusDynamicInput();
    }

    private string RectangleFirstCornerPrompt() =>
        _rectangleFilletRadius > 1e-9 ? $"Specify first corner point or [Chamfer/Fillet] <Fillet {_rectangleFilletRadius:0.###}>:"
        : _rectangleChamfer1 > 1e-9 || _rectangleChamfer2 > 1e-9 ? $"Specify first corner point or [Chamfer/Fillet] <Chamfer {_rectangleChamfer1:0.###},{_rectangleChamfer2:0.###}>:"
        : "Specify first corner point or [Chamfer/Fillet]:";

    /// <summary>Cuts straight chamfer lines or rounded fillet arcs at every corner of the rectangle (RECTANG's C/F options).
    /// Horizontal edges get <paramref name="chamfer1"/> and vertical edges get <paramref name="chamfer2"/>, matching
    /// AutoCAD's own behaviour for an axis-aligned rectangle (no rotation support, consistent with the rest of RECTANG).</summary>
    private static List<PolylineVertex> BuildRectangleVertices(Point2 first, Point2 opposite, double chamfer1, double chamfer2, double filletRadius)
    {
        var corners = new[] { first, new Point2(opposite.X, first.Y), opposite, new Point2(first.X, opposite.Y) };
        var hasFillet = filletRadius > 1e-9;
        var hasChamfer = !hasFillet && (chamfer1 > 1e-9 || chamfer2 > 1e-9);
        if (!hasFillet && !hasChamfer) return corners.Select(c => new PolylineVertex(c)).ToList();
        double signedArea = 0;
        for (var i = 0; i < 4; i++) { var a = corners[i]; var b = corners[(i + 1) % 4]; signedArea += a.X * b.Y - b.X * a.Y; }
        var ccw = signedArea > 0;
        var result = new List<PolylineVertex>();
        for (var i = 0; i < 4; i++)
        {
            var prev = corners[(i + 3) % 4]; var cur = corners[i]; var next = corners[(i + 1) % 4];
            var inLen = Point2.Distance(prev, cur); var outLen = Point2.Distance(cur, next);
            var inDir = inLen < 1e-12 ? new Point2(0, 0) : new Point2((cur.X - prev.X) / inLen, (cur.Y - prev.Y) / inLen);
            var outDir = outLen < 1e-12 ? new Point2(0, 0) : new Point2((next.X - cur.X) / outLen, (next.Y - cur.Y) / outLen);
            var incomingIsHorizontal = Math.Abs(inDir.Y) < 1e-9;
            double enterCut, leaveCut;
            if (hasFillet) enterCut = leaveCut = Math.Min(filletRadius, Math.Min(inLen, outLen) / 2);
            else
            {
                var dIn = incomingIsHorizontal ? chamfer1 : chamfer2;
                var dOut = incomingIsHorizontal ? chamfer2 : chamfer1;
                enterCut = Math.Min(dIn, inLen / 2); leaveCut = Math.Min(dOut, outLen / 2);
            }
            var p1 = new Point2(cur.X - inDir.X * enterCut, cur.Y - inDir.Y * enterCut);
            var p2 = new Point2(cur.X + outDir.X * leaveCut, cur.Y + outDir.Y * leaveCut);
            var bulge = hasFillet ? (ccw ? 1 : -1) * Math.Tan(Math.PI / 8) : 0;
            result.Add(new PolylineVertex(p1, bulge));
            result.Add(new PolylineVertex(p2));
        }
        return result;
    }

    private void BeginArcCommand()
    {
        EndLineCommand();
        _arcActive = true;
        _arcCenter = _arcStartPoint = null;
        DynamicPromptText.Text = "Specify arc center:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        AppendCommandHistory("Specify arc center:");
        FocusDynamicInput();
    }

    private void AppendCommandHistory(string text)
    {
        if (_pickTool is not null && _pickTool != "ZOOMW" && DynamicPromptText is not null && text.StartsWith(_pickTool + ":", StringComparison.Ordinal) && !text.Contains("Done", StringComparison.Ordinal))
        {
            DynamicPromptText.Text = text; // the same prompt follows the cursor as a tooltip
            UpdateDynamicInputMode();
        }
        CommandStatusText.Text = text;
        CommandStatusText.ToolTip = text;
        // Preserve selection while the user is copying command history.
        // Appending to a read-only textbox must not steal focus from the drawing canvas.
        var selectingHistory = CommandHistoryTextBox.IsKeyboardFocusWithin && CommandHistoryTextBox.SelectionLength > 0;
        CommandHistoryTextBox.AppendText(text + Environment.NewLine);
        if (CommandHistoryTextBox.LineCount > 1000)
        {
            var excess = CommandHistoryTextBox.LineCount - 1000;
            var end = CommandHistoryTextBox.GetCharacterIndexFromLineIndex(excess);
            if (end > 0) CommandHistoryTextBox.Text = CommandHistoryTextBox.Text[end..];
        }
        if (!selectingHistory && CommandExpandedPanel.Visibility == Visibility.Visible) CommandHistoryTextBox.ScrollToEnd();
    }

    private bool _updatingPropertiesFilter;
    private void PropertiesSelectionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingPropertiesFilter && _propertiesVisible) RefreshPropertiesPalette();
    }

    private void RefreshPropertiesPalette()
    {
        SyncStyleCombosToSelection();
        if (GeneralPropertiesRows is null || !_propertiesVisible) return;
        GeneralPropertiesRows.Children.Clear();
        PropertiesGeometryRows.Children.Clear(); PropertiesTextRows.Children.Clear();
        PropertiesViewRows.Children.Clear(); PropertiesMiscRows.Children.Clear();
        var selected = _selectedEntityIds.Select(id => _spatialIndex.TryGetEntity(id, out var item) ? item : null).OfType<CadEntity>().ToArray();
        var typeGroups = selected.GroupBy(GetEntityTypeName).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        var filterOptions = new List<string> { selected.Length == 0 ? "No selection" : $"All ({selected.Length})" };
        filterOptions.AddRange(typeGroups.Select(group => $"{group.Key} ({group.Count()})"));
        var priorFilter = PropertiesSelectionFilter.SelectedItem as string;
        _updatingPropertiesFilter = true;
        try
        {
            PropertiesSelectionFilter.ItemsSource = filterOptions;
            PropertiesSelectionFilter.SelectedItem = filterOptions.Contains(priorFilter) ? priorFilter : filterOptions[0];
        }
        finally { _updatingPropertiesFilter = false; }
        if (selected.Length > 1 && PropertiesSelectionFilter.SelectedIndex > 0)
        {
            var groupIndex = PropertiesSelectionFilter.SelectedIndex - 1;
            if (groupIndex < typeGroups.Length) selected = typeGroups[groupIndex].ToArray();
        }
        PropertiesGeometryExpander.Visibility = selected.Length == 1 ? Visibility.Visible : Visibility.Collapsed;
        PropertiesTextExpander.Visibility = selected.Length == 1 && (selected[0] is CadTextEntity or CadMTextEntity) ? Visibility.Visible : Visibility.Collapsed;
        PropertiesViewExpander.Visibility = Visibility.Collapsed;
        PropertiesMiscExpander.Visibility = selected.Length == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (selected.Length == 0) { GeneralPropertiesExpander.Visibility = Visibility.Collapsed; return; }
        GeneralPropertiesExpander.Visibility = Visibility.Visible;
        var entity = selected[0];
        {
            CadColor? colorSwatch = null;
            if (selected.Length == 1)
                colorSwatch = entity.ExplicitColor ?? (_editor.Drawing.LayerColors.TryGetValue(entity.Layer, out var lc) ? lc : entity.DisplayColor);
            AddPropertyRow("Color", selected.Length == 1 ? FormatEntityColor(entity) : "Multiple", colorSwatch, commit: input => EditColor(selected, input));
        }
        AddPropertyRow("Layer", selected.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? entity.Layer : "Multiple", commit: input => EditLayer(selected, input));
        {
            string LineTypeText(CadEntity e) => e.LineTypeName ?? (_editor.Drawing.LayerStates.TryGetValue(e.Layer, out var ls) ? $"ByLayer ({ls.LineType})" : "ByLayer");
            AddPropertyRow("Linetype", selected.Select(LineTypeText).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? LineTypeText(entity) : "Multiple", commit: input => EditLineType(selected, input));
        }
        AddPropertyRow("Linetype scale", entity.LineTypeScale.ToString("0.####", CultureInfo.CurrentCulture), commit: input => EditLineTypeScale(selected, input));
        AddPropertyRow("Plot style", "ByColor", dimValue: true);
        AddPropertyRow("Lineweight", selected.Select(e => e.LineWeightMm).Distinct().Count() == 1 ? (entity.LineWeightMm is { } lw ? lw.ToString("0.##", CultureInfo.CurrentCulture) + " mm" : "ByLayer") : "Multiple", commit: input => EditLineWeight(selected, input));
        AddPropertyRow("Transparency", "ByLayer");
        if (selected.Length != 1) return;
        AddGroupedPropertyRow(PropertiesMiscRows, "Handle", entity.NativeHandle ?? "New entity");
        AddGroupedPropertyRow(PropertiesMiscRows, "Object ID", entity.Id.ToString("N")[..8].ToUpperInvariant());
        void Geometry(string name, string value, Func<string, string?>? commit = null) => AddGroupedPropertyRow(PropertiesGeometryRows, name, value, commit);
        void TextField(string name, string value, Func<string, string?>? commit = null) => AddGroupedPropertyRow(PropertiesTextRows, name, value, commit);
        string N(double value) => value.ToString("0.####", CultureInfo.CurrentCulture);
        switch (entity)
        {
            case CircleEntity circle:
                Geometry("Center X", N(circle.Center.X), input => EditNumber(circle, input, (e, v) => e with { Center = new Point2(v, e.Center.Y) }));
                Geometry("Center Y", N(circle.Center.Y), input => EditNumber(circle, input, (e, v) => e with { Center = new Point2(e.Center.X, v) }));
                Geometry("Radius", N(circle.Radius), input => EditNumber(circle, input, (e, v) => v > 0 ? e with { Radius = v } : null));
                Geometry("Diameter", N(circle.Radius * 2), input => EditNumber(circle, input, (e, v) => v > 0 ? e with { Radius = v / 2 } : null));
                Geometry("Circumference", N(Math.Tau * Math.Abs(circle.Radius))); Geometry("Area", N(Math.PI * circle.Radius * circle.Radius)); break;
            case LineEntity line:
                Geometry("Start X", N(line.Start.X), input => EditNumber(line, input, (e, v) => e with { Start = new Point2(v, e.Start.Y) }));
                Geometry("Start Y", N(line.Start.Y), input => EditNumber(line, input, (e, v) => e with { Start = new Point2(e.Start.X, v) }));
                Geometry("End X", N(line.End.X), input => EditNumber(line, input, (e, v) => e with { End = new Point2(v, e.End.Y) }));
                Geometry("End Y", N(line.End.Y), input => EditNumber(line, input, (e, v) => e with { End = new Point2(e.End.X, v) }));
                Geometry("Length", N(Point2.Distance(line.Start, line.End)), input => EditNumber(line, input, (e, v) => v > 0 ? e with { End = LineEndFrom(e, v, null) } : null));
                Geometry("Angle", N(Math.Atan2(line.End.Y-line.Start.Y,line.End.X-line.Start.X)*180/Math.PI), input => EditNumber(line, input, (e, v) => e with { End = LineEndFrom(e, null, v) })); break;
            case ArcEntity arc:
                Geometry("Center X", N(arc.Center.X), input => EditNumber(arc, input, (e, v) => e with { Center = new Point2(v, e.Center.Y) }));
                Geometry("Center Y", N(arc.Center.Y), input => EditNumber(arc, input, (e, v) => e with { Center = new Point2(e.Center.X, v) }));
                Geometry("Radius", N(arc.Radius), input => EditNumber(arc, input, (e, v) => v > 0 ? e with { Radius = v } : null));
                Geometry("Start angle", N(arc.StartAngleDegrees), input => EditNumber(arc, input, (e, v) => e with { StartAngleDegrees = v }));
                Geometry("End angle", N(arc.EndAngleDegrees), input => EditNumber(arc, input, (e, v) => e with { EndAngleDegrees = v })); break;
            case PolylineEntity pl:
                Geometry("Closed", pl.Closed ? "Yes" : "No", input => ApplyPropertyEdit(pl, pl with { Closed = input.Length > 0 && "yYtTcC1".IndexOf(input[0]) >= 0 }));
                Geometry("Vertices", pl.Vertices.Count.ToString(CultureInfo.CurrentCulture));
                var pts = PolylineTessellator.Tessellate(pl).ToArray();
                var length = pts.Zip(pts.Skip(1),(u,v)=>Point2.Distance(u,v)).Sum();
                if(pl.Closed && pts.Length>2) length+=Point2.Distance(pts[^1],pts[0]); Geometry("Length",N(length)); break;
            case CadTextEntity text:
                Geometry("Insertion X", N(text.Position.X), input => EditNumber(text, input, (e, v) => e with { Position = new Point2(v, e.Position.Y) }));
                Geometry("Insertion Y", N(text.Position.Y), input => EditNumber(text, input, (e, v) => e with { Position = new Point2(e.Position.X, v) }));
                TextField("Contents", text.Value, input => ApplyPropertyEdit(text, text with { Value = input }));
                TextField("Height", N(text.Height), input => EditNumber(text, input, (e, v) => v > 0 ? e with { Height = v } : null));
                TextField("Rotation", N(text.RotationRadians*180/Math.PI), input => EditNumber(text, input, (e, v) => e with { RotationRadians = v * Math.PI / 180 }));
                AddVnDiagnosisRows(text.TextFontFamily, text.Value, (name, value) => TextField(name, value)); break;
            case CadMTextEntity text:
                Geometry("Insertion X", N(text.Position.X), input => EditNumber(text, input, (e, v) => e with { Position = new Point2(v, e.Position.Y) }));
                Geometry("Insertion Y", N(text.Position.Y), input => EditNumber(text, input, (e, v) => e with { Position = new Point2(e.Position.X, v) }));
                TextField("Contents", text.Value.Replace("\n", " ↵ "), input =>
                {
                    var plain = input.Replace(" ↵ ", "\n").Replace("↵", "\n").Replace("\r\n", "\n");
                    return ApplyPropertyEdit(text, text with { Value = plain, FormattedValue = plain.Replace("\n", "\\P") });
                });
                TextField("Height", N(text.Height), input => EditNumber(text, input, (e, v) => v > 0 ? e with { Height = v } : null));
                TextField("Width", N(text.Width), input => EditNumber(text, input, (e, v) => v >= 0 ? e with { Width = v } : null));
                TextField("Rotation", N(text.RotationRadians*180/Math.PI), input => EditNumber(text, input, (e, v) => e with { RotationRadians = v * Math.PI / 180 }));
                AddVnDiagnosisRows(text.TextFontFamily, text.Value, (name, value) => TextField(name, value)); break;
            case ProxyCadEntity { Dimension: { } dim } dimProxy:
                Geometry("Object", "Dimension (" + dim.Kind.ToLowerInvariant() + ")");
                Geometry("Measurement", DimensionMeasureText(dim));
                TextField("Text override", dim.TextOverride ?? "", input => ApplyPropertyEdit(dimProxy, RecomposeDimension(dimProxy, dim with { TextOverride = string.IsNullOrWhiteSpace(input) ? null : input.Trim() })));
                Geometry("Dim style", dim.Style.Name); Geometry("Scale", "1:" + N(dim.Style.Scale));
                break;
            case ProxyCadEntity { InsertData: { } ins } insProxy:
                Geometry("Object", "Block reference");
                Geometry("Block name", ins.BlockName);
                TextField("Position X", N(ins.Position.X), input => EditNumber(insProxy, input, (e, v) => TransformProxy(e, pt => new Point2(pt.X + v - ins.Position.X, pt.Y), 0, 1)));
                TextField("Position Y", N(ins.Position.Y), input => EditNumber(insProxy, input, (e, v) => TransformProxy(e, pt => new Point2(pt.X, pt.Y + v - ins.Position.Y), 0, 1)));
                TextField("Rotation", N(ins.Rotation * 180 / Math.PI), input => EditNumber(insProxy, input, (e, v) =>
                {
                    var delta = v * Math.PI / 180 - ins.Rotation; double c = Math.Cos(delta), sn = Math.Sin(delta);
                    return TransformProxy(e, pt => new Point2(ins.Position.X + (pt.X - ins.Position.X) * c - (pt.Y - ins.Position.Y) * sn, ins.Position.Y + (pt.X - ins.Position.X) * sn + (pt.Y - ins.Position.Y) * c), delta, 1);
                }));
                TextField("Scale", N(ins.ScaleX), input => EditNumber(insProxy, input, (e, v) =>
                    Math.Abs(ins.ScaleX) < 1e-12 || v <= 0 ? null : TransformProxy(e, pt => new Point2(ins.Position.X + (pt.X - ins.Position.X) * v / ins.ScaleX, ins.Position.Y + (pt.Y - ins.Position.Y) * v / ins.ScaleX), 0, v / ins.ScaleX)));
                if (insProxy.HasBounds) { Geometry("Width", N(Math.Abs(insProxy.BoundsMax.X - insProxy.BoundsMin.X))); Geometry("Height", N(Math.Abs(insProxy.BoundsMax.Y - insProxy.BoundsMin.Y))); }
                break;
            case ProxyCadEntity proxy:
                Geometry("Native object", proxy.NativeType);
                if(proxy.HasBounds) { Geometry("Width",N(Math.Abs(proxy.BoundsMax.X-proxy.BoundsMin.X))); Geometry("Height",N(Math.Abs(proxy.BoundsMax.Y-proxy.BoundsMin.Y))); } break;
        }
    }

    private static Grid BuildPropertyRow(string label, string value, CadColor? swatch = null, bool dimValue = false)
    {
        var row = new Grid { Height = 21, Background = Brushes.Transparent, ToolTip = $"{label}: {value}" };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = label, Margin = new Thickness(18, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(176, 183, 193)), TextTrimming = TextTrimming.CharacterEllipsis });
        var valuePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        if (swatch is { } c)
            valuePanel.Children.Add(new Border { Width = 11, Height = 11, Margin = new Thickness(0, 0, 6, 0), BorderBrush = new SolidColorBrush(Color.FromRgb(200, 205, 212)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) });
        valuePanel.Children.Add(new TextBlock { Text = value, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(dimValue ? Color.FromRgb(150, 157, 168) : Color.FromRgb(240, 243, 247)) });
        Grid.SetColumn(valuePanel, 1); row.Children.Add(valuePanel);
        return row;
    }

    private void AddGroupedPropertyRow(Panel? panel, string label, string value, Func<string, string?>? commit = null)
    {
        if (panel is null) return;
        // Identity-like fields (handle, ids) are read-only: show them dimmed like AutoCAD.
        var row = BuildPropertyRow(label, value, dimValue: label is "Handle" or "Object ID" or "Native object");
        if (commit is not null) MakeEditable(row, label, value, commit);
        panel.Children.Add(row);
    }

    private void AddPointProperties(string label, Point2 point)
    {
        AddPropertyRow(label + " X", point.X.ToString("0.###", CultureInfo.CurrentCulture));
        AddPropertyRow(label + " Y", point.Y.ToString("0.###", CultureInfo.CurrentCulture));
    }

    private void AddPropertyRow(string label, string value, CadColor? swatch = null, bool dimValue = false, Func<string, string?>? commit = null)
    {
        var row = BuildPropertyRow(label, value, swatch, dimValue);
        if (commit is not null) MakeEditable(row, label, value, commit);
        GeneralPropertiesRows.Children.Add(row);
    }

    private bool _updatingCadStyleUi;
    private static CadColor NamedCadColor(string value) => value switch
    {
        "Red" => new CadColor(255, 90, 90),
        "Yellow" => new CadColor(255, 220, 80),
        "Green" => new CadColor(95, 220, 125),
        "Cyan" => new CadColor(85, 215, 225),
        "Blue" => new CadColor(110, 140, 255),
        "White" => new CadColor(235, 235, 235),
        _ => new CadColor(0, 213, 159)
    };

    private static bool TryResolveCadColorValue(string? value, out CadColor color, out string displayText)
    {
        color = default;
        displayText = "ByLayer";
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "ByLayer", StringComparison.OrdinalIgnoreCase))
            return false;
        var t = value.Trim();
        switch (t)
        {
            case "Red": case "Yellow": case "Green": case "Cyan": case "Blue": case "White":
                color = NamedCadColor(t); displayText = t; return true;
        }
        if (t.StartsWith("#") && t.Length == 7)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(t);
                color = new CadColor(c.R, c.G, c.B);
                displayText = $"{c.R},{c.G},{c.B}";
                return true;
            }
            catch { }
        }
        var parts = t.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
        {
            color = new CadColor(r, g, b); displayText = $"{r},{g},{b}"; return true;
        }
        return false;
    }

    private static Brush CreateSwatchBrush(string key)
    {
        if (TryResolveCadColorValue(key, out var c, out _))
        {
            var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
            if (brush.CanFreeze) brush.Freeze();
            return brush;
        }
        var byLayerBrush = new SolidColorBrush(Color.FromRgb(76, 184, 188));
        if (byLayerBrush.CanFreeze) byLayerBrush.Freeze();
        return byLayerBrush;
    }

    private static CadColorOption CreateCadColorOption(string key)
    {
        if (TryResolveCadColorValue(key, out _, out var display)) return new CadColorOption(key, display, CreateSwatchBrush(key));
        return new CadColorOption("ByLayer", "ByLayer", CreateSwatchBrush("ByLayer"));
    }

    private static List<CadColorOption> BuildCadColorOptions(string currentValue)
    {
        var keys = new List<string> { "ByLayer", "Red", "Yellow", "Green", "Cyan", "Blue", "White" };
        if (!string.IsNullOrWhiteSpace(currentValue) && !keys.Contains(currentValue, StringComparer.OrdinalIgnoreCase)) keys.Add(currentValue);
        return keys.Select(CreateCadColorOption).ToList();
    }

    private void OpenCadColorManager()
    {
        var drawing = _editor.Drawing;
        if (!TryResolveCadColorValue(drawing.CurrentColor, out var initial, out _)) initial = new CadColor(255, 255, 255);
        var dialog = CreateCadChildWindow("Color — Create / Modify", 390, 410);
        var panel = new StackPanel { Margin = new Thickness(18) };
        var swatch = new Border { Height = 45, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.LightSlateGray,
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10) };
        var red = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true, Value = initial.R };
        var green = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true, Value = initial.G };
        var blue = new Slider { Minimum = 0, Maximum = 255, TickFrequency = 1, IsSnapToTickEnabled = true, Value = initial.B };
        var value = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(36, 45, 57)), FontSize = 12, Margin = new Thickness(0, 8, 0, 8) };
        void RefreshPreview()
        {
            var r = (byte)Math.Round(red.Value); var g = (byte)Math.Round(green.Value); var b = (byte)Math.Round(blue.Value);
            swatch.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
            value.Text = $"RGB  {r},{g},{b}     HEX  #{r:X2}{g:X2}{b:X2}";
        }
        void AddChannel(string caption, Slider slider)
        {
            panel.Children.Add(new TextBlock { Text = caption, Foreground = new SolidColorBrush(Color.FromRgb(36, 45, 57)), Margin = new Thickness(0, 5, 0, 2) });
            panel.Children.Add(slider);
            slider.ValueChanged += (_, _) => RefreshPreview();
        }
        panel.Children.Add(new TextBlock { Text = "Choose any RGB color", Foreground = new SolidColorBrush(Color.FromRgb(36, 45, 57)), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(swatch);
        AddChannel("Red", red); AddChannel("Green", green); AddChannel("Blue", blue);
        panel.Children.Add(value);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "ByLayer", MinWidth = 75, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "Apply", MinWidth = 75, Height = 30 };
        reset.Click += (_, _) => { drawing.CurrentColor = "ByLayer"; dialog.DialogResult = true; };
        apply.Click += (_, _) => { drawing.CurrentColor = $"{(byte)Math.Round(red.Value)},{(byte)Math.Round(green.Value)},{(byte)Math.Round(blue.Value)}"; dialog.DialogResult = true; };
        buttons.Children.Add(reset); buttons.Children.Add(apply); panel.Children.Add(buttons);
        dialog.Content = panel; EnableEscapeToClose(dialog); RefreshPreview();
        if (dialog.ShowDialog() == true) { RefreshCadStyles(); RenderPreview(); }
        DrawingCanvas.Focus();
    }

    private static void EnableEscapeToClose(Window dialog)
    {
        dialog.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape || args.Handled) return;
            args.Handled = true;
            dialog.Close();
        };
    }

    private Window CreateCadChildWindow(string title, double width, double height)
    {
        var dialog = new Window { Title = title, Owner = this, Width = width, Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White,
            Foreground = Brushes.Black, FontFamily = new FontFamily("Segoe UI") };
        ConfigureEditorDialog(dialog);
        EnableEscapeToClose(dialog);
        RememberChildWindow(dialog);
        return dialog;
    }

    private void RefreshCadStyles()
    {
        if (LayerSelector is null || _activeSession is null) return;
        _comboSyncKey = null;
        _updatingCadStyleUi = true;
        try
        {
            var d = _editor.Drawing;
            void Set(System.Windows.Controls.ComboBox box, IEnumerable<string> values, string selected)
            {
                box.ItemsSource = values.ToArray(); box.SelectedItem = selected;
            }
            Set(LayerSelector, new[] { "0" }.Concat(d.LayerColors.Keys).Concat(d.Entities.Select(e => e.Layer)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v), d.CurrentLayer);
            Set(TextStyleSelector, d.TextStyles.Keys.OrderBy(v=>v), d.CurrentTextStyle);
            Set(DimensionStyleSelector, d.DimensionStyles.Keys.OrderBy(v=>v), d.CurrentDimensionStyle);
            RefreshCadByLayerCombos();
        }
        finally { _updatingCadStyleUi = false; }
    }
    /// <summary>
    /// Color / Line Style combos: "ByLayer" shows the color and linetype of the layer in <paramref name="layerName"/> (default: current layer).
    /// With <paramref name="showSelection"/> the combos show the properties of the selected objects instead of the current settings.
    /// </summary>
    private void RefreshCadByLayerCombos(string? layerName = null, bool showSelection = false, string? colorKey = null, string? lineKey = null)
    {
        var d = _editor.Drawing;
        var wasUpdating = _updatingCadStyleUi; _updatingCadStyleUi = true;
        try
        {
            layerName ??= d.CurrentLayer;
            var layerColor = d.LayerColors.TryGetValue(layerName, out var lc) ? lc : new CadColor(255, 255, 255);
            var layerBrush = new SolidColorBrush(Color.FromRgb(layerColor.R, layerColor.G, layerColor.B)); layerBrush.Freeze();
            var wantedColor = showSelection ? colorKey : d.CurrentColor;
            var colors = BuildCadColorOptions(wantedColor ?? d.CurrentColor).Select(o => o.Key.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ? new CadColorOption("ByLayer", "ByLayer", layerBrush) : o).ToList();
            CadColorSelector.ItemsSource = colors;
            CadColorSelector.SelectedItem = wantedColor is null ? null
                : colors.FirstOrDefault(x => string.Equals(x.Key, wantedColor, StringComparison.OrdinalIgnoreCase)) ?? CreateCadColorOption(wantedColor);
            var layerLine = d.LayerStates.TryGetValue(layerName, out var ls) && !string.IsNullOrWhiteSpace(ls.LineType) ? ls.LineType : "Continuous";
            var lines = new[] { new CadLineStyleOption("ByLayer", $"ByLayer ({layerLine})") }
                .Concat(d.LineStyles.Keys.OrderBy(v => v).Select(v => new CadLineStyleOption(v, v))).ToList();
            CadLineStyleSelector.ItemsSource = lines;
            var wantedLine = showSelection ? lineKey : d.CurrentLineStyle;
            CadLineStyleSelector.SelectedItem = wantedLine is null ? null : lines.FirstOrDefault(x => string.Equals(x.Key, wantedLine, StringComparison.OrdinalIgnoreCase)) ?? lines[0];
        }
        finally { _updatingCadStyleUi = wasUpdating; }
    }

    private string? _comboSyncKey;

    /// <summary>
    /// Like AutoCAD: with objects selected the Layer / Color / Line Style combos show THEIR properties (blank when they differ);
    /// with nothing selected they show the current settings again.
    /// </summary>
    private void SyncStyleCombosToSelection()
    {
        if (LayerSelector is null || _activeSession is null || _updatingCadStyleUi) return;
        var d = _editor.Drawing;
        var selected = _selectedEntityIds.Count == 0 || _selectedEntityIds.Count > 2000 ? Array.Empty<CadEntity>()
            : _selectedEntityIds.Select(id => _spatialIndex.TryGetEntity(id, out var item) ? item : null).OfType<CadEntity>().ToArray();
        string ColorOf(CadEntity e) => e.ColorByBlock ? "ByBlock" : e.ExplicitColor is { } col ? $"{col.R},{col.G},{col.B}" : "ByLayer";
        var layers = selected.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var colorKeys = selected.Select(ColorOf).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var lineKeys = selected.Select(e => e.LineTypeName ?? "ByLayer").Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        var key = selected.Length == 0
            ? $"none|{d.CurrentLayer}|{d.CurrentColor}|{d.CurrentLineStyle}|{d.LayerColors.GetValueOrDefault(d.CurrentLayer)}"
            : $"{string.Join(",", layers)}|{string.Join(",", colorKeys)}|{string.Join(",", lineKeys)}|{(layers.Length == 1 ? d.LayerColors.GetValueOrDefault(layers[0]).ToString() : "")}";
        if (key == _comboSyncKey) return;
        _comboSyncKey = key;
        if (selected.Length == 0) { RefreshCadStyles(); _comboSyncKey = key; return; }
        _updatingCadStyleUi = true;
        try
        {
            if (layers.Length == 1)
            {
                if (LayerSelector.ItemsSource is IEnumerable<string> known && !known.Contains(layers[0], StringComparer.OrdinalIgnoreCase)) LayerSelector.ItemsSource = known.Append(layers[0]).ToArray();
                LayerSelector.SelectedItem = (LayerSelector.ItemsSource as IEnumerable<string>)?.FirstOrDefault(x => x.Equals(layers[0], StringComparison.OrdinalIgnoreCase));
            }
            else LayerSelector.SelectedItem = null;
        }
        finally { _updatingCadStyleUi = false; }
        RefreshCadByLayerCombos(layers.Length == 1 ? layers[0] : d.CurrentLayer, showSelection: true, colorKeys.Length == 1 ? colorKeys[0] : null, lineKeys.Length == 1 ? lineKeys[0] : null);
    }

    private bool ApplyStyleComboToSelection(System.Windows.Controls.ComboBox combo, CadEntity[] targets)
    {
        var d = _editor.Drawing;
        string? error; string what;
        if (ReferenceEquals(combo, CadColorSelector) && combo.SelectedItem is CadColorOption color) { what = $"color {color.DisplayText}"; error = EditColor(targets, color.Key); }
        else if (ReferenceEquals(combo, CadLineStyleSelector) && combo.SelectedItem is CadLineStyleOption line) { what = $"linetype {line.Key}"; error = EditLineType(targets, line.Key); }
        else if (ReferenceEquals(combo, LayerSelector) && combo.SelectedItem is string layer) { what = $"layer {layer}"; error = EditLayer(targets, layer); }
        else if (ReferenceEquals(combo, TextStyleSelector) && combo.SelectedItem is string textStyle && d.TextStyles.TryGetValue(textStyle, out var ts))
        {
            what = $"text style {textStyle}";
            if (!targets.Any(t => t is CadTextEntity or CadMTextEntity)) return false;
            error = ApplyPropertyEdits(targets, e => e is CadTextEntity or CadMTextEntity ? e with { TextFontFamily = ts.FontFamily } : e);
        }
        else if (ReferenceEquals(combo, DimensionStyleSelector) && combo.SelectedItem is string dimStyle && d.DimensionStyles.TryGetValue(dimStyle, out var ds))
        {
            what = $"dimension style {dimStyle}";
            if (!targets.Any(t => t is ProxyCadEntity { Dimension: not null })) return false;
            error = ApplyPropertyEdits(targets, e => e is ProxyCadEntity { Dimension: { } dm } px ? RecomposeDimension(px, dm with { Style = ds }) : e);
        }
        else return false;
        AppendCommandHistory(error is null ? $"Changed {what} for {targets.Length} selected object(s)." : $"Cannot change {what}: {error}");
        RefreshCadStyles(); RefreshPropertiesPalette(); RenderDrawing(false);
        DrawingCanvas.Focus();
        return true;
    }

    private void CadStyleSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingCadStyleUi || _activeSession is null || sender is not System.Windows.Controls.ComboBox combo) return;
        var d = _editor.Drawing;
        // AutoCAD: with objects selected (and no command running) these combos change the SELECTED OBJECTS, not the current settings.
        if (!IsDynamicCommandActive && _pendingSelectionAction == SelectionAction.None && _selectedEntityIds.Count > 0 && combo.SelectedItem is not null)
        {
            var targets = d.Entities.Where(item => _selectedEntityIds.Contains(item.Id)).ToArray();
            if (targets.Length > 0 && ApplyStyleComboToSelection(combo, targets)) return;
        }
        if (ReferenceEquals(combo, CadColorSelector))
        {
            if (combo.SelectedItem is CadColorOption colorOption) d.CurrentColor = colorOption.Key;
            else return;
        }
        else
        {
            if (ReferenceEquals(combo, CadLineStyleSelector)) { if (combo.SelectedItem is not CadLineStyleOption lineOption) return; d.CurrentLineStyle = lineOption.Key; RenderPreview(); DrawingCanvas.Focus(); return; }
            if (combo.SelectedItem is not string value) return;
            if (ReferenceEquals(combo, LayerSelector)) { d.CurrentLayer = value; RefreshCadByLayerCombos(); }
            else if (ReferenceEquals(combo, TextStyleSelector)) d.CurrentTextStyle = value;
            else if (ReferenceEquals(combo, DimensionStyleSelector)) d.CurrentDimensionStyle = value;
            else if (ReferenceEquals(combo, CadLineStyleSelector)) d.CurrentLineStyle = value;
        }
        RenderPreview(); DrawingCanvas.Focus();
    }
    private void CadComboManage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        // Button lives inside the ComboBox template, not in ItemsSource.
        // It is never a selectable style and cannot overwrite Current* settings.
        var combo = button.TemplatedParent as System.Windows.Controls.ComboBox;
        if (combo is null) return;
        combo.IsDropDownOpen = false;
        if (ReferenceEquals(combo, CadColorSelector))
        {
            OpenCadColorManager();
            return;
        }
        int category = ReferenceEquals(combo, TextStyleSelector) ? 1
            : ReferenceEquals(combo, DimensionStyleSelector) ? 2
            : ReferenceEquals(combo, CadLineStyleSelector) ? 3 : 0;
        OpenCadStyleManager(category);
        if (DrawingCanvas is not null) DrawingCanvas.Focus();
    }

    private void ManageCadStyles_Click(object sender, RoutedEventArgs e) => OpenCadStyleManager(0);

    private string FormatLayerColor(string layer) => _editor.Drawing.LayerColors.TryGetValue(layer, out var color)
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : "Default";

    private static string FormatEntityColor(CadEntity entity) => entity.ExplicitColor is { } color
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : entity.ColorByBlock ? "ByBlock" : "ByLayer";

    private static string GetEntityTypeName(CadEntity entity) => entity switch
    {
        CadTextEntity => "Text", CadMTextEntity => "MText",
        LineEntity => "Line", CircleEntity => "Circle", ArcEntity => "Arc", PolylineEntity => "Polyline",
        ProxyCadEntity proxy => proxy.NativeType, _ => entity.GetType().Name
    };

    private void EndLineCommand()
    {
        _previewTimer.Stop(); _previewUpdatePending = false;
        _editor.EndCommand();
        _transformActive = false;
        _transformBasePoint = null;
        _transformEntities = Array.Empty<CadEntity>();
        _rotateActive = false;
        _rotateBasePoint = null;
        _rotateEntities = Array.Empty<CadEntity>();
        _rotateCopy = false; _rotateReferenceStage = 0; _copyCount = 0;
        _polylineActive = false;
        _polygonRegionMode = false;
        _polylineVertices.Clear();
        _rectangleActive = false;
        _rectangleFirstCorner = null;
        _arcActive = false;
        _arcCenter = _arcStartPoint = null;
        _pendingSelectionAction = SelectionAction.None;
        UpdateDynamicInputMode();
        DrawingCanvas.Focus();
        RenderDrawing(fitToDrawing: false);
    }

    private void DrawingCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        _lastPointer = e.GetPosition(DrawingCanvas);
        if (_isPanning)
        {
            return;
        }
        if (_gripActive) { GripMouseMove(); return; }
        if (_pickTool == "ZOOMW" && _zoomWindowFirst is { } zoomFirst) { UpdateSelectionBox(zoomFirst, _lastPointer); return; }
        if (_objectDragActive)
        {
            var ddx = _lastPointer.X - _objectDragStart.X; var ddy = _lastPointer.Y - _objectDragStart.Y;
            if (!_objectDragMoved && Math.Abs(ddx) < 5 && Math.Abs(ddy) < 5) return;
            _objectDragMoved = true;
            SelectionOverlayImage.RenderTransform = new TranslateTransform(ddx, ddy);
            return;
        }
        if (_selectionDragActive)
        {
            _selectionDragThresholdMet |= Math.Abs(_lastPointer.X - _selectionDragStart.X) >= 5 || Math.Abs(_lastPointer.Y - _selectionDragStart.Y) >= 5;
            UpdateSelectionBox(_selectionDragStart, _lastPointer);
            return;
        }
        if (_windowSelectionAwaitingSecondPoint)
        {
            UpdateSelectionBox(_windowSelectionFirstPoint, _lastPointer);
            return;
        }
        if (_wheelDebounceTimer.IsEnabled) return;
        if (IsDynamicCommandActive)
        {
            // Keep the cursor-following prompt/DYN box glued to the pointer immediately (cheap Canvas.SetLeft/Top).
            // Only the heavy snap/preview work is coalesced into the timer tick, which can run late on a busy UI thread.
            PositionDynamicInput(_lastPointer);
            // Mouse input only records the latest point. One tick performs snap, prompt and preview work.
            _previewUpdatePending = true;
            if (!_previewTimer.IsEnabled) _previewTimer.Start();
        }
    }

    private void UpdateDrawingPreviewAtPointer()
    {
        _previewUpdatePending = false;
        if (_pickTool is not null && _pickTool != "ZOOMW")
        {
            // Pick tools (DIM*, TRIM, EXTEND, ID): snap, keep the guide at the cursor and redraw the rubber-band / provisional result.
            _editor.MovePointer(ConstrainPointer(ScreenToWorld(_lastPointer)));
            PositionDynamicInput(_lastPointer);
            RenderPreview();
            return;
        }
        if (!_editor.IsLineActive && !_editor.IsCircleActive && !_transformActive && !_rectangleActive && !_arcActive && !_polylineActive && !_rotateActive && !_mirrorPointActive && !_distancePointActive) return;
        var world = ConstrainPointer(ScreenToWorld(_lastPointer));
        _editor.MovePointer(world);
        DynamicPromptText.Text = _mirrorPointActive ? (_mirrorAxisStart is null ? "MIRROR: Pick axis first point:" : "MIRROR: Pick axis second point:")
            : _distancePointActive ? (_distanceStart is null ? "DIST: Pick first point:" : "DIST: Pick second point:")
            : _editor.IsCircleActive
            ? (_editor.CircleCenter is null ? "Specify center point:" : "Specify radius:")
            : _transformActive
                ? (_transformBasePoint is null ? (_transformDisplacement ? "Specify displacement <0,0>:" : "Specify base point or [Displacement]:") : "Specify second point or <use first point as displacement>:")
                : _rotateActive
                    ? (_rotateBasePoint is null ? "Specify rotation base point:" : _rotateReferenceStage == 1 ? "Specify the reference angle <0>:" : _rotateReferenceStage == 2 ? "Specify the new angle:" : "Specify rotation angle or [Copy/Reference]:")
                    : _polylineActive
                        ? (_polylineVertices.Count == 0 ? "Specify first polyline point:" : "Specify next polyline point:")
                        : _rectangleActive
                    ? (_rectangleFirstCorner is null ? (_rectanglePendingOption is null ? "Specify first corner point or [Chamfer/Fillet]:" : "Specify a distance:") : "Specify opposite corner:")
                    : _arcActive
                        ? (_arcCenter is null ? "Specify arc center:" : _arcStartPoint is null ? "Specify arc start point:" : "Specify arc end point:")
                        : (_editor.LineStart is null ? "Specify first point:" : "Specify next point:");
        PositionDynamicInput(_lastPointer);
        RenderPreview();
    }

    private long _lastLeftDownTick; private Point _lastLeftDownPoint;

    private void DrawingCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDynamicCommandActive && DynamicInputPanel.IsMouseOver) return;
        DrawingCanvas.Focus();
        _lastPointer = e.GetPosition(DrawingCanvas);
        if (_pickTool is not null) { HandlePickToolClick(_lastPointer); e.Handled = true; return; }
        if (e.ClickCount >= 2 && IsPaperSpaceActive && FindEntityAt(_lastPointer, recordSample: false) is null && TryEnterModelFromViewport(_lastPointer)) { e.Handled = true; return; }
        // Own double-click detection as well: ClickCount can restart when the first click re-laid out the UI or captured the mouse.
        var nowTick = Environment.TickCount64;
        var isDoubleClick = e.ClickCount >= 2 || (nowTick - _lastLeftDownTick < 450 && Math.Abs(_lastPointer.X - _lastLeftDownPoint.X) < 6 && Math.Abs(_lastPointer.Y - _lastLeftDownPoint.Y) < 6);
        _lastLeftDownTick = isDoubleClick ? 0 : nowTick; _lastLeftDownPoint = _lastPointer;
        if (isDoubleClick && !_editor.IsLineActive && !_editor.IsCircleActive && !_transformActive && !_rectangleActive && !_arcActive && !_polylineActive && !_rotateActive && !_mirrorPointActive && !_distancePointActive &&
            FindEditTarget(_lastPointer) is CadEntity editableText && (EditTextEntity(editableText) || EditInsertAttributes(editableText) || EditTableCell(editableText, _lastPointer)))
        {
            e.Handled = true;
            return;
        }
        var world = ConstrainPointer(ScreenToWorld(_lastPointer));
        if (_activeOsnap is { } acquiredSnap && StatusOtrackToggle.IsChecked == true)
        {
            if (_trackingAnchors.Count >= 32) _trackingAnchors.RemoveAt(0);
            if (!_trackingAnchors.Any(point => Point2.Distance(point, acquiredSnap.Point) < 1e-8))
                _trackingAnchors.Add(acquiredSnap.Point);
        }
        if (_mirrorPointActive)
        {
            if (_mirrorAxisStart is null) { _mirrorAxisStart = world; DynamicPromptText.Text = "MIRROR: Pick axis second point:"; AppendCommandHistory($"MIRROR axis first: {world.X:0.###},{world.Y:0.###}"); }
            else CompleteMirrorByPickedAxis(_mirrorAxisStart.Value, world);
            _editor.MovePointer(world); RenderPreview(); e.Handled = true; return;
        }
        if (_distancePointActive)
        {
            if (_distanceStart is null) { _distanceStart = world; DynamicPromptText.Text = "DIST: Pick second point:"; AppendCommandHistory($"DIST first: {world.X:0.###},{world.Y:0.###}"); }
            else CompleteDistanceByPickedPoints(_distanceStart.Value, world);
            _editor.MovePointer(world); RenderPreview(); e.Handled = true; return;
        }
        if (_rotateActive)
        {
            if (_rotateBasePoint is null)
            {
                _rotateBasePoint = world;
                _editor.MovePointer(world);
                DynamicPromptText.Text = "Specify rotation angle or [Copy/Reference]:";
                AppendCommandHistory("Specify rotation angle or [Copy/Reference]:");
                RenderPreview();
            }
            else CompleteRotateFromPoint(world);
            e.Handled = true;
            return;
        }
        if (_polylineActive)
        {
            AddPolylineVertex(world);
            DynamicPromptText.Text = "Specify next polyline point or Enter to finish:";
            PositionDynamicInput(_lastPointer);
            FocusDynamicInput();
            e.Handled = true;
            return;
        }
        if (_transformActive)
        {
            if (_transformBasePoint is null)
            {
                _transformBasePoint = world;
                DynamicPromptText.Text = "Specify second point or <use first point as displacement>:";
                AppendCommandHistory("Specify second point or <use first point as displacement>:");
            }
            else CompleteMove(world);
            e.Handled = true;
            return;
        }
        if (_rectangleActive)
        {
            if (_rectangleFirstCorner is null)
            {
                _rectangleFirstCorner = world;
                DynamicPromptText.Text = "Specify opposite corner:";
                AppendCommandHistory("Specify opposite corner:");
            }
            else AddRectangle(world);
            DynamicValueTextBox.Clear();
            FocusDynamicInput();
            e.Handled = true;
            return;
        }
        if (_arcActive)
        {
            AddArcPoint(world);
            DynamicValueTextBox.Clear();
            FocusDynamicInput();
            e.Handled = true;
            return;
        }
        if (_editor.IsLineActive)
        {
            _editor.AddLinePoint(world);
            DynamicPromptText.Text = "Specify next point:";
        }
        else if (_editor.IsCircleActive && _editor.CircleCenter is null)
        {
            _editor.AddCircleCenter(world);
            AppendCommandHistory($"Center: {world.X:0.###}, {world.Y:0.###}");
            DynamicPromptText.Text = "Specify radius or diameter:";
        }
        else if (_editor.IsCircleActive)
        {
            var radius = Point2.Distance(_editor.CircleCenter.Value, world);
            _editor.AddCircleRadius(radius);
            AppendCommandHistory($"Radius: {radius:0.###}");
            UpdateDynamicInputMode();
        }
        else
        {
            if (_windowSelectionAwaitingSecondPoint)
            {
                CompleteWindowSelection(_windowSelectionFirstPoint, _lastPointer, _selectionDragAdds);
                DrawingCanvas.Focus();
                e.Handled = true;
                return;
            }
            if (TryHandleGripPress(e)) { e.Handled = true; return; }
            // Press on an already selected object and drag: move the selection (ghost preview, applied on release).
            if (_pendingSelectionAction == SelectionAction.None && _selectedEntityIds.Count > 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
                FindEntityAt(_lastPointer, recordSample: false) is { } pressed && _selectedEntityIds.Contains(pressed.Id))
            {
                _objectDragActive = true; _objectDragMoved = false; _objectDragStart = _lastPointer;
                DrawingCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }
            _selectionDragActive = true;
            _selectionDragThresholdMet = false;
            _selectionDragAdds = _pendingSelectionAction != SelectionAction.None || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            _selectionDragStart = _lastPointer;
            _lastInteractionRenderTimestamp = 0;
            SelectionDragOverlay.Width = SelectionDragOverlay.Height = 0;
            SelectionDragOverlay.Visibility = Visibility.Visible;
            DrawingCanvas.CaptureMouse();
            e.Handled = true;
            return;
        }
        DynamicValueTextBox.Clear();
        FocusDynamicInput();
        e.Handled = true;
    }

    private void DrawingCanvas_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CommandExpandedPanel.Visibility == Visibility.Visible) SetCommandHistoryExpanded(false);
    }

    /// <summary>Object a double-click should edit: the picked object; for a block with attributes the click may land in empty space inside the already-selected block,
    /// so the single selected block whose box contains the point counts too.</summary>
    private CadEntity? FindEditTarget(Point screenPoint)
    {
        var hit = FindEntityAt(screenPoint);
        if (hit is ProxyCadEntity { InsertData.Attributes.Count: > 0 } || hit is CadTextEntity or CadMTextEntity) return hit;
        var world = ScreenToWorld(screenPoint);
        var pad = 6 / Math.Max(_viewScale, 1e-9);
        foreach (var id in _selectedEntityIds.Take(2).ToArray())
        {
            if (_editor.Drawing.AllSpaceEntities.FirstOrDefault(x => x.Id == id) is ProxyCadEntity { InsertData.Attributes.Count: > 0, HasBounds: true } block &&
                world.X >= Math.Min(block.BoundsMin.X, block.BoundsMax.X) - pad && world.X <= Math.Max(block.BoundsMin.X, block.BoundsMax.X) + pad &&
                world.Y >= Math.Min(block.BoundsMin.Y, block.BoundsMax.Y) - pad && world.Y <= Math.Max(block.BoundsMin.Y, block.BoundsMax.Y) + pad) return block;
        }
        if (hit is ProxyCadEntity { NativeType: var nativeType, InsertData: not null } plain && string.Equals(nativeType, "INSERT", StringComparison.OrdinalIgnoreCase))
            AppendCommandHistory($"Block {plain.InsertData!.BlockName} has no ATTRIB values to edit (attribute definitions in the block: {plain.InsertData.AttributeDefinitions}). Its texts are plain TEXT/MTEXT of the block definition.");
        return hit;
    }

    private static readonly (string Name, int H, int V)[] AttributeJustifications =
    {
        ("Left", 0, 0), ("Center", 1, 0), ("Right", 2, 0), ("Aligned", 3, 0), ("Middle", 4, 0), ("Fit", 5, 0),
        ("Top left", 0, 3), ("Top center", 1, 3), ("Top right", 2, 3),
        ("Middle left", 0, 2), ("Middle center", 1, 2), ("Middle right", 2, 2),
        ("Bottom left", 0, 1), ("Bottom center", 1, 1), ("Bottom right", 2, 1)
    };

    private static int AttributeAttachment(int horizontal, int vertical)
    {
        var column = horizontal is 1 or 4 ? 1 : horizontal == 2 ? 2 : 0;
        var row = horizontal == 4 ? 1 : vertical switch { 3 => 0, 2 => 1, _ => 2 };
        return row * 3 + column + 1;
    }

    /// <summary>Double-click on a block reference that carries attributes: AutoCAD-style Enhanced Attribute Editor
    /// (Attribute / Text Options / Properties tabs, one list row per ATTRIB).</summary>
    private bool EditInsertAttributes(CadEntity entity)
    {
        if (entity is not ProxyCadEntity { InsertData: { Attributes: { Count: > 0 } firstAttributes } firstInsertData } firstProxy) return false;
        var proxy = firstProxy; var insertData = firstInsertData;
        IReadOnlyList<CadInsertAttribute> baseline = firstAttributes;
        var current = firstAttributes.ToArray();
        var drawing = _editor.Drawing;
        var dialog = CreateCadChildWindow("Enhanced Attribute Editor", 540, 640);
        dialog.ResizeMode = ResizeMode.CanResize; dialog.MinWidth = 500; dialog.MinHeight = 560;
        var selected = -1; var loading = false; var dirty = false;
        var root = new DockPanel { Margin = new Thickness(14) };

        // header
        var header = new TextBlock { Margin = new Thickness(0, 0, 0, 8), LineHeight = 20 };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        // buttons
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Button Btn(string text, bool def = false, bool cancel = false) => new() { Content = text, Width = 84, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 4, 8, 4), IsDefault = def, IsCancel = cancel };
        var apply = Btn("Apply"); var ok = Btn("OK", def: true); var cancelButton = Btn("Cancel", cancel: true);
        buttons.Children.Add(apply); buttons.Children.Add(ok); buttons.Children.Add(cancelButton);
        DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);

        // attribute list (Tag / Prompt / Value)
        var list = new ListView { Height = 130, Margin = new Thickness(0, 0, 0, 10) };
        var grid = new GridView();
        grid.Columns.Add(new GridViewColumn { Header = "Tag", Width = 120, DisplayMemberBinding = new System.Windows.Data.Binding("Tag") });
        grid.Columns.Add(new GridViewColumn { Header = "Prompt", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("Prompt") });
        grid.Columns.Add(new GridViewColumn { Header = "Value", Width = 210, DisplayMemberBinding = new System.Windows.Data.Binding("Value") });
        list.View = grid;
        DockPanel.SetDock(list, Dock.Top); root.Children.Add(list);

        // controls
        TextBox Box(double width = 160) => new() { Width = width, Height = 24, VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.White, Foreground = Brushes.Black };
        ComboBox Combo(IEnumerable<string> items, double width = 190, bool editable = false) => new() { ItemsSource = items.ToArray(), Width = width, Height = 24, IsEditable = editable, VerticalContentAlignment = VerticalAlignment.Center };
        UIElement Field(string label, FrameworkElement control, double labelWidth = 100)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            row.Children.Add(new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(control); return row;
        }
        var valueBox = Box(380);
        var tagText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var promptText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
        var styleCombo = Combo(drawing.TextStyles.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), 300);
        var justification = Combo(AttributeJustifications.Select(j => j.Name), 150);
        var backwards = new CheckBox { Content = "Backwards", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var upsideDown = new CheckBox { Content = "Upside down", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        var heightBox = Box(110); var widthBox = Box(110); var rotationBox = Box(110); var obliqueBox = Box(110);
        var annotative = new CheckBox { Content = "Annotative", IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        var boundary = Box(110); boundary.IsEnabled = false;
        var layerNames = drawing.LayerStates.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var layerCombo = Combo(layerNames, 300);
        var lineTypeCombo = Combo(new[] { "ByLayer", "ByBlock" }.Concat(_editor.Drawing.LineStyles.Keys.Where(n => !n.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) && !n.Equals("ByBlock", StringComparison.OrdinalIgnoreCase))), 300);
        var colorCombo = Combo(new[] { "ByLayer", "ByBlock", "Red", "Yellow", "Green", "Cyan", "Blue", "Magenta", "White" }, 300, editable: true);
        var weightCombo = Combo(new[] { "ByLayer", "ByBlock", "Default", "0.05", "0.09", "0.13", "0.15", "0.18", "0.20", "0.25", "0.30", "0.35", "0.40", "0.50", "0.53", "0.60", "0.70", "0.80", "0.90", "1", "1.06", "1.2", "1.4", "1.58", "2", "2.11" }, 300, editable: true);
        var plotStyle = Combo(new[] { "ByColor" }, 300); plotStyle.SelectedIndex = 0; plotStyle.IsEnabled = false;

        // tabs
        var tabs = new TabControl { Margin = new Thickness(0, 0, 0, 0) };
        var tabAttribute = new StackPanel { Margin = new Thickness(12) };
        tabAttribute.Children.Add(Field("Tag:", tagText)); tabAttribute.Children.Add(Field("Prompt:", promptText)); tabAttribute.Children.Add(Field("Value:", valueBox));
        var tabText = new StackPanel { Margin = new Thickness(12) };
        tabText.Children.Add(Field("Text style:", styleCombo));
        var justRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
        justRow.Children.Add(new TextBlock { Text = "Justification:", Width = 100, VerticalAlignment = VerticalAlignment.Center }); justRow.Children.Add(justification); justRow.Children.Add(backwards); justRow.Children.Add(upsideDown);
        tabText.Children.Add(justRow);
        var metrics = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel(); var right = new StackPanel(); Grid.SetColumn(right, 1);
        left.Children.Add(Field("Height:", heightBox)); left.Children.Add(Field("Rotation:", rotationBox));
        right.Children.Add(Field("Width factor:", widthBox)); right.Children.Add(Field("Oblique angle:", obliqueBox));
        metrics.Children.Add(left); metrics.Children.Add(right); tabText.Children.Add(metrics);
        var annoRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(100, 4, 0, 0) };
        annoRow.Children.Add(annotative); annoRow.Children.Add(new TextBlock { Text = "Boundary width:", Margin = new Thickness(40, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray }); annoRow.Children.Add(boundary);
        tabText.Children.Add(annoRow);
        var tabProps = new StackPanel { Margin = new Thickness(12) };
        tabProps.Children.Add(Field("Layer:", layerCombo)); tabProps.Children.Add(Field("Linetype:", lineTypeCombo)); tabProps.Children.Add(Field("Color:", colorCombo));
        tabProps.Children.Add(Field("Lineweight:", weightCombo)); tabProps.Children.Add(Field("Plot style:", plotStyle));
        tabs.Items.Add(new TabItem { Header = "Attribute", Content = tabAttribute });
        tabs.Items.Add(new TabItem { Header = "Text Options", Content = tabText });
        tabs.Items.Add(new TabItem { Header = "Properties", Content = tabProps });
        root.Children.Add(tabs);

        // state <-> controls
        void RefreshList()
        {
            var keep = list.SelectedIndex;
            loading = true;
            list.Items.Clear();
            foreach (var a in current) list.Items.Add(new { a.Tag, Prompt = a.Prompt ?? string.Empty, a.Value });
            list.SelectedIndex = keep >= 0 ? keep : 0;
            loading = false;
        }
        void SetDirty(bool value) { dirty = value; apply.IsEnabled = value; }
        void Load(int index)
        {
            if (index < 0 || index >= current.Length) return;
            loading = true;
            var a = current[index]; selected = index;
            header.Text = $"Block: {insertData.BlockName}\nTag: {a.Tag}";
            tagText.Text = a.Tag; promptText.Text = a.Prompt ?? string.Empty; valueBox.Text = a.Value;
            styleCombo.Text = a.TextStyle ?? string.Empty;
            var j = Array.FindIndex(AttributeJustifications, x => x.H == a.HAlign && x.V == a.VAlign); justification.SelectedIndex = j < 0 ? 0 : j;
            backwards.IsChecked = a.Backwards; upsideDown.IsChecked = a.UpsideDown;
            heightBox.Text = D(a.Height); widthBox.Text = D(a.WidthFactor); rotationBox.Text = D(a.RotationDegrees); obliqueBox.Text = D(a.ObliqueDegrees);
            layerCombo.Text = a.Layer ?? string.Empty; lineTypeCombo.Text = a.LineType ?? "ByLayer"; colorCombo.Text = a.Color ?? "ByLayer"; weightCombo.Text = a.LineWeight ?? "ByLayer";
            var detailed = a.Detailed;
            foreach (var control in new Control[] { styleCombo, justification, backwards, upsideDown, heightBox, widthBox, rotationBox, obliqueBox, layerCombo, lineTypeCombo, colorCombo, weightCombo })
                control.IsEnabled = detailed;
            loading = false;
        }
        void Commit()
        {
            if (selected < 0 || selected >= current.Length) return;
            var a = current[selected];
            var j = AttributeJustifications[Math.Max(0, justification.SelectedIndex)];
            double Num(TextBox box, double fallback) => TryD(box.Text, out var v) ? v : fallback;
            var updated = a with
            {
                Value = valueBox.Text ?? string.Empty,
                TextStyle = string.IsNullOrWhiteSpace(styleCombo.Text) ? a.TextStyle : styleCombo.Text.Trim(),
                HAlign = a.Detailed ? j.H : a.HAlign, VAlign = a.Detailed ? j.V : a.VAlign,
                Backwards = backwards.IsChecked == true, UpsideDown = upsideDown.IsChecked == true,
                Height = Math.Max(0, Num(heightBox, a.Height)), WidthFactor = Math.Max(0.0001, Num(widthBox, a.WidthFactor)),
                RotationDegrees = Num(rotationBox, a.RotationDegrees), ObliqueDegrees = Math.Clamp(Num(obliqueBox, a.ObliqueDegrees), -85, 85),
                Layer = string.IsNullOrWhiteSpace(layerCombo.Text) ? a.Layer : layerCombo.Text.Trim(),
                LineType = string.IsNullOrWhiteSpace(lineTypeCombo.Text) ? a.LineType : lineTypeCombo.Text.Trim(),
                Color = string.IsNullOrWhiteSpace(colorCombo.Text) ? a.Color : colorCombo.Text.Trim(),
                LineWeight = string.IsNullOrWhiteSpace(weightCombo.Text) ? a.LineWeight : weightCombo.Text.Trim()
            };
            current[selected] = updated;
        }
        void Touch() { if (!loading) { SetDirty(true); } }
        list.SelectionChanged += (_, _) => { if (loading) return; Commit(); Load(list.SelectedIndex); };
        foreach (var box in new[] { valueBox, heightBox, widthBox, rotationBox, obliqueBox }) box.TextChanged += (_, _) => Touch();
        foreach (var combo in new[] { styleCombo, justification, layerCombo, lineTypeCombo, colorCombo, weightCombo })
        { combo.SelectionChanged += (_, _) => Touch(); if (combo.IsEditable) combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => Touch())); }
        foreach (var check in new[] { backwards, upsideDown }) { check.Checked += (_, _) => Touch(); check.Unchecked += (_, _) => Touch(); }

        bool ApplyChanges()
        {
            Commit();
            var changed = false;
            for (var i = 0; i < current.Length; i++) if (current[i] != baseline[i]) { changed = true; break; }
            if (!changed) { SetDirty(false); return false; }
            var texts = proxy.ProxyGraphicsTexts?.ToList();
            if (texts is not null)
                for (var i = 0; i < texts.Count; i++)
                {
                    if (texts[i].Tag is not { } tag) continue;
                    var index = Array.FindIndex(current, a => string.Equals(a.Tag, tag, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) continue;
                    var before = baseline[index]; var after = current[index];
                    var t = texts[i] with { Text = after.Value, Formatted = texts[i].IsMText ? after.Value : texts[i].Formatted };
                    if (after.Detailed && before.Detailed)
                    {
                        if (before.Height > 1e-12 && after.Height > 0) t = t with { Height = t.Height * after.Height / before.Height };
                        if (before.WidthFactor > 1e-12) t = t with { WidthFactor = Math.Clamp(t.WidthFactor * after.WidthFactor / before.WidthFactor, 0.05, 10) };
                        t = t with { RotationRadians = t.RotationRadians + (after.RotationDegrees - before.RotationDegrees) * Math.PI / 180.0 };
                        if (Math.Abs(after.ObliqueDegrees - before.ObliqueDegrees) > 1e-9) t = t with { Oblique = after.ObliqueDegrees * Math.PI / 180.0 };
                        if (after.HAlign != before.HAlign || after.VAlign != before.VAlign) t = t with { HAlign = after.HAlign, VAlign = after.VAlign, AttachmentPoint = AttributeAttachment(after.HAlign, after.VAlign) };
                        if (!string.Equals(after.TextStyle, before.TextStyle, StringComparison.OrdinalIgnoreCase) && after.TextStyle is { } styleName && drawing.TextStyles.TryGetValue(styleName, out var styleDefinition) && !string.IsNullOrWhiteSpace(styleDefinition.FontFamily))
                            t = t with { FontFamily = styleDefinition.FontFamily };
                    }
                    texts[i] = t;
                }
            var snapshot = current.ToArray();
            var updatedEntity = proxy with { InsertData = insertData with { Attributes = snapshot }, ProxyGraphicsTexts = texts };
            _editor.ApplyEntityChanges([proxy], [updatedEntity]);
            _selectedEntityIds.Clear(); _selectedEntityIds.Add(updatedEntity.Id); RefreshPropertiesPalette();
            AppendCommandHistory($"Updated attributes of {insertData.BlockName}.");
            proxy = updatedEntity; insertData = updatedEntity.InsertData ?? insertData; baseline = snapshot;
            RefreshList(); SetDirty(false);
            return true;
        }
        apply.Click += (_, _) => ApplyChanges();
        ok.Click += (_, _) => { ApplyChanges(); dialog.DialogResult = true; };

        dialog.Content = root;
        ConfigureEditorDialog(dialog);
        RefreshList(); Load(0); SetDirty(false);
        dialog.ShowDialog();
        return true;
    }

    /// <summary>TEXTEDIT (ED): the same edit dialog as double-clicking a TEXT/MTEXT, reachable by command name too.
    /// Uses the current selection when it is exactly one text object; otherwise asks to click one, repeating until Esc.</summary>
    private void BeginTextEditCommand()
    {
        if (_selectedEntityIds.Count == 1 && _spatialIndex.TryGetEntity(_selectedEntityIds.First(), out var selected) && selected is CadTextEntity or CadMTextEntity)
        { EditTextEntity(selected); return; }
        BeginPickTool("TEXTEDIT", "TEXTEDIT: Select an annotation object:");
    }

    private bool EditTextEntity(CadEntity entity)
    {
        if (entity is not (CadTextEntity or CadMTextEntity)) return false;
        var isMText = entity is CadMTextEntity;
        var currentValue = entity switch { CadTextEntity text => text.Value, CadMTextEntity text => text.Value, _ => string.Empty };
        var currentHeight = entity switch { CadTextEntity text => text.Height, CadMTextEntity text => text.Height, _ => 1 };
        var currentRotation = entity switch { CadTextEntity text => text.RotationRadians * 180 / Math.PI, CadMTextEntity text => text.RotationRadians * 180 / Math.PI, _ => 0 };
        var currentWidth = entity is CadMTextEntity mtext ? mtext.Width : 0;
        var currentLayer = entity.Layer;
        var currentLayerColor = _editor.Drawing.LayerColors.TryGetValue(currentLayer, out var layerColor) ? layerColor : entity.DisplayColor ?? new CadColor(255, 255, 255);
        var dialog = CreateCadChildWindow(isMText ? "Edit MText" : "Edit Text", 460, isMText ? 500 : 455);
        dialog.ResizeMode = ResizeMode.CanResize;
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Text content", Margin = new Thickness(0, 0, 0, 5) });
        var content = new TextBox { Text = currentValue, AcceptsReturn = isMText, TextWrapping = TextWrapping.Wrap,
            MinHeight = isMText ? 105 : 55, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.White, Foreground = Brushes.Black };
        panel.Children.Add(content);
        var height = AddNumericEditor(panel, "Text height", currentHeight);
        var rotation = AddNumericEditor(panel, "Rotation (degrees)", currentRotation);
        TextBox? width = isMText ? AddNumericEditor(panel, "Reference width (0 = unlimited)", currentWidth) : null;
        var layer = AddTextEditor(panel, "Layer", currentLayer);
        var colorValue = entity.ExplicitColor is { } explicitColor ? $"#{explicitColor.R:X2}{explicitColor.G:X2}{explicitColor.B:X2}" : "ByLayer";
        var color = AddTextEditor(panel, "Object color (ByLayer or #RRGGBB)", colorValue);
        var layerColorBox = AddTextEditor(panel, "Layer color (#RRGGBB)", $"#{currentLayerColor.R:X2}{currentLayerColor.G:X2}{currentLayerColor.B:X2}");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Apply", Width = 82, Margin = new Thickness(0, 0, 8, 0), IsDefault = true, Padding = new Thickness(8, 4, 8, 4) };
        var cancel = new Button { Content = "Cancel", Width = 82, IsCancel = true, Padding = new Thickness(8, 4, 8, 4) };
        save.Click += (_, _) =>
        {
            if (!TryReadFinite(height.Text, out var h) || h <= 0 || !TryReadFinite(rotation.Text, out var degrees) ||
                (width is not null && (!TryReadFinite(width.Text, out var w) || w < 0)) ||
                string.IsNullOrWhiteSpace(layer.Text) || !TryParseCadColor(color.Text, out var objectColor) ||
                !TryParseCadColor(layerColorBox.Text, out var newLayerColor) || newLayerColor is null)
            {
                MessageBox.Show(dialog, "Enter a positive height, valid numeric values, a layer name, and colors as ByLayer or #RRGGBB.", "Invalid text properties", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var targetLayer = layer.Text.Trim();
            _cadFiles.SetLayerColor(_editor.Drawing, targetLayer, newLayerColor.Value);
            CadEntity updated = entity switch
            {
                CadTextEntity text => text with { Value = content.Text, Height = h, RotationRadians = degrees * Math.PI / 180,
                    Layer = targetLayer, ExplicitColor = objectColor, DisplayColor = objectColor ?? newLayerColor.Value },
                CadMTextEntity text => text with { Value = content.Text.Replace("\r\n", "\n"), FormattedValue = content.Text.Replace("\r\n", "\\P").Replace("\n", "\\P"), Height = h, RotationRadians = degrees * Math.PI / 180, Width = double.Parse(width!.Text, CultureInfo.CurrentCulture),
                    Layer = targetLayer, ExplicitColor = objectColor, DisplayColor = objectColor ?? newLayerColor.Value },
                _ => entity
            };
            _editor.ApplyEntityChanges([entity], [updated]);
            _selectedEntityIds.Clear(); _selectedEntityIds.Add(updated.Id); RefreshPropertiesPalette();
            AppendCommandHistory($"Updated {GetEntityTypeName(updated)}.");
            dialog.DialogResult = true;
        };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons); dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.MinWidth = 420; dialog.MinHeight = 360; dialog.Height = isMText ? 650 : 600;
        ConfigureEditorDialog(dialog);
        dialog.ShowDialog();
        return true;
    }

    private static void ConfigureEditorDialog(Window dialog)
    {
        var ink = Brushes.Black;
        var panelBrush = Brushes.White;
        var hoverBrush = new SolidColorBrush(Color.FromRgb(232, 238, 246)); hoverBrush.Freeze();
        var borderBrush = new SolidColorBrush(Color.FromRgb(198, 205, 214)); borderBrush.Freeze();
        var subtleBrush = new SolidColorBrush(Color.FromRgb(245, 246, 248)); subtleBrush.Freeze();
        dialog.Background = panelBrush;
        dialog.Foreground = ink; dialog.FontSize = 12;
        DialogTheme.SetIsLight(dialog, true);

        var label = new Style(typeof(TextBlock));
        label.Setters.Add(new Setter(TextBlock.ForegroundProperty, ink));
        label.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12d));
        dialog.Resources[typeof(TextBlock)] = label;

        var textBox = new Style(typeof(TextBox));
        textBox.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        textBox.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        textBox.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        textBox.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        textBox.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 4, 7, 4)));
        textBox.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        textBox.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Control.BackgroundProperty, hoverBrush) } });
        dialog.Resources[typeof(TextBox)] = textBox;

        var combo = new Style(typeof(ComboBox));
        combo.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        combo.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        combo.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        combo.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        combo.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 2, 6, 2)));
        combo.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        combo.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Control.BackgroundProperty, hoverBrush) } });
        dialog.Resources[typeof(ComboBox)] = combo;

        var comboItem = new Style(typeof(ComboBoxItem));
        comboItem.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        comboItem.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
        comboItem.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true,
            Setters = { new Setter(Control.BackgroundProperty, hoverBrush), new Setter(Control.ForegroundProperty, Brushes.Black) } });
        dialog.Resources[typeof(ComboBoxItem)] = comboItem;

        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, subtleBrush));
        button.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 4, 12, 4)));
        button.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        button.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Control.BackgroundProperty, hoverBrush) } });
        dialog.Resources[typeof(Button)] = button;

        var check = new Style(typeof(CheckBox));
        check.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        check.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        dialog.Resources[typeof(CheckBox)] = check;

        var radio = new Style(typeof(RadioButton));
        radio.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        radio.Setters.Add(new Setter(Control.FontSizeProperty, 12d));
        dialog.Resources[typeof(RadioButton)] = radio;

        var group = new Style(typeof(GroupBox));
        group.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        group.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
        group.Setters.Add(new Setter(Control.BackgroundProperty, panelBrush));
        dialog.Resources[typeof(GroupBox)] = group;

        foreach (var type in new[] { typeof(ListBox), typeof(ListView), typeof(TabControl), typeof(DataGrid), typeof(TreeView) })
        {
            var st = new Style(type);
            st.Setters.Add(new Setter(Control.ForegroundProperty, ink));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            st.Setters.Add(new Setter(Control.BorderBrushProperty, borderBrush));
            dialog.Resources[type] = st;
        }
        foreach (var type in new[] { typeof(DataGridCell), typeof(DataGridRow), typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), typeof(ListBoxItem), typeof(ListViewItem), typeof(TreeViewItem), typeof(MenuItem), typeof(Label) })
        {
            var st = new Style(type);
            st.Setters.Add(new Setter(Control.ForegroundProperty, ink));
            dialog.Resources[type] = st;
        }
        var tabItem = new Style(typeof(TabItem));
        tabItem.Setters.Add(new Setter(Control.ForegroundProperty, ink));
        dialog.Resources[typeof(TabItem)] = tabItem;
    }

    private static TextBox AddTextEditor(Panel parent, string label, string value)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var box = new TextBox { Text = value, Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.White, Foreground = Brushes.Black };
        parent.Children.Add(box);
        return box;
    }

    private static bool TryParseCadColor(string value, out CadColor? result)
    {
        result = null;
        if (value.Trim().Equals("ByLayer", StringComparison.OrdinalIgnoreCase)) return true;
        var hex = value.Trim();
        if (hex.Length != 7 || hex[0] != '#' || !byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return false;
        result = new CadColor(r, g, b);
        return true;
    }

    private static TextBox AddNumericEditor(Panel parent, string label, double value)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 3) });
        var box = new TextBox { Text = value.ToString("0.###", CultureInfo.CurrentCulture), Width = 180, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.White, Foreground = Brushes.Black };
        parent.Children.Add(box);
        return box;
    }

    private static bool TryReadFinite(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) && double.IsFinite(result);

    private void DrawingCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gripActive && TryHandleGripRelease()) { e.Handled = true; return; }
        if (_objectDragActive)
        {
            _objectDragActive = false;
            DrawingCanvas.ReleaseMouseCapture();
            SelectionOverlayImage.RenderTransform = Transform.Identity;
            _lastPointer = e.GetPosition(DrawingCanvas);
            if (_objectDragMoved)
            {
                var offset = new Point2((_lastPointer.X - _objectDragStart.X) / _viewScale, -(_lastPointer.Y - _objectDragStart.Y) / _viewScale);
                _transformEntities = _editor.Drawing.Entities.Where(entity => _selectedEntityIds.Contains(entity.Id)).ToArray();
                _transformCopy = false; _stretchActive = false; _transformActive = true; _transformBasePoint = new Point2(0, 0);
                CompleteMove(offset);
                _lastSelectionOverlayKey = null; RenderSelectionOverlay();
            }
            else if (FindEntityAt(_objectDragStart, recordSample: false) is { } clicked) SelectAt(clicked, false);
            e.Handled = true;
            return;
        }
        if (!_selectionDragActive) return;
        _lastPointer = e.GetPosition(DrawingCanvas);
        _selectionDragActive = false;
        DrawingCanvas.ReleaseMouseCapture();
        if (!_selectionDragThresholdMet)
        {
            // A completed two-click window must not begin a new selection on mouse-up.
            if (_windowSelectionAwaitingSecondPoint) { e.Handled = true; return; }
            var hit = FindEntityAt(_selectionDragStart);
            if (hit is not null)
            {
                SelectionDragOverlay.Visibility = Visibility.Collapsed;
                SelectAt(hit, _selectionDragAdds);
            }
            else
            {
                _windowSelectionAwaitingSecondPoint = true;
                _windowSelectionFirstPoint = _selectionDragStart;
                _lastInteractionRenderTimestamp = 0;
                SelectionDragOverlay.Visibility = Visibility.Visible;
                UpdateSelectionBox(_windowSelectionFirstPoint, _windowSelectionFirstPoint);
                AppendCommandHistory("Specify opposite corner of selection window, or press Esc to cancel:");
            }
            e.Handled = true;
            return;
        }
        SelectionDragOverlay.Visibility = Visibility.Collapsed;
        CompleteWindowSelection(_selectionDragStart, _lastPointer, _selectionDragAdds);
        e.Handled = true;
    }

    private void UpdateSelectionBox(Point first, Point second)
    {
        Canvas.SetLeft(SelectionDragOverlay, Math.Min(first.X, second.X));
        Canvas.SetTop(SelectionDragOverlay, Math.Min(first.Y, second.Y));
        SelectionDragOverlay.Width = Math.Max(1, Math.Abs(second.X - first.X));
        SelectionDragOverlay.Height = Math.Max(1, Math.Abs(second.Y - first.Y));
        var windowSelection = second.X >= first.X;
        SelectionDragOverlay.Fill = new SolidColorBrush(Color.FromArgb(36, 174, 180, 190));
        SelectionDragOverlay.Stroke = new SolidColorBrush(windowSelection ? Color.FromRgb(202, 207, 216) : Color.FromRgb(157, 170, 192));
    }

    private void CompleteWindowSelection(Point firstScreen, Point secondScreen, bool addToSelection)
    {
        _windowSelectionAwaitingSecondPoint = false;
        SelectionDragOverlay.Visibility = Visibility.Collapsed;
        var start = ScreenToWorld(firstScreen); var end = ScreenToWorld(secondScreen);
        var minX = Math.Min(start.X, end.X); var maxX = Math.Max(start.X, end.X);
        var minY = Math.Min(start.Y, end.Y); var maxY = Math.Max(start.Y, end.Y);
        var windowSelection = secondScreen.X >= firstScreen.X;
        var queryTimer = Stopwatch.StartNew();
        if (!addToSelection) _selectedEntityIds.Clear();
        var selected = _spatialIndex.QueryUnordered(minX, minY, maxX, maxY)
            .Where(entity => !_editor.Drawing.HiddenEntityIds.Contains(entity.Id))
            .Where(entity => windowSelection ? _spatialIndex.IsContained(entity, minX, minY, maxX, maxY) : CrossesWindow(entity, minX, minY, maxX, maxY))
            .ToArray();
        foreach (var entity in selected) _selectedEntityIds.Add(entity.Id);
        queryTimer.Stop(); _lastSelectionQueryMilliseconds = queryTimer.Elapsed.TotalMilliseconds;
        AppendCommandHistory($"{(windowSelection ? "Window" : "Crossing")} selected {selected.Length} object(s); {_selectedEntityIds.Count} total.");
        if (!windowSelection) _lastCrossingWindow = (minX, minY, maxX, maxY);
        RefreshPropertiesPalette();
        RenderSelectionOverlay();
        DrawingCanvas.Focus();
        AfterPendingSelectionChanged(null);
    }

    private void DrawingCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (e.ClickCount >= 2)
        {
            _isPanning = false;
            DrawingCanvas.ReleaseMouseCapture();
            ResetPanPreview();
            SavePreviousView();
            FitView();
            UpdateZoomControls();
            RenderDrawing(false);
            AppendCommandHistory("Zoom extents (middle-button double-click).");
            e.Handled = true;
            return;
        }
        _wheelDebounceTimer.Stop();
        _pendingWheelZoom = false;
        HideFocusPatch(animated: false);
        SavePreviousView();
        _geometryBuildCancellation?.Cancel();
        _isPanning = true;
        PrepareGridForInteraction();
        _panPreviewActive = true;
        _lastInteractionRenderTimestamp = 0;
        SetInteractiveRenderQuality(true);
        _panPreviewTransform.X = _panPreviewTransform.Y = 0;
        Transform previewTransform = _panPreviewTransform;
        if (_wheelZoomPreviewActive)
        {
            var combined = new TransformGroup();
            combined.Children.Add(_wheelZoomPreviewTransform);
            combined.Children.Add(_panPreviewTransform);
            previewTransform = combined;
        }
        DrawingGeometryImage.RenderTransform = SelectionOverlayImage.RenderTransform =
            DrawingPreviewImage.RenderTransform = previewTransform;
        GridDotsImage.CacheMode = null;
        DrawingGeometryImage.CacheMode = SelectionOverlayImage.CacheMode = GetLayerBitmapCache(); DrawingPreviewImage.CacheMode = new BitmapCache(1.0);
        _lastPointer = _panAnchor = e.GetPosition(DrawingCanvas);
        _panCenterAtStart = _viewCenter;
        DrawingCanvas.CaptureMouse();
        DrawingCanvas.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void DrawingCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_isPanning) return;
        _lastPointer = e.GetPosition(DrawingCanvas);
        var dx = _lastPointer.X - _panAnchor.X;
        var dy = _lastPointer.Y - _panAnchor.Y;
        _panPreviewTransform.X = dx; _panPreviewTransform.Y = dy;
        _viewCenter = new Point2(_panCenterAtStart.X - dx / _viewScale,
            _panCenterAtStart.Y + dy / _viewScale);
        _isPanning = false;
        if (_wheelZoomPreviewActive)
        {
            _wheelZoomPreviewMatrix.Translate(dx, dy);
            if (!_viewportPreviewTimer.IsEnabled) _viewportPreviewTimer.Start();
            DrawingGeometryImage.RenderTransform = SelectionOverlayImage.RenderTransform =
                DrawingPreviewImage.RenderTransform = _wheelZoomPreviewTransform;
            _panPreviewActive = false;
            ApplyIdleBitmapCache();
        }
        DrawingCanvas.ReleaseMouseCapture();
        DrawingCanvas.Cursor = Cursors.Cross;
        RefreshGridNow();
        RenderDrawing(false);
        AppendCommandHistory($"Pan view center: {_viewCenter.X:0.###}, {_viewCenter.Y:0.###}");
        e.Handled = true;
    }

    private void DrawingCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_isPanning || DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0 || e.Delta == 0) return;
        var targetScale = Math.Clamp(_viewScale * Math.Pow(1.15, Math.Clamp(e.Delta / 120d, -8, 8)),
            MinViewportScale, MaxViewportScale);
        var factor = targetScale / _viewScale;
        if (!double.IsFinite(factor) || factor <= 0 || Math.Abs(factor - 1) < 1e-9) return;
        if (!_pendingWheelZoom)
        {
            HideFocusPatch(animated: false);
            SavePreviousView();
            PrepareGridForInteraction();
            _geometryBuildCancellation?.Cancel();
        }
        var anchor = e.GetPosition(DrawingCanvas);
        var worldAnchor = ScreenToWorld(anchor);
        ApplyWheelZoomPreview(anchor, factor);
        _viewScale = targetScale;
        _viewCenter = new Point2(worldAnchor.X - (anchor.X - DrawingCanvas.ActualWidth / 2) / _viewScale,
            worldAnchor.Y + (anchor.Y - DrawingCanvas.ActualHeight / 2) / _viewScale);
        _pendingWheelZoom = true;
        // Redraw as soon as the drawing can afford it: light views refresh almost at once (crisp at every notch, like AutoCAD);
        // heavy views wait for the wheel to settle so half-built frames are not thrown away at every notch.
        var debounce = _lastFullBuildMilliseconds <= 25 ? 30 : _lastFullBuildMilliseconds <= 70 ? 70 : 110;
        if (Math.Abs(_wheelDebounceTimer.Interval.TotalMilliseconds - debounce) > 0.5) _wheelDebounceTimer.Interval = TimeSpan.FromMilliseconds(debounce);
        _wheelDebounceTimer.Stop(); _wheelDebounceTimer.Start();
        // Grid and objects must move in the same frame, or the picture appears to slide under the cursor.
        // The grid is rebuilt at its true position at most once per frame; in between (high-resolution wheels) it follows the same zoom transform.
        var wheelNow = Stopwatch.GetTimestamp();
        if (_lastWheelGridTimestamp == 0 || Stopwatch.GetElapsedTime(_lastWheelGridTimestamp, wheelNow).TotalMilliseconds >= 24)
        {
            _lastWheelGridTimestamp = wheelNow; _wheelGridResidual = Matrix.Identity;
            RenderDrawingGrid(force: true);
        }
        else
        {
            _wheelGridResidual.ScaleAt(factor, factor, anchor.X, anchor.Y);
            GridDotsImage.RenderTransform = new MatrixTransform(_wheelGridResidual);
        }
        // Light drawings: the debounce timer above is a pure debounce (restarted on every wheel tick), so a
        // continuous/fast wheel gesture kept the real, crisp frame from ever committing until the wheel fully
        // stopped - the whole gesture played out as only the scaled bitmap preview (soft, and with a slightly
        // "rubbery" feel), even though the comment above ("crisp at every notch, like AutoCAD") already describes
        // the intended feel for fast/light drawings. For that fast tier only, also commit periodically WHILE the
        // wheel keeps moving (a throttle, not just a debounce), so the real geometry actually redraws live during
        // the gesture instead of only at the end. Heavier drawings (debounce > 30ms tier) are untouched: forcing
        // a full rebuild mid-gesture there would be slower than the gesture itself.
        if (debounce <= 30 && !_geometryBuildInProgress &&
            (_lastWheelLiveCommitTimestamp == 0 || Stopwatch.GetElapsedTime(_lastWheelLiveCommitTimestamp, wheelNow).TotalMilliseconds >= WheelLiveRefreshMilliseconds))
        {
            _lastWheelLiveCommitTimestamp = wheelNow;
            CommitPendingWheelZoom();
        }
    }

    private const double WheelLiveRefreshMilliseconds = 30;
    private long _lastWheelLiveCommitTimestamp;
    private long _lastWheelGridTimestamp;
    private Matrix _wheelGridResidual = Matrix.Identity;

    private void ApplyWheelZoomPreview(Point anchor, double factor)
    {
        if (!_wheelZoomPreviewActive)
        {
            _lastInteractionRenderTimestamp = 0;
            _wheelZoomPreviewMatrix = Matrix.Identity;
            if (_panPreviewActive)
            {
                _wheelZoomPreviewMatrix.Translate(_panPreviewTransform.X, _panPreviewTransform.Y);
                _panPreviewActive = false;
            }
            DrawingGeometryImage.CacheMode = GetLayerBitmapCache();
            SelectionOverlayImage.CacheMode = GetLayerBitmapCache();
            DrawingPreviewImage.CacheMode = s_previewBitmapCache;
            // Keep the grid's frozen cache object during the gesture; dropping and re-creating it flashed the background.
            if (GridDotsImage.CacheMode is null) GridDotsImage.CacheMode = GetLayerBitmapCache();
            SetInteractiveRenderQuality(true);
            _wheelZoomPreviewActive = true;
            DrawingGeometryImage.RenderTransform = _wheelZoomPreviewTransform;
            SelectionOverlayImage.RenderTransform = _wheelZoomPreviewTransform;
            DrawingPreviewImage.RenderTransform = _wheelZoomPreviewTransform;
        }
        _wheelZoomPreviewMatrix.ScaleAt(factor, factor, anchor.X, anchor.Y);
        // Apply now (one shared MatrixTransform, a cheap property write): deferring it to a 16 ms timer left the objects one frame
        // behind the grid and the camera, which read as the drawing slipping away from the point under the cursor.
        UpdateSafeWheelPreview();
    }

    private void UpdateSafeWheelPreview()
    {
        // Keep WPF's bitmap preview transform bounded across arbitrarily long wheel gestures.
        // The camera continues updating; a full frame replaces this preview after wheel idle.
        var safePreview = double.IsFinite(_wheelZoomPreviewMatrix.M11) &&
            double.IsFinite(_wheelZoomPreviewMatrix.OffsetX) && double.IsFinite(_wheelZoomPreviewMatrix.OffsetY) &&
            _wheelZoomPreviewMatrix.M11 >= 1d / 64 && _wheelZoomPreviewMatrix.M11 <= 64 &&
            Math.Abs(_wheelZoomPreviewMatrix.OffsetX) <= 1e6 && Math.Abs(_wheelZoomPreviewMatrix.OffsetY) <= 1e6;
        // Never hide the layers (that showed as a blank flash): outside the safe range keep the last good transform.
        if (safePreview) _wheelZoomPreviewTransform.Matrix = _wheelZoomPreviewMatrix;
        else
        {
            var clamped = _wheelZoomPreviewTransform.Matrix;
            _wheelZoomPreviewMatrix = clamped;
        }
        DrawingGeometryImage.Visibility = SelectionOverlayImage.Visibility = DrawingPreviewImage.Visibility = Visibility.Visible;
    }

    private void CommitPendingWheelZoom()
    {
        _wheelDebounceTimer.Stop();
        if (!_pendingWheelZoom) return;
        _pendingWheelZoom = false;
        _lastWheelGridTimestamp = 0; _wheelGridResidual = Matrix.Identity;
        UpdateZoomControls();
        RenderDrawingGrid(force: true); // same camera as the last wheel step reuses that grid image; a transformed one is rebuilt
        RenderDrawing(false);
    }

    /// <summary>The grid is cheap (two StreamGeometry paths). Rebuild it the moment the camera settles instead of
    /// waiting for the (slow) geometry frame, so grid and cursor feel instant while objects refine afterwards.</summary>
    private void RefreshGridNow()
    {
        if (GridDotsImage is null || DrawingCanvas is null) return;
        GridDotsImage.RenderTransform = Transform.Identity;
        _lastGridImageKey = null;
        RenderDrawingGrid(force: true);
    }

    private static readonly BitmapCache s_previewBitmapCache = CreateFrozenPreviewCache();
    private static BitmapCache CreateFrozenPreviewCache() { var c = new BitmapCache(1.0); c.Freeze(); return c; }

    private void ResetWheelZoomPreview()
    {
        _viewportPreviewTimer.Stop();
        DrawingGeometryImage.Visibility = SelectionOverlayImage.Visibility = DrawingPreviewImage.Visibility = Visibility.Visible;
        var refreshGrid = _wheelZoomPreviewActive;
        if (DrawingGeometryImage is not null) DrawingGeometryImage.RenderTransform = Transform.Identity;
        if (SelectionOverlayImage is not null) SelectionOverlayImage.RenderTransform = Transform.Identity;
        if (DrawingPreviewImage is not null) DrawingPreviewImage.RenderTransform = Transform.Identity;
        if (GridDotsImage is not null) GridDotsImage.RenderTransform = Transform.Identity;
        ApplyIdleBitmapCache();
        if (DrawingPreviewImage is not null) DrawingPreviewImage.CacheMode = null;
        SetInteractiveRenderQuality(false);
        _wheelZoomPreviewActive = false;
        _wheelZoomPreviewMatrix = Matrix.Identity;
        // The grid stayed visible and followed the temporary camera transform.
        // Rebuild it once, after the geometry frame has committed and transforms reset.
        if (refreshGrid && GridDotsImage is not null)
            RenderDrawingGrid(force: true); // same key as the grid built at wheel idle: reuses the image, no second rebuild/flash
    }

    /// <summary>
    /// The drafting crosshair is a full-canvas element that moves on every pointer move. Without a
    /// retained bitmap, WPF re-renders the (possibly very heavy) vector images under it for every
    /// cursor move, which freezes the app on mere hover. Keep the static layers cached as bitmaps.
    /// </summary>
    private void ApplyIdleBitmapCache()
    {
        // Assign the SAME frozen cache object the interactive phase used: replacing it with a new BitmapCache at the moment a new frame
        // arrives threw the cached surface away and showed as a screen flash.
        var layerCache = GetLayerBitmapCache();
        if (DrawingGeometryImage is not null && !ReferenceEquals(DrawingGeometryImage.CacheMode, layerCache)) DrawingGeometryImage.CacheMode = layerCache;
        if (SelectionOverlayImage is not null && !ReferenceEquals(SelectionOverlayImage.CacheMode, layerCache)) SelectionOverlayImage.CacheMode = layerCache;
        if (GridDotsImage is not null && !ReferenceEquals(GridDotsImage.CacheMode, layerCache)) GridDotsImage.CacheMode = layerCache;
    }

    private BitmapCache? _layerBitmapCache; private double _layerBitmapCacheDpi;
    private BitmapCache GetLayerBitmapCache()
    {
        var dpi = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);
        if (_layerBitmapCache is null || _layerBitmapCacheDpi != dpi)
        {
            _layerBitmapCache = new BitmapCache(dpi) { SnapsToDevicePixels = true }; _layerBitmapCache.Freeze(); _layerBitmapCacheDpi = dpi;
        }
        return _layerBitmapCache;
    }

    private void SetInteractiveRenderQuality(bool interactive)
    {
        var mode = interactive ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality;
        if (DrawingGeometryImage is not null) RenderOptions.SetBitmapScalingMode(DrawingGeometryImage, mode);
        if (SelectionOverlayImage is not null) RenderOptions.SetBitmapScalingMode(SelectionOverlayImage, mode);
        if (DrawingPreviewImage is not null) RenderOptions.SetBitmapScalingMode(DrawingPreviewImage, mode);
        if (GridDotsImage is not null) RenderOptions.SetBitmapScalingMode(GridDotsImage, mode);
    }

    private void ResetPanPreview()
    {
        if (!_panPreviewActive) return;
        _panPreviewActive = false;
        DrawingGeometryImage.RenderTransform = SelectionOverlayImage.RenderTransform =
            DrawingPreviewImage.RenderTransform = GridDotsImage.RenderTransform = Transform.Identity;
        ApplyIdleBitmapCache();
        DrawingPreviewImage.CacheMode = null;
        SetInteractiveRenderQuality(false);
        _lastGridImageKey = null;
        RenderDrawingGrid();
    }

    private void DrawingCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (_pickTool == "WIZ" && e.Key is Key.Enter or Key.Space && !(e.Key == Key.Space && WizardWantsSpace)) { WizardTyped(string.Empty); e.Handled = true; }
        else if (_polylineActive && e.Key == Key.Enter) { FinishPolyline(); e.Handled = true; }
        else if (_polylineActive && e.Key == Key.C) { FinishPolyline(close: true); e.Handled = true; }
        else if (_polylineActive && e.Key == Key.Back && _polylineVertices.Count > 0) { _polylineVertices.RemoveAt(_polylineVertices.Count - 1); RenderPreview(); e.Handled = true; }
        else if (e.Key == Key.Delete) { EraseSelection(); e.Handled = true; }
        else if ((e.Key is Key.Enter or Key.Space) && _pendingSelectionAction != SelectionAction.None) { CompleteSelectionStage(); e.Handled = true; }
        else if (e.Key is Key.Space or Key.Enter && !_dynamicCommandEntry && !IsDynamicCommandActive && !string.IsNullOrWhiteSpace(_lastCommand)) { ProcessCommand(_lastCommand); e.Handled = true; }
        else if (e.Key == Key.Escape && _gripActive) { CancelGrip(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            CancelActiveCommand();
            if (!_editor.IsLineActive && !_editor.IsCircleActive && !_transformActive) _selectedEntityIds.Clear();
            RefreshPropertiesPalette();
            RenderDrawing(false);
            e.Handled = true;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Z) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.Y) { Redo_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void DynamicValueTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CommandSuggestionPopup.IsOpen = false; _dynamicCommandEntry = false; _dynamicIdleVisible = true; DynamicValueTextBox.Clear(); CancelActiveCommand(); DynamicPromptText.Text = string.Empty; UpdateDynamicInputMode(); DrawingCanvas.Focus(); e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Space)) return;
        if (e.Key == Key.Space && WizardWantsSpace) return;
        if (_dynamicCommandEntry && CommandSuggestionPopup.IsOpen && CommandSuggestionsList.SelectedItem is CommandHint suggestion)
        { ExecuteCommandSuggestion(suggestion); e.Handled = true; return; }
        SubmitDynamicInput();
        e.Handled = true;
    }

    private void SubmitDynamicInput()
    {
        try { SubmitDynamicInputCore(); }
        finally { HideDynamicInputNow(); }
    }

    /// <summary>After Enter/Space the DYN input box disappears at once; typing again brings it back.</summary>
    private void HideDynamicInputNow()
    {
        if (DynamicInputPanel is null) return;
        _dynamicCommandEntry = false;
        DynamicValueTextBox.Clear();
        CommandSuggestionPopup.IsOpen = false;
        if (DynamicValueTextBox.IsKeyboardFocusWithin) DrawingCanvas.Focus();
        DynamicInputPanel.Visibility = Visibility.Collapsed;
        DynamicInputEntryPanel.Visibility = Visibility.Collapsed;
        DynamicInputPanel.IsHitTestVisible = false;
    }

    private void SubmitDynamicInputCore()
    {
        var text = DynamicValueTextBox.Text.Trim();
        if (_pickTool == "WIZ" && !_dynamicCommandEntry) { WizardTyped(text); return; }
        if (!_dynamicCommandEntry && TryHandleLegacyOption(text)) return; // LINE [Close/Undo], ROTATE [Copy/Reference]
        if (_dynamicCommandEntry)
        {
            if (CommandSuggestionPopup.IsOpen && CommandSuggestionsList.SelectedItem is CommandHint selected)
            { ExecuteCommandSuggestion(selected); return; }
            CommandSuggestionPopup.IsOpen = false;
            _dynamicCommandEntry = false;
            DynamicValueTextBox.Clear();
            UpdateDynamicInputMode();
            if (text.Length > 0) ProcessCommand(text);
            else if (!string.IsNullOrWhiteSpace(_lastCommand)) ProcessCommand(_lastCommand);
            return;
        }
        if (string.IsNullOrEmpty(text) || (_polylineActive && string.Equals(text, "C", StringComparison.OrdinalIgnoreCase)))
        {
            if (_polylineActive) { FinishPolyline(close: string.Equals(text, "C", StringComparison.OrdinalIgnoreCase)); return; }
            if (_rotateActive)
            {
                if (_rotateBasePoint is Point2 rotateBase) CompleteRotateFromPoint(ConstrainPointer(ScreenToWorld(_lastPointer)));
                else { _rotateActive = false; UpdateDynamicInputMode(); }
                return;
            }
            else if (_transformActive)
            {
                var point = ConstrainPointer(ScreenToWorld(_lastPointer));
                if (_transformBasePoint is null)
                {
                    _transformBasePoint = point;
                    DynamicPromptText.Text = "Specify second point or <use first point as displacement>:";
                }
                else if (_transformCopy && _copyCount > 0 && !_stretchActive) { EndLineCommand(); AppendCommandHistory("COPY: done."); DrawingCanvas.Focus(); } // Enter = Exit
                else if (_transformBasePoint is Point2 firstPoint)
                {
                    // AutoCAD: Enter at the second point = "use first point as displacement".
                    AppendCommandHistory($"Displacement = first point ({firstPoint.X:0.###}, {firstPoint.Y:0.###}).");
                    CompleteMove(new Point2(firstPoint.X * 2, firstPoint.Y * 2));
                    if (_transformCopy) { EndLineCommand(); DrawingCanvas.Focus(); }
                }
            }
            else if (_rectangleActive)
            {
                var point = ConstrainPointer(ScreenToWorld(_lastPointer));
                if (_rectangleFirstCorner is null) { _rectangleFirstCorner = point; DynamicPromptText.Text = "Specify opposite corner:"; }
                else AddRectangle(point);
            }
            else if (_arcActive) AddArcPoint(ConstrainPointer(ScreenToWorld(_lastPointer)));
            else if (_editor.IsCircleActive)
            {
                if (_editor.CircleCenter is null) { _editor.AddCircleCenter(ScreenToWorld(_lastPointer)); DynamicPromptText.Text = "Specify radius:"; }
                else EndLineCommand();
            }
            else if (_editor.LineStart is null) { _editor.AddLinePoint(ConstrainPointer(ScreenToWorld(_lastPointer))); DynamicPromptText.Text = "Specify next point:"; }
            else EndLineCommand();
        }
        else if (_rotateActive)
        {
            if (_rotateBasePoint is null && DynamicInputParser.TryParsePoint(text, ScreenToWorld(_lastPointer), ScreenToWorld(_lastPointer), out var rotateBase))
            {
                _rotateBasePoint = rotateBase;
                DynamicPromptText.Text = "Specify rotation angle or [Copy/Reference]:";
                DynamicValueTextBox.Clear();
            }
            else if (_rotateBasePoint is Point2 origin && (double.TryParse(text.TrimEnd('d', 'D'), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) || double.TryParse(text.TrimEnd('d', 'D'), NumberStyles.Float, CultureInfo.CurrentCulture, out degrees)))
                CompleteRotate(degrees * Math.PI / 180.0);
            else if (_rotateBasePoint is Point2 rotateOrigin && DynamicInputParser.TryParsePoint(text, rotateOrigin, ScreenToWorld(_lastPointer), out var rotateTarget))
                CompleteRotateFromPoint(rotateTarget);
            else { DynamicPromptText.Text = "Enter an angle in degrees or a point:"; DynamicValueTextBox.SelectAll(); }
        }
        else if (_polylineActive)
        {
            if (!_polylineArcMode && _polylineVertices.Count > 0 && text.Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                _polylineArcMode = true; DynamicPromptText.Text = "Specify endpoint of arc or [Line]:"; DynamicValueTextBox.Clear(); return;
            }
            if (_polylineArcMode && text.Equals("L", StringComparison.OrdinalIgnoreCase))
            {
                _polylineArcMode = false; DynamicPromptText.Text = "Specify next point or [Arc/Close]:"; DynamicValueTextBox.Clear(); return;
            }
            var reference = _polylineVertices.Count > 0 ? _polylineVertices[^1].Point : ScreenToWorld(_lastPointer);
            if (DynamicInputParser.TryParsePoint(text, reference, ScreenToWorld(_lastPointer), out var polylinePoint)) AddPolylineVertex(ConstrainPointer(polylinePoint));
            else { DynamicPromptText.Text = "Enter a point as X,Y or @dX,dY; Enter finishes:"; DynamicValueTextBox.SelectAll(); }
        }
        else if (_transformActive)
        {
            var reference = _transformBasePoint ?? ScreenToWorld(_lastPointer);
            if (DynamicInputParser.TryParsePoint(text, reference, ScreenToWorld(_lastPointer), out var point))
            {
                if (_transformBasePoint is null)
                {
                    _transformBasePoint = point;
                    DynamicPromptText.Text = "Specify second point or <use first point as displacement>:";
                    DynamicValueTextBox.Clear();
                }
                else CompleteMove(point);
            }
            else { DynamicPromptText.Text = "Enter a point as X,Y or @dX,dY:"; DynamicValueTextBox.SelectAll(); }
        }
        else if (_rectangleActive)
        {
            if (_rectangleFirstCorner is null && _rectanglePendingOption is null && text.Length > 0 && "chamfer".StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                _rectanglePendingOption = "CHAMFER1";
                DynamicPromptText.Text = $"Specify first chamfer distance for rectangles <{_rectangleChamfer1:0.###}>:";
                DynamicValueTextBox.Clear(); return;
            }
            if (_rectangleFirstCorner is null && _rectanglePendingOption is null && text.Length > 0 && "fillet".StartsWith(text, StringComparison.OrdinalIgnoreCase))
            {
                _rectanglePendingOption = "FILLET";
                DynamicPromptText.Text = $"Specify fillet radius for rectangles <{_rectangleFilletRadius:0.###}>:";
                DynamicValueTextBox.Clear(); return;
            }
            if (_rectanglePendingOption is { } pending)
            {
                double value;
                if (text.Length == 0) value = pending switch { "CHAMFER1" => _rectangleChamfer1, "CHAMFER2" => _rectangleChamfer2, _ => _rectangleFilletRadius };
                else if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)))
                { DynamicPromptText.Text = "Enter a distance:"; DynamicValueTextBox.SelectAll(); return; }
                switch (pending)
                {
                    case "CHAMFER1":
                        _rectangleChamfer1 = Math.Max(0, value); _rectangleFilletRadius = 0; _rectanglePendingOption = "CHAMFER2";
                        DynamicPromptText.Text = $"Specify second chamfer distance for rectangles <{_rectangleChamfer2:0.###}>:";
                        break;
                    case "CHAMFER2":
                        _rectangleChamfer2 = Math.Max(0, value); _rectanglePendingOption = null;
                        DynamicPromptText.Text = RectangleFirstCornerPrompt();
                        break;
                    default:
                        _rectangleFilletRadius = Math.Max(0, value); _rectangleChamfer1 = _rectangleChamfer2 = 0; _rectanglePendingOption = null;
                        DynamicPromptText.Text = RectangleFirstCornerPrompt();
                        break;
                }
                DynamicValueTextBox.Clear(); return;
            }
            var reference = _rectangleFirstCorner ?? ScreenToWorld(_lastPointer);
            if (DynamicInputParser.TryParsePoint(text, reference, ScreenToWorld(_lastPointer), out var point))
            {
                if (_rectangleFirstCorner is null) { _rectangleFirstCorner = point; DynamicPromptText.Text = "Specify opposite corner:"; DynamicValueTextBox.Clear(); }
                else AddRectangle(point);
            }
            else { DynamicPromptText.Text = "Enter a corner point as X,Y or @dX,dY:"; DynamicValueTextBox.SelectAll(); }
        }
        else if (_arcActive)
        {
            var reference = _arcStartPoint ?? _arcCenter ?? ScreenToWorld(_lastPointer);
            if (DynamicInputParser.TryParsePoint(text, reference, ScreenToWorld(_lastPointer), out var point)) AddArcPoint(point);
            else { DynamicPromptText.Text = "Enter an arc point as X,Y or @dX,dY:"; DynamicValueTextBox.SelectAll(); }
        }
        else if (_editor.IsCircleActive)
        {
            if (_editor.CircleCenter is null && DynamicInputParser.TryParsePoint(text, ScreenToWorld(_lastPointer), _editor.PreviewPoint ?? ScreenToWorld(_lastPointer), out var center))
            {
                _editor.AddCircleCenter(center);
                DynamicPromptText.Text = "Specify radius:";
                DynamicValueTextBox.Clear();
            }
            else if (_editor.CircleCenter is Point2 circleCenter &&
                     (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius) ||
                      double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out radius)) && radius > 0)
            {
                _editor.AddCircleRadius(radius);
                UpdateDynamicInputMode();
            }
            else
            {
                DynamicPromptText.Text = "Enter a center point or a positive radius:";
                DynamicValueTextBox.SelectAll();
            }
        }
        else
        {
            var reference = _editor.LineStart ?? ScreenToWorld(_lastPointer);
            if (DynamicInputParser.TryParsePoint(text, reference, _editor.PreviewPoint ?? reference, out var point))
            {
                _editor.AddLinePoint(point);
                DynamicValueTextBox.Clear();
                DynamicPromptText.Text = "Specify next point:";
                RenderDrawing(fitToDrawing: false);
            }
            else
            {
                DynamicPromptText.Text = "Invalid point. Enter X,Y, @dX,dY, or distance<angle:";
                DynamicValueTextBox.SelectAll();
            }
        }
    }

    private void CancelActiveCommand()
    {
        // PLINE: Esc after at least two points keeps what was drawn (AutoCAD ends the command, it does not discard the polyline).
        if (_polylineActive && _polylineVertices.Count >= 2) { FinishPolyline(); return; }
        if (_wiz is { EndOnEsc: true, OnEnd: not null } escWizard) { var onEscEnd = escWizard.OnEnd; CancelPickTool(silent: true); try { onEscEnd(escWizard); } catch { } }
        CancelPickTool(silent: true);
        _selectionCallback = null;
        _windowSelectionAwaitingSecondPoint = false;
        _selectionDragActive = false;
        SelectionDragOverlay.Visibility = Visibility.Collapsed;
        _pendingSelectionAction = SelectionAction.None;
        _transformActive = false;
        _transformBasePoint = null;
        _mirrorPointActive = false; _mirrorAxisStart = null;
        _distancePointActive = false; _distanceStart = null;
        EndLineCommand();
        AppendCommandHistory("Command cancelled.");
    }

    private void AddRectangle(Point2 opposite)
    {
        if (_rectangleFirstCorner is not Point2 first) return;
        var vertices = BuildRectangleVertices(first, opposite, _rectangleChamfer1, _rectangleChamfer2, _rectangleFilletRadius);
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), [_editor.StyleNew(new PolylineEntity(vertices, true, Guid.NewGuid()))]);
        _rectangleActive = false;
        _rectangleFirstCorner = null;
        _rectanglePendingOption = null;
        UpdateDynamicInputMode();
        AppendCommandHistory("Rectangle created.");
        DrawingCanvas.Focus();
        RenderDrawing(false);
    }

    private void AddArcPoint(Point2 point)
    {
        if (_arcCenter is null)
        {
            _arcCenter = point;
            DynamicPromptText.Text = "Specify arc start point:";
            AppendCommandHistory("Specify arc start point:");
            return;
        }
        if (_arcStartPoint is null)
        {
            if (Point2.Distance(_arcCenter.Value, point) < 1e-9) { DynamicPromptText.Text = "Start point must be away from the center:"; return; }
            _arcStartPoint = point;
            DynamicPromptText.Text = "Specify arc end point:";
            AppendCommandHistory("Specify arc end point:");
            return;
        }
        if (Point2.Distance(_arcCenter.Value, point) < 1e-9) { DynamicPromptText.Text = "End point must be away from the center:"; return; }
        var startAngle = Math.Atan2(_arcStartPoint.Value.Y - _arcCenter.Value.Y, _arcStartPoint.Value.X - _arcCenter.Value.X) * 180 / Math.PI;
        var endAngle = Math.Atan2(point.Y - _arcCenter.Value.Y, point.X - _arcCenter.Value.X) * 180 / Math.PI;
        var arc = new ArcEntity(_arcCenter.Value, Point2.Distance(_arcCenter.Value, _arcStartPoint.Value), startAngle, endAngle, Guid.NewGuid());
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), [_editor.StyleNew(arc)]);
        _arcActive = false; _arcCenter = _arcStartPoint = null;
        UpdateDynamicInputMode();
        AppendCommandHistory("Arc created (center, start, end; counter-clockwise).");
        DrawingCanvas.Focus();
        RenderDrawing(false);
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CommandSuggestionPopup.IsOpen && Keyboard.FocusedElement == DynamicValueTextBox)
        {
            if (e.Key is Key.Up or Key.Down) { MoveSuggestionSelection(e.Key == Key.Down ? 1 : -1); e.Handled = true; return; }
            if (e.Key == Key.Tab) { AcceptSelectedSuggestion(); e.Handled = true; return; }
        }
        if (e.Key == Key.Escape && (_dynamicCommandEntry || IsDynamicCommandActive))
        {
            CommandSuggestionPopup.IsOpen = false; _dynamicCommandEntry = false; _dynamicIdleVisible = _dynamicInputEnabled;
            DynamicValueTextBox.Clear(); DynamicPromptText.Text = string.Empty; CancelActiveCommand(); UpdateDynamicInputMode(); DrawingCanvas.Focus(); e.Handled = true; return;
        }
        if (e.Key == Key.Escape && CommandExpandedPanel.Visibility == Visibility.Visible)
            SetCommandHistoryExpanded(false);
        if ((e.Key is Key.Space or Key.Enter) && (_dynamicCommandEntry || IsDynamicCommandActive) && Keyboard.FocusedElement == DynamicValueTextBox && !(e.Key == Key.Space && WizardWantsSpace))
        {
            SubmitDynamicInput(); e.Handled = true; return;
        }
        // Space and Enter use the same CAD command lifecycle, but never hijack text editors.
        if (e.Key is Key.Space or Key.Enter && !e.Handled &&
            Keyboard.FocusedElement is not TextBoxBase &&
            Keyboard.FocusedElement != CommandInputTextBox &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            if (_windowSelectionAwaitingSecondPoint) { e.Handled = true; return; }
            if (_pickTool == "WIZ" && !(e.Key == Key.Space && WizardWantsSpace)) { WizardTyped(string.Empty); e.Handled = true; return; }
            if (_polylineActive) { FinishPolyline(); DrawingCanvas.Focus(); e.Handled = true; return; }
            if (_editor.IsLineActive || _editor.IsCircleActive || _transformActive || _rotateActive ||
                _rectangleActive || _arcActive)
            {
                // Finish the active command without erasing the completed geometry.
                CancelActiveCommand(); DrawingCanvas.Focus(); e.Handled = true; return;
            }
            if (_pendingSelectionAction == SelectionAction.None && !string.IsNullOrWhiteSpace(_lastCommand))
            { ProcessCommand(_lastCommand); DrawingCanvas.Focus(); e.Handled = true; return; }
        }
        if ((e.Key is Key.Space or Key.Enter) && _pendingSelectionAction != SelectionAction.None && Keyboard.FocusedElement != CommandInputTextBox)
        {
            CompleteSelectionStage(); e.Handled = true; return;
        }
        if (e.Key == Key.I && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _controlNameMode = !_controlNameMode;
            ControlNamePopup.IsOpen = false;
            _lastNamedControl = null;
            if (_controlNameMode) ShowControlName(Mouse.DirectlyOver as DependencyObject);
            AppendCommandHistory($"Control name display {(_controlNameMode ? "ON" : "OFF")} — Ctrl+Shift+I.");
            e.Handled = true;
            return;
        }
        if (IsDynamicCommandActive && _dynamicInputEnabled &&
            Keyboard.FocusedElement != DynamicValueTextBox &&
            (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0 &&
            TryGetDynamicInputCharacter(e.Key, out var character))
        {
            DynamicInputPanel.Visibility = Visibility.Visible;
            DynamicInputEntryPanel.Visibility = Visibility.Visible;
            PositionDynamicInput(_lastPointer);
            DynamicValueTextBox.Focus();
            DynamicValueTextBox.Text += character;
            DynamicValueTextBox.CaretIndex = DynamicValueTextBox.Text.Length;
            UpdateDynamicInputMode();
            e.Handled = true;
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        if (e.Key == Key.D1 || e.Key == Key.NumPad1) TogglePropertiesPalette();
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) FileSaveAs_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.S) FileSave_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.A) SelectAllEntities();
        else if (e.Key == Key.O) FileOpen_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.N) FileNew_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Z) Undo_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.Y) Redo_Click(this, new RoutedEventArgs());
        else return;
        e.Handled = true;
    }

    private static bool TryGetDynamicInputCharacter(Key key, out char character)
    {
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var caps = Keyboard.IsKeyToggled(Key.CapsLock);
        if (key is >= Key.A and <= Key.Z)
        {
            var letter = (char)('A' + ((int)key - (int)Key.A));
            character = shift ^ caps ? letter : char.ToLowerInvariant(letter);
            return true;
        }
        if (key is >= Key.D0 and <= Key.D9)
        {
            var digit = (char)('0' + ((int)key - (int)Key.D0));
            if (!shift) { character = digit; return true; }
            character = ")!@#$%^&*("[digit - '0'];
            return true;
        }
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            character = (char)('0' + ((int)key - (int)Key.NumPad0));
            return true;
        }
        if (key is Key.OemMinus or Key.Subtract) { character = shift ? '_' : '-'; return !shift; }
        if (key is Key.OemPeriod or Key.Decimal) { character = '.'; return true; }
        if (key == Key.OemComma) { character = ','; return true; }
        character = default;
        return false;
    }

    private void SavePreviousView()
    {
        _previousViews.Push((_viewCenter, _viewScale));
        while (_previousViews.Count > 30)
        {
            var keep = _previousViews.Take(30).ToArray();
            _previousViews.Clear();
            for (var index = keep.Length - 1; index >= 0; index--) _previousViews.Push(keep[index]);
        }
    }

    private void ZoomPrevious()
    {
        if (_previousViews.Count == 0) { AppendCommandHistory("No previous view."); return; }
        (_viewCenter, _viewScale) = _previousViews.Pop();
        UpdateZoomControls();
        RenderDrawing(false);
        AppendCommandHistory("Restored previous view.");
    }

    private void ZoomAt(Point screenPoint, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || DrawingCanvas.ActualWidth <= 0 || DrawingCanvas.ActualHeight <= 0) return;
        if (_pendingWheelZoom) CommitPendingWheelZoom();
        factor = Math.Clamp(_viewScale * factor, MinViewportScale, MaxViewportScale) / _viewScale;
        if (Math.Abs(factor - 1) < 1e-9) return;
        SavePreviousView();
        var worldAnchor = ScreenToWorld(screenPoint);
        ApplyWheelZoomPreview(screenPoint, factor);
        _viewScale *= factor;
        var width = DrawingCanvas.ActualWidth; var height = DrawingCanvas.ActualHeight;
        _viewCenter = new Point2(worldAnchor.X - (screenPoint.X - width / 2) / _viewScale,
            worldAnchor.Y + (screenPoint.Y - height / 2) / _viewScale);
        UpdateZoomControls();
        RenderDrawing(false);
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingZoomControls || DrawingCanvas is null || DrawingCanvas.ActualWidth <= 0) return;
        if (!_sliderChangePending) SavePreviousView();
        _sliderChangePending = true;
        var screenCenter = new Point(DrawingCanvas.ActualWidth / 2, DrawingCanvas.ActualHeight / 2);
        var worldAnchor = ScreenToWorld(screenCenter);
        _viewScale = _fitScale * Math.Pow(2, (ZoomSlider.Value - 50) / 10.0);
        _viewCenter = worldAnchor;
        UpdateZoomControls();
        _sliderDebounceTimer.Stop();
        _sliderDebounceTimer.Start();
    }

    private void UpdateZoomControls()
    {
        if (ZoomLevelText is null || ZoomSlider is null) return;
        var ratio = _viewScale / Math.Max(1e-6, _fitScale);
        ZoomLevelText.Text = $"{ratio * 100:0}%";
        _updatingZoomControls = true;
        ZoomSlider.Value = Math.Clamp(50 + 10 * Math.Log2(Math.Max(1e-6, ratio)), ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updatingZoomControls = false;
    }

    private Point2 ConstrainPointer(Point2 candidate)
    {
        var reference = GetDraftingReferencePoint();
        if (IsDynamicCommandActive && reference is Point2 origin)
        {
            var dx = candidate.X - origin.X;
            var dy = candidate.Y - origin.Y;
            if (StatusOrthoToggle.IsChecked == true)
            {
                candidate = Math.Abs(dx) >= Math.Abs(dy)
                    ? new Point2(candidate.X, origin.Y)
                    : new Point2(origin.X, candidate.Y);
                UpdatePolarGuide(null, null);
            }
            else if (StatusPolarToggle.IsChecked == true)
            {
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var angle = Math.Atan2(dy, dx);
                // AutoCAD-like 15-degree polar increment; distance remains free.
                const double step = Math.PI / 12.0;
                var lockedAngle = Math.Round(angle / step) * step;
                candidate = new Point2(origin.X + distance * Math.Cos(lockedAngle),
                    origin.Y + distance * Math.Sin(lockedAngle));
                UpdatePolarGuide(origin, candidate);
            }
            else UpdatePolarGuide(null, null);
        }
        else UpdatePolarGuide(null, null);

        if (IsDynamicCommandActive && StatusOsnapToggle.IsChecked == true && _osnapModes.Count > 0)
        {
            var started = Stopwatch.GetTimestamp();
            var tolerance = 10 / Math.Max(_viewScale, 1e-9);
            var found = _spatialIndex.TryFindSnap(candidate, tolerance, _osnapModes, out var snap);
            if (reference is Point2 relationReference)
            {
                if (_osnapModes.Contains(OsnapMode.Perpendicular) &&
                    _spatialIndex.TryFindRelationSnap(candidate, relationReference, tolerance, OsnapMode.Perpendicular, out var perpendicular) &&
                    (!found || Point2.Distance(candidate, perpendicular.Point) < Point2.Distance(candidate, snap.Point)))
                { snap = perpendicular; found = true; }
                if (_osnapModes.Contains(OsnapMode.Parallel) &&
                    _spatialIndex.TryFindRelationSnap(candidate, relationReference, tolerance, OsnapMode.Parallel, out var parallel) &&
                    (!found || Point2.Distance(candidate, parallel.Point) < Point2.Distance(candidate, snap.Point)))
                { snap = parallel; found = true; }
            }
            // Temporary polyline vertices exist only in the active command, not the R-tree.
            // Snap against stable preview vertices, never the moving cursor point.
            if (_polylineActive && _osnapModes.Contains(OsnapMode.Endpoint))
            {
                foreach (var vertex in _polylineVertices.Take(Math.Max(0, _polylineVertices.Count - 1)))
                {
                    var distance = Point2.Distance(candidate, vertex.Point);
                    if (distance <= tolerance && (!found || distance < Point2.Distance(candidate, snap.Point)))
                    { snap = new OsnapResult(vertex.Point, OsnapMode.Endpoint); found = true; }
                }
            }
            _lastOsnapQueryMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _osnapSamples.Enqueue(_lastOsnapQueryMilliseconds);
            while (_osnapSamples.Count > 100) _osnapSamples.Dequeue();
            if (found)
            {
                candidate = snap.Point;
                UpdateOsnapMarker(snap);
                // Acquire OSNAP tracking points while hovering, then keep them for
                // the current command just as AutoCAD's object-tracking references.
                if (StatusOtrackToggle.IsChecked == true)
                {
                    if (_trackingAnchors.Count >= 32) _trackingAnchors.RemoveAt(0);
                    if (!_trackingAnchors.Any(point => Point2.Distance(point, snap.Point) < 1e-8))
                        _trackingAnchors.Add(snap.Point);
                }
            }
            else UpdateOsnapMarker(null);
        }
        else UpdateOsnapMarker(null);

        candidate = ApplyObjectTracking(candidate);
        return candidate;
    }

    private void UpdateOsnapMarker(OsnapResult? snap)
    {
        _activeOsnap = snap;
        if (OsnapMarker is null || OsnapMarkerLabel is null) return;
        if (snap is not { } active)
        {
            OsnapMarker.Visibility = Visibility.Collapsed;
            OsnapMarkerLabel.Visibility = Visibility.Collapsed;
            return;
        }
        var screen = WorldToScreen(active.Point);
        Canvas.SetLeft(OsnapMarker, screen.X); Canvas.SetTop(OsnapMarker, screen.Y);
        Canvas.SetLeft(OsnapMarkerLabel, screen.X + 8); Canvas.SetTop(OsnapMarkerLabel, screen.Y + 5);
        OsnapMarker.Data = OsnapMarkerGeometries[active.Mode];
        var label = active.Mode switch
        {
            OsnapMode.Endpoint => "END", OsnapMode.Midpoint => "MID", OsnapMode.Center => "CEN",
            OsnapMode.Quadrant => "QUAD", OsnapMode.Intersection => "INT", OsnapMode.Parallel => "PAR",
            OsnapMode.Perpendicular => "PERP", _ => "INS"
        };
        OsnapMarkerLabel.Text = label;
        OsnapMarker.Visibility = Visibility.Visible;
        OsnapMarkerLabel.Visibility = Visibility.Visible;
    }

    private Point2 ScreenToWorld(Point screen)
    {
        var width = Math.Max(1, DrawingCanvas.ActualWidth);
        var height = Math.Max(1, DrawingCanvas.ActualHeight);
        return new Point2(_viewCenter.X + (screen.X - width / 2) / _viewScale,
            _viewCenter.Y + (height / 2 - screen.Y) / _viewScale);
    }

    private Point WorldToScreen(Point2 world)
    {
        var width = Math.Max(1, DrawingCanvas.ActualWidth);
        var height = Math.Max(1, DrawingCanvas.ActualHeight);
        return new Point(width / 2 + (world.X - _viewCenter.X) * _viewScale,
            height / 2 - (world.Y - _viewCenter.Y) * _viewScale);
    }

    private void PositionDynamicInput(Point pointer)
    {
        var width = Math.Max(1, DrawingCanvas.ActualWidth);
        var height = Math.Max(1, DrawingCanvas.ActualHeight);
        // The lightweight hint follows the cursor but cannot receive clicks or keyboard focus.
        if (DynamicHintPanel.Visibility == Visibility.Visible)
        {
            var hintWidth = Math.Max(1, DynamicHintPanel.ActualWidth);
            var hintHeight = Math.Max(1, DynamicHintPanel.ActualHeight);
            Canvas.SetLeft(DynamicHintPanel, Math.Round(Math.Clamp(pointer.X + 18, 6, Math.Max(6, width - hintWidth - 6))));
            Canvas.SetTop(DynamicHintPanel, Math.Round(Math.Clamp(pointer.Y + 21, 6, Math.Max(6, height - hintHeight - 6))));
        }
        // Do not move the editable form while the user is typing or using suggestions.
        if (DynamicInputPanel.Visibility != Visibility.Visible ||
            DynamicValueTextBox.IsKeyboardFocusWithin || CommandSuggestionPopup.IsOpen) return;
        var panelWidth = DynamicInputPanel.Width > 0 ? DynamicInputPanel.Width : 242;
        var panelHeight = Math.Max(23, DynamicInputPanel.ActualHeight);
        Canvas.SetLeft(DynamicInputPanel, Math.Round(Math.Clamp(pointer.X + 18, 6, Math.Max(6, width - panelWidth - 6))));
        Canvas.SetTop(DynamicInputPanel, Math.Round(Math.Clamp(pointer.Y + 38, 6, Math.Max(6, height - panelHeight - 6))));
    }

    private void FocusDynamicInput()
    {
        // Starting a draw command must not open the editable form; first typed key does.
        DrawingCanvas.Focus();
        UpdateDynamicInputMode();
    }

    private void DynamicValueTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!_dynamicInputEnabled) return;
        UpdateDynamicInputMode(); PositionDynamicInput(_lastPointer);
    }

    private void DynamicInputPanel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsDynamicCommandActive || _dynamicCommandEntry) SubmitDynamicInput();
        e.Handled = true;
    }

    private bool IsDynamicCommandActive => (_pickTool is not null && _pickTool != "ZOOMW") || _editor.IsLineActive || _editor.IsCircleActive || _transformActive || _rectangleActive || _arcActive || _polylineActive || _rotateActive || _mirrorPointActive || _distancePointActive;

    private void UpdateDynamicInputMode()
    {
        if (DynamicInputPanel is null) return;
        var active = IsDynamicCommandActive;
        // Prompt is display-only and follows the pointer; never takes focus.
        var showHint = _dynamicInputEnabled && (active || _selectHintActive) && !string.IsNullOrWhiteSpace(DynamicPromptText.Text);
        DynamicHintPanel.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        DynamicHintPanel.IsHitTestVisible = false;
        // Editable DYN appears only after a real keystroke (or while it is focused).
        var showInput = _dynamicInputEnabled &&
            (_dynamicCommandEntry || DynamicValueTextBox.IsKeyboardFocusWithin ||
             !string.IsNullOrEmpty(DynamicValueTextBox.Text));
        DynamicInputPanel.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
        // Match the editable field width to the adjacent prompt hint instead of showing a
        // much wider fixed DYN box. Measure with the same 245-DIP cap as the hint text.
        DynamicHintPanel.Measure(new Size(245, 32));
        DynamicInputPanel.Width = 162;
        DynamicInputPanel.IsHitTestVisible = showInput;
        DynamicInputEntryPanel.Visibility = showInput ? Visibility.Visible : Visibility.Collapsed;
        if (showHint || showInput)
        {
            // A command can start from the command line/toolbar, where _lastPointer is stale. Use the live pointer.
            if (DrawingCanvas.IsMouseOver) _lastPointer = Mouse.GetPosition(DrawingCanvas);
            else if (_lastPointer.X == 0 && _lastPointer.Y == 0) _lastPointer = new Point(DrawingCanvas.ActualWidth / 2, DrawingCanvas.ActualHeight / 2);
            PositionDynamicInput(_lastPointer);
            // Real panel size is known only after layout; place once more so it hugs the cursor immediately.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => PositionDynamicInput(_lastPointer)));
        }
        if (active) QueueSnapCandidateBuild(_activeSession);
    }

    private void PrepareGridForInteraction()
    {
        // Keep the grid image visible and transform it with the viewport preview.
        // Hiding/rebuilding it on every gesture caused a visible flash at wheel idle.
        if (GridDotsImage is not null) GridDotsImage.Visibility = Visibility.Visible;
    }

    private bool IsPaperSpaceActive => !string.Equals(_activeLayout, "Model", StringComparison.OrdinalIgnoreCase);

    private (double Width, double Height) GetPaperSize(string layout)
    {
        var d = _editor.Drawing;
        if (d.LayoutPapers.TryGetValue(layout, out var native)) return native;
        if (d.PlotSetups.TryGetValue(layout, out var setup)) return (setup.PaperWidthMm, setup.PaperHeightMm);
        return (420, 297);
    }

    /// <summary>Layout background: dark pasteboard, drop-shadowed white sheet, dashed printable area (like AutoCAD paper space).</summary>
    private void RenderPaperBackground(double paperWidth, double paperHeight, Point2 viewCenter, double width, double height, GridImageKey key)
    {
        var s = _viewScale;
        Point P(double x, double y) => new(width / 2 + (x - viewCenter.X) * s, height / 2 - (y - viewCenter.Y) * s);
        var a = P(0, paperHeight); var b = P(paperWidth, 0);
        var sheet = new Rect(a, b);
        var margin = _editor.Drawing.PlotSetups.TryGetValue(_activeLayout, out var ps) ? new[] { ps.MarginLeftMm, ps.MarginTopMm, ps.MarginRightMm, ps.MarginBottomMm } : new[] { 5d, 5d, 5d, 5d };
        var printable = new Rect(P(margin[0], paperHeight - margin[1]), P(paperWidth - margin[2], margin[3]));
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(58, 66, 77)), null, new Rect(0, 0, width, height));
            var shadowOffset = Math.Clamp(Math.Min(sheet.Width, sheet.Height) * 0.012, 3, 14);
            var shadow = new SolidColorBrush(Color.FromArgb(120, 10, 14, 20)); shadow.Freeze();
            context.DrawRectangle(shadow, null, new Rect(sheet.X + shadowOffset, sheet.Y + shadowOffset, sheet.Width, sheet.Height));
            context.DrawRectangle(Brushes.White, null, sheet);
            if (printable.Width > 2 && printable.Height > 2)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromRgb(236, 168, 40)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
                pen.Freeze();
                context.DrawRectangle(null, pen, printable);
            }
        }
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height)); group.Freeze();
        _lastGridImage = new DrawingImage(group); _lastGridImage.Freeze(); _lastGridImageKey = key;
        GridDotsImage.Source = _lastGridImage;
    }

    private void RenderDrawingGrid(bool force = false, Point2? centerOverride = null)
    {
        var viewCenter = centerOverride ?? _viewCenter;
        if ((_panPreviewActive || _wheelZoomPreviewActive) && !force) return;
        GridDotsImage.RenderTransform = Transform.Identity;
        var width = Math.Max(0, DrawingCanvas.ActualWidth); var height = Math.Max(0, DrawingCanvas.ActualHeight);
        GridDotsImage.Width = width; GridDotsImage.Height = height;
        if (GridToggleButton.IsChecked != true)
        {
            GridDotsImage.Source = null; _lastGridImage = null; _lastGridImageKey = null; return;
        }
        var paper = IsPaperSpaceActive ? GetPaperSize(_activeLayout) : ((double, double)?)null;
        var key = new GridImageKey(viewCenter.X, viewCenter.Y, _viewScale, width, height, paper is { } pp ? $"{pp.Item1:0.##}x{pp.Item2:0.##}" : "");
        if (_lastGridImageKey == key && _lastGridImage is not null) { GridDotsImage.Source = _lastGridImage; return; }
        if (width <= 0 || height <= 0 || !double.IsFinite(_viewScale) || _viewScale <= 0) return;
        if (paper is { } sheet) { RenderPaperBackground(sheet.Item1, sheet.Item2, viewCenter, width, height, key); return; }
        // Match the reference: fine 20 px grid with every fifth line emphasized.
        var desiredWorldStep = 20 / _viewScale;
        var decade = Math.Pow(10, Math.Floor(Math.Log10(desiredWorldStep)));
        var normalized = desiredWorldStep / decade;
        var worldStep = (normalized < 1.5 ? 1 : normalized < 3.5 ? 2 : normalized < 7.5 ? 5 : 10) * decade;
        if (_gridWorldStep > 0)
        {
            worldStep = _gridWorldStep;
            var projectedStep = worldStep * _viewScale;
            if (double.IsFinite(projectedStep) && projectedStep > 0 && projectedStep < 12)
                worldStep *= Math.Pow(10, Math.Ceiling(Math.Log10(12 / projectedStep)));
        }
        var spacing = worldStep * _viewScale;
        var originX = width / 2 - viewCenter.X * _viewScale;
        var originY = height / 2 + viewCenter.Y * _viewScale;
        if (!double.IsFinite(spacing) || spacing < 1 || !double.IsFinite(originX) || !double.IsFinite(originY)) return;
        // Enumerate screen-sized ranges only. World-coordinate integer indices can overflow at deep zoom.
        var firstX = ((originX % spacing) + spacing) % spacing;
        var firstY = ((originY % spacing) + spacing) % spacing;
        var baseColumn = Math.Round((firstX - originX) / spacing);
        var baseRow = Math.Round((firstY - originY) / spacing);
        var columns = (int)Math.Min(2048, Math.Ceiling(width / spacing) + 1);
        var rows = (int)Math.Min(2048, Math.Ceiling(height / spacing) + 1);
        // Keep the neutral charcoal CAD background with subtle, evenly spaced major/minor grid lines.
        var minorPen = new Pen(new SolidColorBrush(Color.FromRgb(43, 49, 58)), 1);
        var majorPen = new Pen(new SolidColorBrush(Color.FromRgb(58, 66, 77)), 1);
        minorPen.Freeze(); majorPen.Freeze();
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 37, 43)), null, new Rect(0, 0, width, height));
            // Two StreamGeometry paths (minor/major) replace up to ~4,000 individual DrawLine calls.
            var minorGeometry = new StreamGeometry(); var majorGeometry = new StreamGeometry();
            using (var minorContext = minorGeometry.Open())
            using (var majorContext = majorGeometry.Open())
            {
                for (var column = 0; column < columns; column++)
                {
                    var x = firstX + column * spacing;
                    var target = Math.Abs(Math.IEEERemainder(baseColumn + column, 5)) < 0.5 ? majorContext : minorContext;
                    target.BeginFigure(new Point(x, 0), false, false);
                    target.LineTo(new Point(x, height), true, false);
                }
                for (var row = 0; row < rows; row++)
                {
                    var y = firstY + row * spacing;
                    var target = Math.Abs(Math.IEEERemainder(baseRow + row, 5)) < 0.5 ? majorContext : minorContext;
                    target.BeginFigure(new Point(0, y), false, false);
                    target.LineTo(new Point(width, y), true, false);
                }
            }
            minorGeometry.Freeze(); majorGeometry.Freeze();
            context.DrawGeometry(null, minorPen, minorGeometry);
            context.DrawGeometry(null, majorPen, majorGeometry);
        }
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height)); group.Freeze();
        _lastGridImage = new DrawingImage(group); _lastGridImage.Freeze(); _lastGridImageKey = key;
        GridDotsImage.Source = _lastGridImage;
    }

    private void RenderDrawing(bool fitToDrawing)
    {
        if (_viewportClosed || DrawingGeometryImage is null || DrawingCanvas is null) return;
        if ((_isPanning || _wheelDebounceTimer.IsEnabled) && !fitToDrawing) return;
        var timer = Stopwatch.StartNew();
        if (fitToDrawing) FitView();
        var width = DrawingCanvas.ActualWidth;
        var height = DrawingCanvas.ActualHeight;
        DrawingGeometryImage.Width = width;
        DrawingGeometryImage.Height = height;
        RenderDrawingGrid();
        var key = new RenderImageKey(_activeSession.GeometryRevision, _viewCenter.X, _viewCenter.Y, _viewScale, width, height, _skipTextMTextForPerfTest);
        if (_geometryBuildInProgress &&
            (_geometryBuildKey != key || _geometryBuildCancellation?.IsCancellationRequested == true))
        {
            // A camera can zoom out and back to the same key after its original worker
            // was canceled by the start of the gesture. Mark a same-key restart too;
            // key inequality alone would otherwise leave an empty viewport unbuilt.
            _renderRequestedDuringBuild = true;
            if (_geometryBuildKey != key) _geometryBuildCancellation?.Cancel();
        }
        if (!(_lastRenderImage is not null && _lastRenderImageKey == key) && TryGetCachedFrame(_activeSession, key, out var cachedFrame))
        {
            _lastRenderImage = cachedFrame; _lastRenderImageKey = key; // revisited camera: reuse the finished frame
        }
        if (_lastRenderImage is not null && _lastRenderImageKey == key)
        {
            DrawingGeometryImage.Source = _lastRenderImage;
            HideFocusPatch(animated: false);
            MarkInitialViewportRendered(_activeSession);
            ResetPanPreview();
            RenderSelectionOverlay();
            RenderPreview();
            ResetWheelZoomPreview();
            timer.Stop(); _lastRenderMilliseconds = timer.Elapsed.TotalMilliseconds;
            return;
        }
        if (width <= 0 || height <= 0 || !double.IsFinite(_viewScale) || _viewScale <= 0) return;
        // One worker owns query, cache lookup, geometry and composition. Repeated requests
        // cancel stale work; its completion starts only the latest viewport.
        if (!_geometryBuildInProgress)
        {
            // Progressive refinement (AutoCAD-like): when full frames are slow, show a fast coarse draft first
            // (far-LOD: text as boxes, dense areas aggregated), then refine to the full-detail frame.
            // Stage 0 (AutoCAD-style instant draft): when frames are slow, first paint EVERYTHING as colored 1-px pixel strokes in tens of ms,
            // so nothing is missing while the real frame builds. Then (stage 1) the pointer region sharp, (stage 2) the full-quality frame.
            // While the previous frame is still shown scaled by the zoom preview and the scale change is modest, keep showing it until the real
            // frame is ready: swapping in a sparse pixel/coarse draft first and then the full frame is what read as a flash.
            var previewStillGood = _wheelZoomPreviewActive && _wheelZoomPreviewMatrix.M11 is >= 0.4 and <= 2.5;
            if (!previewStillGood && _pixelDraftKey != key && _lastRenderImage is not null && _lastFullBuildMilliseconds > PixelDraftThresholdMilliseconds)
            {
                _pixelDraftKey = key;
                _ = BuildVisibleGeometryAsync(_activeSession, _activeSession.GeometryRevision, 2, key, true, null, true);
                return;
            }
            var wantDraft = !previewStillGood && _lastFullBuildMilliseconds > DraftBuildThresholdMilliseconds && CurrentGeometryLod < 2 && _pixelDraftKey != key &&
                !(_refineKey is { } refineKey && refineKey == key);
            var focusRect = !wantDraft ? TryGetFocusRect(key, width, height) : null;
            _ = BuildVisibleGeometryAsync(_activeSession, _activeSession.GeometryRevision, CurrentGeometryLod, key, wantDraft, focusRect);
        }
    }

    private const double DraftBuildThresholdMilliseconds = 250;
    private const double PixelDraftThresholdMilliseconds = 120;
    private RenderImageKey? _pixelDraftKey;
    private RenderImageKey? _refineKey;
    private double _lastFullBuildMilliseconds;

    // Above this many viewport candidates, lines/arcs/circles/polylines are written straight into one
    // StreamGeometry per stroke style (no per-entity geometry objects, no geometry-cache traffic).
    private const int DirectStreamCandidateThreshold = 400;
    private const int SoftLineRasterThreshold = 20_000;

    private sealed class DirectStyleStream
    {
        public DirectStyleStream(CadEntity sample)
        {
            Sample = sample;
            Geometry = new StreamGeometry();
            Context = Geometry.Open();
        }
        public CadEntity Sample { get; }
        public StreamGeometry Geometry { get; }
        public StreamGeometryContext Context { get; }
        public int Count;
    }

    private static (CadColor? Color, string Pattern) GetEntityStyleKey(CadEntity entity,
        IReadOnlyDictionary<string, CadColor> layerColors, Dictionary<(IReadOnlyList<double> Pattern, double Scale), string> patternKeys)
    {
        var effectiveColor = entity.ExplicitColor ?? (layerColors.TryGetValue(entity.Layer, out var layerColor) ? layerColor : entity.DisplayColor);
        string patternKey;
        if (entity.LineTypePattern is { Count: > 0 } pattern)
        {
            var cacheKey = (pattern, entity.LineTypeScale);
            if (!patternKeys.TryGetValue(cacheKey, out var cached))
            {
                cached = string.Join(",", pattern.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "@" + entity.LineTypeScale.ToString("R", CultureInfo.InvariantCulture);
                patternKeys[cacheKey] = cached;
            }
            patternKey = cached;
        }
        else patternKey = entity is ProxyCadEntity ? "proxy" : "continuous";
        return (effectiveColor, patternKey);
    }

    // Frames with many items are rasterized on a background STA thread so the WPF UI thread only
    // has to draw ONE bitmap per frame. Handing tens of thousands of vector geometries to the UI/render
    // thread (what the previous versions did) is what froze the application on large DWG files.
    // Item count is a poor proxy for cost (one INSERT proxy can hold thousands of paths), so rasterize almost always.
    private const int RasterizeItemThreshold = 24;
    private const int LargeFrameStyleGroupingThreshold = 1500;
    private const int MaximumRasterDimension = 8192;
    private const long MaximumRasterPixels = 36_000_000;
    // Entities whose whole extent is within this many pixels are drawn as a dot instead of geometry.
    private const double SubPixelEntityLimit = 1.0;
    private const double DenseSubPixelEntityLimit = 3.0;
    // TEXT/MTEXT shorter than this on screen is unreadable; a thin box is drawn instead of glyph outlines.
    private const double MinReadableTextPixels = 4.0;
    // Text taller than this on screen is always drawn as real glyphs, even in LOD 2 / draft frames (no hollow boxes while zooming).
    private const double FarTextBoxPixels = 5.0;

    /// <summary>On the white sheet, near-white CAD colors (AutoCAD color 7) must print/display as black.</summary>
    private static Color PaperSafeColor(CadColor? color, bool paperMode)
    {
        var c = color ?? new CadColor(220, 222, 226);
        if (paperMode && c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 200) return Color.FromRgb(0, 0, 0);
        return Color.FromRgb(c.R, c.G, c.B);
    }

    private static DrawingImage ComposeRenderImage(IReadOnlyList<(CadEntity Entity, Geometry Geometry)> items,
        IReadOnlyDictionary<string, CadColor> layerColors, double width, double height, Point2 center, double scale, CancellationToken cancellationToken,
        bool lightweight = false, BitmapSource? tinyOverlay = null, double dpiScale = 1.0,
        IReadOnlyList<(Geometry Geometry, Pen Pen)>? directLayers = null, bool paperMode = false, int directFigures = 0, int tier = 2)
    {
        var pens = new Dictionary<(CadColor? Color, string Pattern), Pen>();
        // LineTypePattern arrays are shared per linetype, so the (expensive) textual pattern key is
        // computed once per distinct pattern instead of once per entity per frame.
        var patternKeys = new Dictionary<(IReadOnlyList<double> Pattern, double Scale), string>();
        (CadColor? Color, string Pattern) GetStyleKey(CadEntity entity, Geometry geometry)
        {
            var effectiveColor = entity.ExplicitColor ?? (layerColors.TryGetValue(entity.Layer, out var layerColor) ? layerColor : entity.DisplayColor);
            string patternKey;
            if (entity.LineTypePattern is { Count: > 0 } pattern)
            {
                var cacheKey = (pattern, entity.LineTypeScale);
                if (!patternKeys.TryGetValue(cacheKey, out var cachedPatternKey))
                {
                    cachedPatternKey = string.Join(",", pattern.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "@" + entity.LineTypeScale.ToString("R", CultureInfo.InvariantCulture);
                    patternKeys[cacheKey] = cachedPatternKey;
                }
                patternKey = cachedPatternKey;
            }
            else patternKey = entity is ProxyCadEntity ? "proxy" : "continuous";
            // Glyph outlines are closed shapes: text is drawn solid (filled), like AutoCAD, not as outlines.
            if (entity is CadTextEntity or CadMTextEntity && geometry is not (RectangleGeometry or LineGeometry)) patternKey = "fill:" + patternKey;
            else if (entity is ProxyCadEntity { PreviewFilled: true } && geometry is not (RectangleGeometry or LineGeometry)) patternKey = "fill:hatch:" + patternKey; // solid HATCH
            // DIMENSION/INSERT text: glyph outlines were only stroked (hollow letters). Figures of a proxy's StreamGeometry are not filled, so
            // also supplying a brush fills just the glyphs (and closed arrowheads) while dimension lines stay strokes.
            else if (entity is ProxyCadEntity { PreviewFilled: false } pf && (!string.IsNullOrWhiteSpace(pf.PreviewText) || pf.ProxyGraphicsTexts is { Count: > 0 }) && geometry is not (RectangleGeometry or LineGeometry)) patternKey += "|pf";
            return (effectiveColor, patternKey);
        }
        Pen GetPen(CadEntity entity, (CadColor? Color, string Pattern) key)
        {
            if (pens.TryGetValue(key, out var cached)) return cached;
            var hasPattern = entity.LineTypePattern is { Count: > 0 } && !key.Pattern.StartsWith("fill:", StringComparison.Ordinal);
            var rgb = PaperSafeColor(key.Color, paperMode);
            var brush = new SolidColorBrush(rgb); 
            if (key.Pattern.StartsWith("fill:hatch:", StringComparison.Ordinal)) brush.Opacity = 0.6; // solid hatch must not hide the geometry beneath it
            brush.Freeze();
            var isFilledText = key.Pattern.StartsWith("fill:", StringComparison.Ordinal);
            var pen = isFilledText ? new Pen(brush, 0) : CreateWorldPen(brush, entity.LineTypePattern, entity.LineTypeScale, scale, entity is ProxyCadEntity && !hasPattern);
            if (isFilledText && pen.CanFreeze) pen.Freeze();
            pens.Add(key, pen);
            return pen;
        }

        // Learned cost says this frame would take too long in WPF's software rasterizer: draw filled glyph/hatch outlines (the costliest
        // items: thousands of Bezier edges plus alpha fills) as their bounding boxes for this frame - AutoCAD-style draft - so the whole
        // drawing still appears in time instead of the frame timing out and leaving objects missing.
        var preUnits = items.Count + directFigures / 20.0;
        var degradeBudget = tier switch { 1 => 4000.0, 3 => 600.0, _ => RasterDegradeBudgetMilliseconds };
        var degradeFills = s_rasterMsPerUnit > 0 && preUnits * s_rasterMsPerUnit > degradeBudget;
        var degradedFillCount = 0;
        Geometry BoundsBox(Geometry geometry)
        {
            degradedFillCount++;
            var b = geometry.Bounds;
            return b.IsEmpty || !double.IsFinite(b.Width) || !double.IsFinite(b.Height) ? Geometry.Empty : new RectangleGeometry(b);
        }
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            if (!lightweight) context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            context.PushTransform(new MatrixTransform(new Matrix(scale, 0, 0, -scale,
                width / 2 - center.X * scale, height / 2 + center.Y * scale)));
            if (directLayers is not null)
                foreach (var (layerGeometry, layerPen) in directLayers)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    context.DrawGeometry(null, layerPen, layerGeometry);
                }

            // Batch only consecutive entities with identical stroke style. This preserves
            // the original painter's order while reducing WPF DrawingContext submissions.
            GeometryGroup? batch = null;
            Pen? batchPen = null;
            (CadColor? Color, string Pattern)? batchKey = null;
            // Layered drawings alternate styles constantly, which fragments a frame into tens of thousands of
            // tiny DrawGeometry calls. Outline-only CAD output has no opaque fills, so for big frames group by
            // stroke style instead (first-appearance order); only the overlap order of different colors changes.
            if (items.Count > LargeFrameStyleGroupingThreshold)
            {
                var groups = new Dictionary<(CadColor? Color, string Pattern), (GeometryGroup Group, Pen Pen)>();
                var order = new List<(CadColor? Color, string Pattern)>();
                foreach (var item in items)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    var itemKey = GetStyleKey(item.Entity, item.Geometry);
                    var itemGeometry = item.Geometry;
                    if (degradeFills && itemKey.Pattern.StartsWith("fill:", StringComparison.Ordinal)) { itemGeometry = BoundsBox(item.Geometry); itemKey = (itemKey.Color, "continuous"); }
                    if (!groups.TryGetValue(itemKey, out var entry))
                    {
                        entry = (new GeometryGroup(), GetPen(item.Entity, itemKey));
                        groups[itemKey] = entry; order.Add(itemKey);
                    }
                    entry.Group.Children.Add(itemGeometry);
                }
                foreach (var key in order)
                    if (groups[key].Group.Children.Count > 0) context.DrawGeometry(key.Pattern.StartsWith("fill:", StringComparison.Ordinal) || key.Pattern.EndsWith("|pf", StringComparison.Ordinal) ? groups[key].Pen.Brush : null, groups[key].Pen, groups[key].Group);
            }
            else
            foreach (var item in items)
            {
                // A stale frame is discarded by the worker after composition. Break here
                // instead of throwing during ordinary zoom/pan cancellation.
                if (cancellationToken.IsCancellationRequested) break;
                var itemKey = GetStyleKey(item.Entity, item.Geometry);
                var itemGeometry = item.Geometry;
                if (degradeFills && itemKey.Pattern.StartsWith("fill:", StringComparison.Ordinal)) { itemGeometry = BoundsBox(item.Geometry); itemKey = (itemKey.Color, "continuous"); }
                if (batch is null || batchKey != itemKey)
                {
                    if (batch is { Children.Count: > 0 } && batchPen is not null)
                        context.DrawGeometry(batchKey is { } doneKey && (doneKey.Pattern.StartsWith("fill:", StringComparison.Ordinal) || doneKey.Pattern.EndsWith("|pf", StringComparison.Ordinal)) ? batchPen.Brush : null, batchPen, batch);
                    batch = new GeometryGroup();
                    batchKey = itemKey;
                    batchPen = GetPen(item.Entity, itemKey);
                }
                batch.Children.Add(itemGeometry);
            }
            if (batch is { Children.Count: > 0 } && batchPen is not null)
                context.DrawGeometry(batchKey is { } lastKey && (lastKey.Pattern.StartsWith("fill:", StringComparison.Ordinal) || lastKey.Pattern.EndsWith("|pf", StringComparison.Ordinal)) ? batchPen.Brush : null, batchPen, batch);
            context.Pop();
            // Pixel-density LOD is purely visual. The original entities stay indexed
            // and remain available at full precision for snapping and picking.
            if (tinyOverlay is not null && !cancellationToken.IsCancellationRequested)
                context.DrawImage(tinyOverlay, new Rect(0, 0, width, height));
        }
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height));
        group.Freeze();
        // WPF's software rasterizer (RenderTargetBitmap) costs ~2 ms per glyph-heavy item. After the first measurement, medium frames whose
        // predicted raster time is long are handed to WPF's GPU/render-thread path as a vector image (BitmapCache keeps pan/zoom cheap);
        // genuinely heavy frames (thousands of items/figures) still rasterize once, off the UI thread.
        s_lastDegradedFills = degradedFillCount;
        var workUnits = items.Count + directFigures / 20.0;
        var predictedRasterMs = workUnits * s_rasterMsPerUnit;
        var wantRaster = items.Count >= RasterizeItemThreshold || directLayers is { Count: > 0 };
        // Vector images are marshaled to WPF's render thread ON THE UI THREAD in proportion to their figure/point count, which froze the UI on
        // zoom for heavy drawings. Only genuinely small frames may take that path; everything else is rasterized on the background raster thread
        // (the one-off per-thread start-up cost that motivated the old fallback is gone since the raster thread is persistent).
        if (wantRaster && s_rasterMsPerUnit > 0 && predictedRasterMs > 350 && workUnits < 150) wantRaster = false;
        var rasterTimedOut = false;
        if (wantRaster && !cancellationToken.IsCancellationRequested &&
            TryRasterizeOnStaThread(group, width, height, dpiScale, cancellationToken, workUnits >= (tier switch { 1 => double.MaxValue, 3 => 0d, _ => RasterAliasedWorkUnits }), out rasterTimedOut) is { } rasterImage)
        {
            if (workUnits >= 1) s_rasterMsPerUnit = s_rasterMsPerUnit <= 0 ? s_lastRasterMilliseconds / workUnits : 0.5 * s_rasterMsPerUnit + 0.5 * s_lastRasterMilliseconds / workUnits;
            return rasterImage;
        }
        s_lastFrameTimedOut = rasterTimedOut;
        if (rasterTimedOut)
        {
            // Too heavy to rasterize in time (and a vector image of it would stall the UI thread): show the cheap pixel overlay only
            // (dense frames keep most lines there) and remember that this much work is slow.
            if (workUnits >= 1) s_rasterMsPerUnit = Math.Max(s_rasterMsPerUnit, RasterTimeoutMilliseconds / workUnits);
            var draft = new DrawingGroup();
            using (var draftContext = draft.Open())
            {
                draftContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
                if (tinyOverlay is not null) draftContext.DrawImage(tinyOverlay, new Rect(0, 0, width, height));
            }
            draft.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height));
            draft.Freeze();
            var draftImage = new DrawingImage(draft); draftImage.Freeze();
            return draftImage;
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Renders a frozen vector drawing to a frozen bitmap on a short-lived STA thread. Returns null
    /// (caller keeps the vector image) if the surface is unreasonably large or the render fails.
    /// </summary>
    private sealed class RasterJob
    {
        public bool Aliased; public DrawingGroup Group = null!; public int PixelWidth, PixelHeight; public double Dpi, Width, Height;
        public CancellationToken Token; public DrawingImage? Result; public readonly ManualResetEventSlim Done = new(false);
    }
    private static readonly System.Collections.Concurrent.BlockingCollection<RasterJob> s_rasterQueue = new();
    private static Thread? s_rasterThread;
    private static readonly object s_rasterThreadGate = new();

    /// <summary>One long-lived STA thread rasterizes frames. A fresh thread per frame paid WPF's per-thread dispatcher/render-channel
    /// start-up (~1 s) every time; reusing it makes repeated frames cheap. Queued jobs whose frame was canceled are skipped.</summary>
    private static void EnsureRasterThread()
    {
        if (s_rasterThread is not null) return;
        lock (s_rasterThreadGate)
        {
            if (s_rasterThread is not null) return;
            var thread = new Thread(() =>
            {
                foreach (var job in s_rasterQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (job.Token.IsCancellationRequested) continue;
                        var visual = new DrawingVisual();
                        // Aliased edges rasterize several times faster in WPF's software path; used for heavy frames (AutoCAD also draws unsmoothed vectors).
                        if (job.Aliased) RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
                        using (var visualContext = visual.RenderOpen()) visualContext.DrawDrawing(job.Group);
                        var bitmap = new RenderTargetBitmap(job.PixelWidth, job.PixelHeight, 96 * job.Dpi, 96 * job.Dpi, PixelFormats.Pbgra32);
                        Interlocked.Exchange(ref s_rasterJobStart, Stopwatch.GetTimestamp());
                        bitmap.Render(visual);
                        Interlocked.Exchange(ref s_rasterJobStart, 0);
                        bitmap.Freeze();
                        var drawing = new ImageDrawing(bitmap, new Rect(0, 0, job.Width, job.Height));
                        drawing.Freeze();
                        var image = new DrawingImage(drawing);
                        image.Freeze();
                        job.Result = image;
                    }
                    catch { Interlocked.Exchange(ref s_rasterJobStart, 0); job.Result = null; /* the vector image is still a correct (slower) fallback */ }
                    finally { job.Done.Set(); }
                }
            }) { IsBackground = true, Name = "iStCAD viewport raster", Priority = ThreadPriority.BelowNormal };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            s_rasterThread = thread;
        }
    }

    // Raised from 150: at 150, any view with as few as ~150 visible items (a common, unremarkable amount - one
    // modest floor plan, say) already lost anti-aliasing and looked jagged even at rest (not just mid-gesture,
    // where LowQuality bitmap scaling is separately and intentionally used for speed). 400 keeps the same safety
    // valve for genuinely heavy scenes (the measured-timing escalation in ResolveRenderTier above still forces
    // tier 2/3 - and tier 3 is always aliased regardless of this constant - when rasterizing has measured slow),
    // it only raises the bar for the *tier-2* frames that otherwise had no real reason to sacrifice smooth edges.
    private const double RasterAliasedWorkUnits = 400;
    private const int SoftLineFarLodThreshold = 1500;
    private const int RasterTimeoutMilliseconds = 4000;
    private static volatile bool s_lastFrameTimedOut;
    private const double RasterDegradeBudgetMilliseconds = 1500;
    private static volatile int s_lastDegradedFills;
    private static long s_rasterJobStart; // Stopwatch timestamp of the job being rasterized, 0 when idle

    private static DrawingImage? TryRasterizeOnStaThread(DrawingGroup group, double width, double height, double dpiScale, CancellationToken token, bool aliased, out bool timedOut)
    {
        timedOut = false;
        if (!double.IsFinite(dpiScale) || dpiScale <= 0) dpiScale = 1;
        var pixelWidth = (int)Math.Ceiling(width * dpiScale);
        var pixelHeight = (int)Math.Ceiling(height * dpiScale);
        if (pixelWidth < 1 || pixelHeight < 1 || pixelWidth > MaximumRasterDimension || pixelHeight > MaximumRasterDimension ||
            (long)pixelWidth * pixelHeight > MaximumRasterPixels) return null;
        var rasterStart = Stopwatch.GetTimestamp();
        // A previous job is still grinding (WPF cannot cancel a Render): do not queue behind it, the frame would wait for all of it.
        var busySince = Interlocked.Read(ref s_rasterJobStart);
        if (busySince != 0 && Stopwatch.GetElapsedTime(busySince).TotalMilliseconds > RasterTimeoutMilliseconds) { timedOut = true; return null; }
        EnsureRasterThread();
        var job = new RasterJob { Aliased = aliased, Group = group, PixelWidth = pixelWidth, PixelHeight = pixelHeight, Dpi = dpiScale, Width = width, Height = height, Token = token };
        s_rasterQueue.Add(job);
        if (!job.Done.Wait(RasterTimeoutMilliseconds))
        {
            timedOut = true; s_lastRasterMilliseconds = RasterTimeoutMilliseconds;
            return null;
        }
        s_lastRasterMilliseconds = (int)Stopwatch.GetElapsedTime(rasterStart).TotalMilliseconds;
        return job.Result;
    }

    /// <summary>Builds a one-bitmap overlay of 2x2 pixel dots for entities too small to draw as geometry.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentBag<byte[]> s_pixelBufferPool = new();

    /// <summary>A zeroed pixel buffer. A full-screen Pbgra32 buffer is ~8 MB: allocating one per frame hit the large-object heap and forced
    /// gen2 collections (17 gen2 GCs / 12 s of GC pause in a session), so buffers are recycled.</summary>
    private static byte[] RentPixelBuffer(int length)
    {
        while (s_pixelBufferPool.TryTake(out var buffer))
            if (buffer.Length == length) { Array.Clear(buffer); return buffer; }
        return new byte[length];
    }

    private static void ReturnPixelBuffer(byte[]? buffer)
    {
        if (buffer is not null && s_pixelBufferPool.Count < 3) s_pixelBufferPool.Add(buffer);
    }

    private static BitmapSource? CreateTinyEntityOverlay(byte[]? pixels, int pixelWidth, int pixelHeight, double dpiScale)
    {
        if (pixels is null || pixelWidth < 1 || pixelHeight < 1) return null;
        var overlay = BitmapSource.Create(pixelWidth, pixelHeight, 96 * dpiScale, 96 * dpiScale, PixelFormats.Pbgra32, null, pixels, pixelWidth * 4);
        overlay.Freeze();
        return overlay;
    }

    /// <summary>
    /// Entities that BuildVisibleGeometryAsync deliberately draws as dots, boxes or simplified lines instead of
    /// cached geometry. The selection overlay must not treat their cache miss as "needs another build".
    /// </summary>
    private bool IsRenderSimplifiedAtCurrentScale(CadEntity entity)
    {
        if (!_spatialIndex.TryGetBounds(entity.Id, out var bounds)) return false;
        var pixelWidth = Math.Abs(bounds.MaxX - bounds.MinX) * _viewScale;
        var pixelHeight = Math.Abs(bounds.MaxY - bounds.MinY) * _viewScale;
        if (pixelWidth <= DenseSubPixelEntityLimit && pixelHeight <= DenseSubPixelEntityLimit) return true;
        if ((entity is CadTextEntity or CadMTextEntity) && pixelHeight < MinReadableTextPixels) return true;
        return entity is PolylineEntity polyline && CadGeometryBuilder.ShouldSimplifyPolyline(polyline, _viewScale, pixelWidth, pixelHeight);
    }

    private Border? _regenerateIndicator;

    /// <summary>AutoCAD-style "regenerating" wait state: shown while a viewport frame is being built.</summary>
    private System.Windows.Threading.DispatcherTimer? _regenerateDelayTimer;

    /// <summary>Show the indicator only if a frame takes longer than ~350 ms: fast frames used to flash it on and off.</summary>
    private void SetRegenerating(bool on)
    {
        if (!on) { _regenerateDelayTimer?.Stop(); SetRegeneratingNow(false); return; }
        _regenerateDelayTimer ??= new System.Windows.Threading.DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _regenerateDelayTimer.Tick -= RegenerateDelayTick; _regenerateDelayTimer.Tick += RegenerateDelayTick;
        _regenerateDelayTimer.Stop(); _regenerateDelayTimer.Start();
    }

    private void RegenerateDelayTick(object? sender, EventArgs e)
    {
        _regenerateDelayTimer?.Stop();
        if (_geometryBuildInProgress) SetRegeneratingNow(true);
    }

    private void SetRegeneratingNow(bool on)
    {
        if (DrawingCanvas is null) return;
        if (_regenerateIndicator is null)
        {
            if (!on) return;
            var bar = new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 3, Width = 120, Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(85, 199, 242)), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = "Regenerating…", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(213, 225, 230)) });
            stack.Children.Add(bar);
            _regenerateIndicator = new Border { Child = stack, Padding = new Thickness(9, 5, 9, 6), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1E, 0x2B, 0x31)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Panel.SetZIndex(_regenerateIndicator, 20);
            DrawingCanvas.Children.Add(_regenerateIndicator);
        }
        if (on)
        {
            _regenerateIndicator.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_regenerateIndicator, Math.Max(6, DrawingCanvas.ActualWidth - _regenerateIndicator.DesiredSize.Width - 12));
            Canvas.SetTop(_regenerateIndicator, 10);
        }
        _regenerateIndicator.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private int CurrentGeometryLod => _viewScale >= 0.5 ? 0 : _viewScale >= 0.08 ? 1 : 2;

    private async Task BuildVisibleGeometryAsync(DrawingSession session, long revision, int lod, RenderImageKey targetKey, bool draft = false, Rect? focus = null, bool pixelOnly = false)
    {
        if (pixelOnly) draft = true; // a pixel-only frame is a draft: it never counts as the finished frame for its camera
        var savedRasterMs = s_lastRasterMilliseconds; var savedRasterPerUnit = s_rasterMsPerUnit; // a focus pass must not distort the full-frame speed measurements
        if (draft) lod = 2;
        var buildTimer = Stopwatch.StartNew();
        _geometryBuildInProgress = true;
        SetRegenerating(true);
        _geometryBuildKey = targetKey;
        _renderRequestedDuringBuild = false;
        var generation = ++_geometryBuildGeneration;
        var cancellation = new CancellationTokenSource();
        _geometryBuildCancellation = cancellation;
        var width = DrawingCanvas.ActualWidth;
        var height = DrawingCanvas.ActualHeight;
        var center = _viewCenter;
        var scale = _viewScale;
        var index = _spatialIndex;
        var lightweightBackend = CadViewport.BackendId == "lightweight-wpf";
        var skipTextMTextForPerfTest = _skipTextMTextForPerfTest;
        var dpiScale = Math.Max(0.5, VisualTreeHelper.GetDpi(DrawingCanvas).DpiScaleX);
        // Captured on the UI thread once per document revision. Camera changes only
        // reuse the snapshots, so zoom never re-enumerates large metadata tables.
        if (session.RenderMetadataRevision != revision || session.RenderHiddenSnapshot is null || session.RenderColorSnapshot is null)
        {
            session.RenderHiddenSnapshot = session.Editor.Drawing.HiddenEntityIds.ToHashSet();
            session.RenderColorSnapshot = new Dictionary<string, CadColor>(session.Editor.Drawing.LayerColors, StringComparer.OrdinalIgnoreCase);
            session.RenderMetadataRevision = revision;
        }
        var hidden = session.RenderHiddenSnapshot;
        var colors = session.RenderColorSnapshot;
        var paperMode = IsPaperSpaceActive;
        var importedLayoutViewports = paperMode && session.Editor.Drawing.LayoutViewports.TryGetValue(_activeLayout, out var vpList) ? vpList : (IReadOnlyList<CadLayoutViewport>)Array.Empty<CadLayoutViewport>();
        // VIEWPORT objects created with the VIEWPORT/MVIEW command are proxy entities (so they can be selected/moved/
        // scaled like any other object) rather than entries in LayoutViewports (which is only populated from a native
        // file on import) - merge in a transient CadLayoutViewport per proxy so the same clipped-model-content
        // renderer already used for imported viewports (BuildViewportContent, below) also draws a live preview of
        // what's actually inside a newly-created one.
        var sessionViewports = paperMode
            ? session.Editor.Drawing.Entities.OfType<ProxyCadEntity>().Where(e => e.Viewport is not null)
                .Select((e, i) => new CadLayoutViewport(100000 + i, e.Viewport!.Center.X, e.Viewport.Center.Y, e.Viewport.Width, e.Viewport.Height,
                    e.Viewport.ViewCenter.X, e.Viewport.ViewCenter.Y, e.Viewport.ViewHeight, e.Viewport.FrozenLayers)).ToArray()
            : Array.Empty<CadLayoutViewport>();
        var layoutViewports = importedLayoutViewports.Concat(sessionViewports).ToArray();
        var failed = false;
        var degradedFrame = false;
        var acceptedFrame = false;
        try
        {
            var result = await RunLowPriority(() =>
            {
                var token = cancellation.Token;
                var tier = ResolveRenderTier(index.IndexedEntityCount); s_lastFrameTier = tier;
                var halfWidth = width / (2 * scale); var halfHeight = height / (2 * scale);
                var items = new List<(CadEntity Entity, Geometry Geometry)>();
                var cacheHits = 0;
                var skippedTextMText = 0;
                var minX = center.X - halfWidth; var minY = center.Y - halfHeight;
                var maxX = center.X + halfWidth; var maxY = center.Y + halfHeight;
                if (focus is { } focusArea)
                {
                    // Focus-first pass: only the region around the pointer is queried, built and drawn (full quality, small cost).
                    minX = center.X + (focusArea.Left - width / 2) / scale; maxX = center.X + (focusArea.Right - width / 2) / scale;
                    maxY = center.Y - (focusArea.Top - height / 2) / scale; minY = center.Y - (focusArea.Bottom - height / 2) / scale;
                }
                // Query the R-tree once. Small/medium views retain drawing order; far-LOD views
                // over the sorting threshold skip sorting, while the higher density threshold enables pixel aggregation.
                // This removes a second full tree traversal during zoom-out.
                var stageTimer = Stopwatch.StartNew();
                var candidates = index.QueryAdaptive(minX, minY, maxX, maxY,
                    allowUnorderedWhenDense: lod >= 2, orderedCandidateLimit: OrderedViewportCandidateLimit, cancellationToken: token);
                _lastQueryStageMilliseconds = stageTimer.Elapsed.TotalMilliseconds; stageTimer.Restart();
                if (token.IsCancellationRequested)
                    return (Image: (DrawingImage?)null, VisibleCount: 0, CacheHits: 0, SkippedTextMTextCount: 0, Canceled: true);
                var dense = lod >= 2 && candidates.Length > DenseViewportCandidateLimit;
                // Anything that cannot be seen as more than a dot is drawn into one pixel buffer
                // (a single bitmap for WPF) instead of becoming thousands of vector geometries.
                var tinyLimit = dense ? DenseSubPixelEntityLimit : SubPixelEntityLimit;
                var rasterPixelWidth = (int)Math.Ceiling(width * dpiScale);
                var rasterPixelHeight = (int)Math.Ceiling(height * dpiScale);
                var tinyBufferAllowed = rasterPixelWidth > 0 && rasterPixelHeight > 0 &&
                    (long)rasterPixelWidth * rasterPixelHeight <= MaximumRasterPixels;
                byte[]? tinyPixels = null;
                void MarkTinyEntity(double screenX, double screenY, byte colorB, byte colorG, byte colorR)
                {
                    var px = (int)Math.Floor(screenX * dpiScale);
                    var py = (int)Math.Floor(screenY * dpiScale);
                    if (px < -2 || py < -2 || px >= rasterPixelWidth || py >= rasterPixelHeight) return;
                    tinyPixels ??= RentPixelBuffer(rasterPixelWidth * rasterPixelHeight * 4);
                    for (var oy = 0; oy < 2; oy++)
                        for (var ox = 0; ox < 2; ox++)
                        {
                            var x = px + ox; var y = py + oy;
                            if (x < 0 || y < 0 || x >= rasterPixelWidth || y >= rasterPixelHeight) continue;
                            var offset = (y * rasterPixelWidth + x) * 4;
                            tinyPixels[offset] = colorB; tinyPixels[offset + 1] = colorG; tinyPixels[offset + 2] = colorR; tinyPixels[offset + 3] = 255;
                        }
                }
                var clipMargin = 8 / Math.Max(scale, 1e-12);
                var clipMinX = minX - clipMargin; var clipMinY = minY - clipMargin; var clipMaxX = maxX + clipMargin; var clipMaxY = maxY + clipMargin;
                // Far-LOD views (zoom extents) of big block previews were 8 s of WPF software raster at only ~6,000 candidates: use the pixel path much earlier there.
                // Previous raster was slow (>1.2 s; the value only updates when WPF rasterizes, so this stays sticky while the pixel path keeps frames cheap):
                // plot objects of ANY size into the pixel buffer for this busy view. 28,909 vector figures took 3.7 s; Bresenham takes milliseconds.
                // Tier (see RenderMode.cs): Quality keeps smooth vectors unless a frame is really slow; Fast plots pixel lines early.
                var (softThreshold, farLodThreshold, softAllMs, softAllCandidates) = tier switch
                {
                    1 => (60_000, 6_000, 3_000, 100),
                    3 => (5_000, 400, 300, 50),
                    _ => (SoftLineRasterThreshold, SoftLineFarLodThreshold, 1_200, 100)
                };
                var softAll = pixelOnly ? tinyBufferAllowed : tinyBufferAllowed && s_lastRasterMilliseconds > softAllMs && candidates.Length > softAllCandidates;
                var softLines = tinyBufferAllowed && (softAll || candidates.Length > softThreshold || (lod >= 2 && candidates.Length > farLodThreshold));
                var softColors = new Dictionary<CadColor, (byte B, byte G, byte R)>();
                var softSegments = new SoftSegmentBatch();
                (byte B, byte G, byte R) softLastRgb = (255, 255, 255); string? softLastLayer = null; var softLineCount = 0;
                void SetSoftColor(CadEntity e)
                {
                    if (ReferenceEquals(e.Layer, softLastLayer) && e.ExplicitColor is null) return;
                    softLastLayer = e.Layer;
                    var effective = e.ExplicitColor ?? (colors.TryGetValue(e.Layer, out var softLayerColor) ? softLayerColor : e.DisplayColor);
                    var colorKey = effective ?? new CadColor(255, 255, 255);
                    if (!softColors.TryGetValue(colorKey, out softLastRgb))
                    {
                        var rgb = PaperSafeColor(effective, paperMode);
                        softColors[colorKey] = softLastRgb = (rgb.B, rgb.G, rgb.R);
                    }
                }
                void PlotSegment(Point2 a, Point2 c)
                {
                    if (!ClipSegmentToRect(a, c, clipMinX, clipMinY, clipMaxX, clipMaxY, out var ca, out var cc)) return;
                    tinyPixels ??= RentPixelBuffer(rasterPixelWidth * rasterPixelHeight * 4);
                    softSegments.Attach(tinyPixels, rasterPixelWidth, rasterPixelHeight);
                    softSegments.Add((float)(((ca.X - center.X) * scale + width / 2) * dpiScale), (float)(((center.Y - ca.Y) * scale + height / 2) * dpiScale),
                        (float)(((cc.X - center.X) * scale + width / 2) * dpiScale), (float)(((center.Y - cc.Y) * scale + height / 2) * dpiScale),
                        softLastRgb.B, softLastRgb.G, softLastRgb.R);
                    softLineCount++;
                }
                void PlotRing(Point2 c0, double radius, double startDeg, double sweepDeg)
                {
                    var steps = Math.Clamp((int)(Math.Abs(radius * scale) * 1.5) + 8, 8, 64);
                    var previous = new Point2(c0.X + radius * Math.Cos(startDeg * Math.PI / 180), c0.Y + radius * Math.Sin(startDeg * Math.PI / 180));
                    for (var i = 1; i <= steps; i++)
                    {
                        var angle = (startDeg + sweepDeg * i / steps) * Math.PI / 180;
                        var next = new Point2(c0.X + radius * Math.Cos(angle), c0.Y + radius * Math.Sin(angle));
                        PlotSegment(previous, next); previous = next;
                    }
                }
                void PlotPath(IReadOnlyList<Point2> pts, bool closedPath)
                {
                    for (var i = 1; i < pts.Count; i++) PlotSegment(pts[i - 1], pts[i]);
                    if (closedPath && pts.Count > 2) PlotSegment(pts[^1], pts[0]);
                }
                // Dense frame, small objects: draw their real outlines (1 px, layer color) instead of gray 2x2 blobs or WPF geometry.
                bool TryPlotSoft(CadEntity e, bool hasB, double bMinX, double bMinY, double bMaxX, double bMaxY, double pxW, double pxH)
                {
                    var SmallObjectPixels = softAll ? 1e9 : 200d;
                    switch (e)
                    {
                        case LineEntity line:
                            SetSoftColor(e); PlotSegment(line.Start, line.End); return true;
                        case PolylineEntity pl when hasB && pxW <= SmallObjectPixels && pxH <= SmallObjectPixels && pl.Vertices.Count >= 2:
                        {
                            SetSoftColor(e);
                            // Connecting raw vertices with straight segments silently flattened every bulge (arc)
                            // segment into a chord in this fast "soft segment" path - the only renderer in the app
                            // that did so (CreateBulgedPolylineGeometry/AppendPolylineFigure/PolylineTessellator all
                            // already draw arcs correctly). This is exactly why an arc in a polyline read from a real
                            // AutoCAD file could show as a straight line: dense/complex real-world drawings are the
                            // ones most likely to hit this draft rasterizer. Tessellate first (same bulge math as
                            // everywhere else) so arcs draw as arcs here too. closedPath: false because Tessellate
                            // already walks the wrap-around segment back to the first point for a closed polyline.
                            PlotPath(PolylineTessellator.Tessellate(pl).ToList(), false);
                            return true;
                        }
                        case CircleEntity circle when hasB && pxW <= SmallObjectPixels:
                            SetSoftColor(e); PlotRing(circle.Center, circle.Radius, 0, 360); return true;
                        case ArcEntity arc when hasB && pxW <= SmallObjectPixels:
                        {
                            SetSoftColor(e);
                            var sweep = ((arc.EndAngleDegrees - arc.StartAngleDegrees) % 360 + 360) % 360; if (sweep < 1e-9) sweep = 360;
                            PlotRing(arc.Center, arc.Radius, arc.StartAngleDegrees, sweep); return true;
                        }
                        case ProxyCadEntity proxy when hasB && pxW <= SmallObjectPixels && pxH <= SmallObjectPixels && !proxy.PreviewFilled:
                        {
                            SetSoftColor(e);
                            var drew = false;
                            if (proxy.PreviewPaths is { Count: > 0 }) foreach (var path in proxy.PreviewPaths) { PlotPath(path, proxy.PreviewClosed); drew = true; }
                            if (proxy.ProxyGraphicsPaths is { Count: > 0 }) foreach (var path in proxy.ProxyGraphicsPaths) { PlotPath(path.Points, path.Closed); drew = true; }
                            if (!drew)
                            {
                                var lo = new Point2(Math.Min(bMinX, bMaxX), Math.Min(bMinY, bMaxY)); var hi = new Point2(Math.Max(bMinX, bMaxX), Math.Max(bMinY, bMaxY));
                                PlotPath(new[] { lo, new Point2(hi.X, lo.Y), hi, new Point2(lo.X, hi.Y) }, true);
                            }
                            return true;
                        }
                        case CadTextEntity or CadMTextEntity when hasB && pxH < MinReadableTextPixels:
                            // Unreadable text: a baseline stroke of its true extent (what AutoCAD's far-zoom text looks like).
                            SetSoftColor(e);
                            PlotSegment(new Point2(Math.Min(bMinX, bMaxX), (bMinY + bMaxY) / 2), new Point2(Math.Max(bMinX, bMaxX), (bMinY + bMaxY) / 2));
                            return true;
                    }
                    return false;
                }
                var pendingBuilds = new List<(int Slot, CadEntity Entity, bool HasBounds, double MinX, double MinY, double MaxX, double MaxY)>();
                var slowestGate = new object();
                var visible = 0;
                long buildTicks = 0, slowestTicks = 0; var buildCalls = 0; var degradedItems = 0; var slowestText = "-";
                // Budget doubles each progressive pass (0.5, 1, 2, 4 s ...) and the 5th pass is unlimited, so a cold zoom finishes in a handful of passes.
                var budgetTicks = _frameProgressPasses >= 4 ? long.MaxValue : (long)(Stopwatch.Frequency * GeometryBuildBudgetMilliseconds * (1 << _frameProgressPasses) / 1000.0);
                var directMode = candidates.Length > DirectStreamCandidateThreshold;
                var directStreams = new Dictionary<(CadColor? Color, string Pattern), DirectStyleStream>();
                var directPatternKeys = new Dictionary<(IReadOnlyList<double> Pattern, double Scale), string>();
                foreach (var entity in candidates)
                {
                    if (token.IsCancellationRequested)
                        return (Image: (DrawingImage?)null, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: true);
                    if (hidden.Contains(entity.Id)) continue;
                    visible++;
                    // Test mode: avoid reading text contents, touching the geometry cache,
                    // measuring glyphs, and creating any text geometry during viewport rendering.
                    if (skipTextMTextForPerfTest && (entity is CadTextEntity or CadMTextEntity))
                    {
                        skippedTextMText++;
                        continue;
                    }
                    var hasBounds = index.TryGetBounds(entity.Id, out var b);
                    var pxWidth = hasBounds ? Math.Abs(b.MaxX - b.MinX) * scale : 0d;
                    var pxHeight = hasBounds ? Math.Abs(b.MaxY - b.MinY) * scale : 0d;
                    if (pixelOnly)
                    {
                        // Instant first picture: every entity as 1-px colored strokes (outline box for anything without a stroke form). No WPF geometry, no raster.
                        if (!TryPlotSoft(entity, hasBounds, b.MinX, b.MinY, b.MaxX, b.MaxY, pxWidth, pxHeight) && hasBounds)
                        {
                            SetSoftColor(entity);
                            var boxLo = new Point2(Math.Min(b.MinX, b.MaxX), Math.Min(b.MinY, b.MaxY)); var boxHi = new Point2(Math.Max(b.MinX, b.MaxX), Math.Max(b.MinY, b.MaxY));
                            PlotPath(new[] { boxLo, new Point2(boxHi.X, boxLo.Y), boxHi, new Point2(boxLo.X, boxHi.Y) }, true);
                        }
                        continue;
                    }
                    if (softLines && TryPlotSoft(entity, hasBounds, b.MinX, b.MinY, b.MaxX, b.MaxY, pxWidth, pxHeight)) continue;
                    if (hasBounds && tinyBufferAllowed && pxWidth <= tinyLimit && pxHeight <= tinyLimit && !(softLines && entity is LineEntity))
                    {
                        // Real layer/entity color instead of one fixed gray: gray dots merged into gray "unrendered" blobs in dense areas.
                        SetSoftColor(entity);
                        MarkTinyEntity((0.5 * (b.MinX + b.MaxX) - center.X) * scale + width / 2,
                            (center.Y - 0.5 * (b.MinY + b.MaxY)) * scale + height / 2, softLastRgb.B, softLastRgb.G, softLastRgb.R);
                        continue;
                    }
                    // Unreadable text: never build glyph outlines (FormattedText + BuildGeometry is by far the
                    // most expensive geometry) for text that is only a few pixels tall; draw a thin box instead.
                    if (hasBounds && lod < 2 && pxHeight < MinReadableTextPixels && (entity is CadTextEntity or CadMTextEntity))
                    {
                        var boxMinX = Math.Min(b.MinX, b.MaxX); var boxMaxX = Math.Max(b.MinX, b.MaxX);
                        var boxMinY = Math.Min(b.MinY, b.MaxY); var boxMaxY = Math.Max(b.MinY, b.MaxY);
                        Geometry box = boxMaxX > boxMinX && boxMaxY > boxMinY
                            ? new RectangleGeometry(new Rect(boxMinX, boxMinY, boxMaxX - boxMinX, boxMaxY - boxMinY))
                            : new LineGeometry(new Point(boxMinX, boxMinY), new Point(boxMaxX, boxMaxY));
                        box.Freeze();
                        items.Add((entity, box));
                        continue;
                    }
                    if (softLines && entity is LineEntity softLine)
                    {
                        // Dense frame (tens of thousands of lines): rasterize straight into the pixel buffer. WPF's software stroker needs
                        // seconds for this many figures; a 1-px Bresenham pass takes milliseconds. Pure speed path, geometry/pick unchanged.
                        if (!ReferenceEquals(softLine.Layer, softLastLayer) || softLine.ExplicitColor is not null)
                        {
                            softLastLayer = softLine.Layer;
                            var effective = softLine.ExplicitColor ?? (colors.TryGetValue(softLine.Layer, out var softLayerColor) ? softLayerColor : softLine.DisplayColor);
                            var colorKey = effective ?? new CadColor(255, 255, 255);
                            if (!softColors.TryGetValue(colorKey, out softLastRgb))
                            {
                                var rgb = PaperSafeColor(effective, paperMode);
                                softColors[colorKey] = softLastRgb = (rgb.B, rgb.G, rgb.R);
                            }
                        }
                        if (ClipSegmentToRect(softLine.Start, softLine.End, clipMinX, clipMinY, clipMaxX, clipMaxY, out var softA, out var softB))
                        {
                            tinyPixels ??= RentPixelBuffer(rasterPixelWidth * rasterPixelHeight * 4);
                            softSegments.Attach(tinyPixels, rasterPixelWidth, rasterPixelHeight);
                            softSegments.Add((float)(((softA.X - center.X) * scale + width / 2) * dpiScale), (float)(((center.Y - softA.Y) * scale + height / 2) * dpiScale),
                                (float)(((softB.X - center.X) * scale + width / 2) * dpiScale), (float)(((center.Y - softB.Y) * scale + height / 2) * dpiScale),
                                softLastRgb.B, softLastRgb.G, softLastRgb.R);
                            softLineCount++;
                        }
                        continue;
                    }
                    if (directMode && entity is LineEntity or CircleEntity or ArcEntity or PolylineEntity)
                    {
                        var styleKey = GetEntityStyleKey(entity, colors, directPatternKeys);
                        if (!directStreams.TryGetValue(styleKey, out var stream))
                            directStreams[styleKey] = stream = new DirectStyleStream(entity);
                        var appended = entity switch
                        {
                            // Clip to the viewport first: a line millions of pixels long (stray far-away objects) otherwise makes WPF
                            // stroke/dash-tessellate the whole off-screen length, which dominated multi-second compose times.
                            LineEntity line => ClipSegmentToRect(line.Start, line.End, clipMinX, clipMinY, clipMaxX, clipMaxY, out var clippedA, out var clippedB)
                                ? CadGeometryBuilder.AppendLineFigure(stream.Context, clippedA, clippedB) : false,
                            CircleEntity circle => CadGeometryBuilder.AppendCircleFigure(stream.Context, circle.Center, circle.Radius),
                            ArcEntity arc => CadGeometryBuilder.AppendArcFigure(stream.Context, arc),
                            PolylineEntity polyline => hasBounds && CadGeometryBuilder.ShouldSimplifyPolyline(polyline, scale, pxWidth, pxHeight)
                                ? CadGeometryBuilder.AppendSimplifiedPolylineFigure(stream.Context, polyline, scale, token)
                                : CadGeometryBuilder.AppendPolylineFigure(stream.Context, polyline, token),
                            _ => false
                        };
                        if (appended) stream.Count++;
                        continue;
                    }
                    // Huge polylines with many vertices per pixel: feed WPF only the vertices that can be seen.
                    if (hasBounds && entity is PolylineEntity bigPolyline && CadGeometryBuilder.ShouldSimplifyPolyline(bigPolyline, scale, pxWidth, pxHeight))
                    {
                        var simplified = CadGeometryBuilder.BuildScreenSimplifiedPolylineGeometry(bigPolyline, scale, token);
                        if (token.IsCancellationRequested)
                            return (Image: (DrawingImage?)null, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: true);
                        if (!simplified.IsFrozen && simplified.CanFreeze) simplified.Freeze();
                        items.Add((entity, simplified));
                        continue;
                    }
                    if (_geometryCache.TryGet(entity, lod, out var geometry, token, shareByContent: true)) cacheHits++;
                    else
                    {
                        var usedTextProxy = false;
                        // At far zoom, glyph outlines are expensive but add little visual
                        // information. Use the spatial-index envelope as a hollow text proxy;
                        // the actual entity remains unchanged for selection, snapping and edits.
                        if (lod >= 2 && (entity is CadTextEntity or CadMTextEntity) && index.TryGetBounds(entity.Id, out var textBounds) &&
                            Math.Abs(textBounds.MaxY - textBounds.MinY) * scale < FarTextBoxPixels)
                        {
                            usedTextProxy = true;
                            var minTextX = Math.Min(textBounds.MinX, textBounds.MaxX);
                            var minTextY = Math.Min(textBounds.MinY, textBounds.MaxY);
                            var maxTextX = Math.Max(textBounds.MinX, textBounds.MaxX);
                            var maxTextY = Math.Max(textBounds.MinY, textBounds.MaxY);
                            geometry = maxTextX > minTextX && maxTextY > minTextY
                                ? new RectangleGeometry(new Rect(minTextX, minTextY, maxTextX - minTextX, maxTextY - minTextY))
                                : CadGeometryBuilder.BuildEntityGeometry(entity, lod, token) ?? Geometry.Empty;
                        }
                        else if (buildTicks > budgetTicks && hasBounds && entity is not (LineEntity or CircleEntity or ArcEntity))
                        {
                            // Cold-cache budget spent: draw a hollow placeholder now and finish the real geometry in the next
                            // progressive pass (everything built so far is cached, so each pass makes progress).
                            usedTextProxy = true; degradedFrame = true; degradedItems++;
                            var bx0 = Math.Min(b.MinX, b.MaxX); var bx1 = Math.Max(b.MinX, b.MaxX);
                            var by0 = Math.Min(b.MinY, b.MaxY); var by1 = Math.Max(b.MinY, b.MaxY);
                            geometry = bx1 > bx0 && by1 > by0 ? new RectangleGeometry(new Rect(bx0, by0, bx1 - bx0, by1 - by0)) : new LineGeometry(new Point(bx0, by0), new Point(bx1, by1));
                        }
                        else
                        {
                            // Cold geometry is built in parallel after the scan (see below); keep the painter's-order slot now.
                            pendingBuilds.Add((items.Count, entity, hasBounds, b.MinX, b.MinY, b.MaxX, b.MaxY));
                            items.Add((entity, Geometry.Empty));
                            continue;
                        }
                        if (token.IsCancellationRequested)
                            return (Image: (DrawingImage?)null, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: true);
                        if (!geometry.IsFrozen) geometry.Freeze();
                        if (!usedTextProxy) _geometryCache.Add(entity, lod, geometry, token); // never cache the proxy box: it would stick after zooming in
                    }
                    items.Add((entity, geometry));
                }
                if (pendingBuilds.Count > 0 && !token.IsCancellationRequested)
                {
                    // Build all cache misses across cores (the geometry cache is locked; builders have no shared mutable state).
                    // The time budget still applies: once spent, remaining non-trivial entities become hollow placeholders.
                    var deadline = budgetTicks == long.MaxValue ? long.MaxValue : Stopwatch.GetTimestamp() + budgetTicks;
                    var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4); // leave cores for the UI and the OS on big files
                    Parallel.For(0, pendingBuilds.Count, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = parallelism }, pendingIndex =>
                    {
                        var job = pendingBuilds[pendingIndex];
                        if (token.IsCancellationRequested) return;
                        if (Stopwatch.GetTimestamp() > deadline && job.HasBounds && job.Entity is not (LineEntity or CircleEntity or ArcEntity))
                        {
                            var bx0 = Math.Min(job.MinX, job.MaxX); var bx1 = Math.Max(job.MinX, job.MaxX);
                            var by0 = Math.Min(job.MinY, job.MaxY); var by1 = Math.Max(job.MinY, job.MaxY);
                            Geometry box = bx1 > bx0 && by1 > by0 ? new RectangleGeometry(new Rect(bx0, by0, bx1 - bx0, by1 - by0)) : new LineGeometry(new Point(bx0, by0), new Point(bx1, by1));
                            box.Freeze();
                            items[job.Slot] = (job.Entity, box);
                            degradedFrame = true; Interlocked.Increment(ref degradedItems);
                            return;
                        }
                        var buildStart = Stopwatch.GetTimestamp();
                        var built = CadGeometryBuilder.BuildEntityGeometry(job.Entity, lod, token) ?? Geometry.Empty;
                        var spent = Stopwatch.GetTimestamp() - buildStart;
                        if (token.IsCancellationRequested) return;
                        if (!built.IsFrozen && built.CanFreeze) built.Freeze();
                        _geometryCache.Add(job.Entity, lod, built, token);
                        items[job.Slot] = (job.Entity, built);
                        Interlocked.Add(ref buildTicks, spent); Interlocked.Increment(ref buildCalls);
                        if (spent > Interlocked.Read(ref slowestTicks))
                            lock (slowestGate)
                                if (spent > slowestTicks)
                                {
                                    slowestTicks = spent;
                                    slowestText = $"{job.Entity.GetType().Name}{(job.Entity is ProxyCadEntity pe ? ":" + pe.NativeType : "")} {Stopwatch.GetElapsedTime(0, spent).TotalMilliseconds:0.#} ms";
                                }
                    });
                    if (token.IsCancellationRequested)
                        return (Image: (DrawingImage?)null, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: true);
                }
                _lastGeometryStageMilliseconds = stageTimer.Elapsed.TotalMilliseconds; stageTimer.Restart(); _lastFrameItemCount = items.Count;
                _lastBuildCalls = buildCalls; _lastBuildMilliseconds = Stopwatch.GetElapsedTime(0, buildTicks).TotalMilliseconds; _slowestBuild = slowestText; _lastDegradedItems = degradedItems;
                var directFigureCount = 0;
                var directLayers = new List<(Geometry Geometry, Pen Pen)>(directStreams.Count);
                foreach (var (styleKey, stream) in directStreams)
                {
                    stream.Context.Close();
                    if (stream.Count == 0 || token.IsCancellationRequested) continue;
                    stream.Geometry.Freeze();
                    directFigureCount += stream.Count;
                    var rgb = PaperSafeColor(styleKey.Color, paperMode);
                    var strokeBrush = new SolidColorBrush(rgb); strokeBrush.Freeze();
                    directLayers.Add((stream.Geometry, CreateWorldPen(strokeBrush, stream.Sample.LineTypePattern, stream.Sample.LineTypeScale, scale, false)));
                }
                _lastDirectFigures = directFigureCount + softLineCount; _lastDirectStyles = directLayers.Count;
                if ((softSegments.Count > 0 || softSegments.DotCount > 0) && !token.IsCancellationRequested)
                {
                    tinyPixels ??= RentPixelBuffer(rasterPixelWidth * rasterPixelHeight * 4);
                    FlushSoftSegments(softSegments, tinyPixels, rasterPixelWidth, rasterPixelHeight); // one native, anti-aliased pass per color
                }
                var tinyOverlay = CreateTinyEntityOverlay(tinyPixels, rasterPixelWidth, rasterPixelHeight, dpiScale);
                ReturnPixelBuffer(tinyPixels); // BitmapSource.Create copied the pixels; keep the 8 MB buffer for the next frame instead of feeding the LOH/gen2 GC
                if (pixelOnly)
                {
                    if (!tinyBufferAllowed || token.IsCancellationRequested)
                        return (Image: (DrawingImage?)null, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: true);
                    var pixelGroup = new DrawingGroup();
                    using (var pixelContext = pixelGroup.Open())
                    {
                        pixelContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
                        if (tinyOverlay is not null) pixelContext.DrawImage(tinyOverlay, new Rect(0, 0, width, height));
                    }
                    pixelGroup.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height));
                    pixelGroup.Freeze();
                    var pixelImage = new DrawingImage(pixelGroup); pixelImage.Freeze();
                    return (Image: (DrawingImage?)pixelImage, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: false);
                }
                var image = ComposeRenderImage(items, colors, width, height, center, scale, token, lightweightBackend, tinyOverlay, dpiScale, directLayers, paperMode, directFigureCount, tier);
                if (layoutViewports.Length > 0 && !token.IsCancellationRequested)
                {
                    var viewportGroups = BuildViewportContent(session, layoutViewports, hidden, colors, width, height, center, scale, token);
                    if (viewportGroups.Count > 0)
                    {
                        var combined = new DrawingGroup();
                        foreach (var g in viewportGroups) combined.Children.Add(g);
                        combined.Children.Add(image.Drawing);
                        combined.Freeze();
                        image = new DrawingImage(combined); image.Freeze();
                    }
                }
                _lastComposeStageMilliseconds = stageTimer.Elapsed.TotalMilliseconds;
                if (token.IsCancellationRequested)
                    return (Image: (DrawingImage?)null, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: true);
                return (Image: (DrawingImage?)image, VisibleCount: visible, CacheHits: cacheHits, SkippedTextMTextCount: skippedTextMText, Canceled: false);
            });
            if (focus is { } focusRegion)
            {
                s_lastRasterMilliseconds = savedRasterMs; s_rasterMsPerUnit = savedRasterPerUnit;
                if (!result.Canceled && result.Image is not null && !cancellation.IsCancellationRequested && ReferenceEquals(session, _activeSession) &&
                    revision == session.GeometryRevision && targetKey == GetRenderImageKey() && !_wheelDebounceTimer.IsEnabled && !_isPanning && !_viewportClosed)
                    ShowFocusPatch(result.Image, focusRegion, width, height);
            }
            else if (!result.Canceled && result.Image is not null && !cancellation.IsCancellationRequested && ReferenceEquals(session, _activeSession) && revision == session.GeometryRevision && ReferenceEquals(index, _spatialIndex) && targetKey == GetRenderImageKey() && !_wheelDebounceTimer.IsEnabled && !_isPanning && !_viewportClosed)
            {
                _lastVisibleEntityCount = result.VisibleCount;
                if (result.VisibleCount == 0 && !_emptyViewRecovered && !IsPaperSpaceActive && index.TryGetExtents(out var recoverExtents))
                {
                    // Index has geometry but this view shows none (stale/odd fit): zoom to extents once and say why.
                    _emptyViewRecovered = true;
                    AppendCommandHistory($"VIEW: no entity in view (center {_viewCenter.X:0.##},{_viewCenter.Y:0.##} scale {_viewScale:0.####}); drawing extents {recoverExtents.MinX:0.##},{recoverExtents.MinY:0.##} → {recoverExtents.MaxX:0.##},{recoverExtents.MaxY:0.##}, {_spatialIndex.IndexedEntityCount:N0} indexed. Zoomed to extents.");
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (!_viewportClosed) { FitView(); RenderDrawing(false); } }));
                }
                _lastGeometryCacheHits = result.CacheHits;
                _lastSkippedTextMTextCount = result.SkippedTextMTextCount;
                _lastRenderImage = result.Image;
                _lastRenderImageKey = draft || degradedFrame ? null : targetKey;
                if (!draft && !degradedFrame && !s_lastFrameTimedOut) StoreFrame(session, targetKey, result.Image);
                _frameProgressPasses = degradedFrame ? _frameProgressPasses + 1 : 0;
                if (draft) _refineKey = targetKey;
                DrawingGeometryImage.Source = result.Image;
                HideFocusPatch(animated: true); // the full frame has arrived: cross-fade the focus patch away
                acceptedFrame = true;
                DrawingGeometryImage.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
                MarkInitialViewportRendered(session);
                ResetPanPreview();
                RenderSelectionOverlay(); RenderPreview(); ResetWheelZoomPreview();
            }
        }
        catch (OperationCanceledException ex) when (cancellation.IsCancellationRequested || ex.CancellationToken == cancellation.Token)
        {
            // Fallback for dependencies that still use ThrowIfCancellationRequested.
            // The normal viewport loops now use cooperative checks to avoid first-chance
            // exception breaks during routine zoom/pan refreshes.
        }
        catch (OperationCanceledException ex)
        {
            // Cancellation from an unrelated dependency is not automatically a stale frame.
            failed = true;
            if (ReferenceEquals(session, _activeSession)) AppendCommandHistory($"Geometry worker cancelled unexpectedly: {ex.Message}");
        }
        catch (Exception ex)
        {
            // A deterministic geometry exception must not repeatedly restart the
            // same frame and spin the UI thread indefinitely.
            failed = true;
            if (ReferenceEquals(session, _activeSession)) AppendCommandHistory($"Geometry worker failed: {ex.Message}");
        }
        finally
        {
            buildTimer.Stop();
            if (acceptedFrame)
            {
                CadViewport.Performance.RecordCompletedFrame(buildTimer.Elapsed);
                _renderBuildSamples.Enqueue(buildTimer.Elapsed.TotalMilliseconds);
                while (_renderBuildSamples.Count > 120) _renderBuildSamples.Dequeue();
            }
            _lastGeometryBuildMilliseconds = buildTimer.Elapsed.TotalMilliseconds;
            if (acceptedFrame && !draft) _lastFullBuildMilliseconds = _lastGeometryBuildMilliseconds;
            _lastRenderMilliseconds = _lastGeometryBuildMilliseconds;
            if (generation == _geometryBuildGeneration)
            {
                _geometryBuildInProgress = false;
                SetRegenerating(false);
                _geometryBuildCancellation = null;
                _geometryBuildKey = null;
            }
            cancellation.Dispose();
            // A tab switch can arrive while the old worker owns the slot. Restart for
            // the current session as well; otherwise the new tab can stay blank.
            // Only schedule another pass for a genuinely newer camera/document key.
            // A cancelled worker must not create an endless same-frame retry loop.
            // Always service the latest requested key after a canceled stale worker. In
            // particular, a zoom out/in can cancel a build and return to the same key;
            // comparing only old/new keys previously left that viewport without a worker.
            var requestedWhileBuilding = _renderRequestedDuringBuild;
            _renderRequestedDuringBuild = false;
            if (!failed && !_viewportClosed && !_isPanning && !_wheelDebounceTimer.IsEnabled &&
                (requestedWhileBuilding || _lastRenderImageKey != GetRenderImageKey()))
            {
                // Defer the restart one dispatcher render turn. This handles zoom-out/in
                // returning to the original camera key after the original worker was canceled.
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    if (!_viewportClosed && !_isPanning && !_wheelDebounceTimer.IsEnabled && !_geometryBuildInProgress &&
                        _lastRenderImageKey != GetRenderImageKey())
                        RenderDrawing(false);
                    else if (!_viewportClosed && !_geometryBuildInProgress && _lastRenderImageKey == GetRenderImageKey())
                        DrawingGeometryImage.Source = _lastRenderImage;
                }));
            }
        }
    }

    /// <summary>Draws the model inside each paper-space viewport (clipped, at the viewport's scale) beneath the layout's own objects.</summary>
    private List<DrawingGroup> BuildViewportContent(DrawingSession session, IReadOnlyList<CadLayoutViewport> viewports, HashSet<Guid> hidden,
        IReadOnlyDictionary<string, CadColor> colors, double width, double height, Point2 center, double scale, CancellationToken token)
    {
        var groups = new List<DrawingGroup>();
        var modelIndex = session.ModelViewportIndex ??= new CadSpatialIndex(session.Editor.Drawing.GetSpaceEntities("Model"), excludedEntityIds: hidden);
        foreach (var vp in viewports)
        {
            if (token.IsCancellationRequested) break;
            var vs = vp.Scale; var eff = scale * vs;
            if (!(vs > 0) || !double.IsFinite(eff) || eff <= 0) continue;
            var rect = new Rect(new Point(width / 2 + (vp.PaperX - vp.Width / 2 - center.X) * scale, height / 2 - (vp.PaperY + vp.Height / 2 - center.Y) * scale),
                                new Size(vp.Width * scale, vp.Height * scale));
            if (!rect.IntersectsWith(new Rect(0, 0, width, height)) || rect.Width < 2 || rect.Height < 2) continue;
            var lod = eff >= 0.5 ? 0 : eff >= 0.08 ? 1 : 2;
            double hw = vp.Width / (2 * vs), hh = vp.Height / (2 * vs);
            var candidates = modelIndex.QueryAdaptive(vp.ViewX - hw, vp.ViewY - hh, vp.ViewX + hw, vp.ViewY + hh,
                allowUnorderedWhenDense: lod >= 2, orderedCandidateLimit: OrderedViewportCandidateLimit, cancellationToken: token);
            var frozen = vp.FrozenLayers.Count > 0 ? new HashSet<string>(vp.FrozenLayers, StringComparer.OrdinalIgnoreCase) : null;
            var patternKeys = new Dictionary<(IReadOnlyList<double> Pattern, double Scale), string>();
            var styles = new Dictionary<(CadColor? Color, string Pattern), (GeometryGroup Group, Pen Pen, bool Fill)>();
            var used = 0;
            foreach (var entity in candidates)
            {
                if (token.IsCancellationRequested) break;
                if (++used > 80_000) break;
                if (hidden.Contains(entity.Id) || (frozen is not null && frozen.Contains(entity.Layer))) continue;
                var textHeight = entity switch { CadTextEntity t => t.Height, CadMTextEntity t => t.Height, _ => double.PositiveInfinity };
                if (textHeight * eff < 1.5) continue; // unreadable text inside a viewport
                if (!_geometryCache.TryGet(entity, lod, out var geometry, token, shareByContent: true))
                {
                    geometry = CadGeometryBuilder.BuildEntityGeometry(entity, lod, token) ?? Geometry.Empty;
                    if (!geometry.IsFrozen && geometry.CanFreeze) geometry.Freeze();
                    _geometryCache.Add(entity, lod, geometry, token);
                }
                var baseKey = GetEntityStyleKey(entity, colors, patternKeys);
                var fill = entity is CadTextEntity or CadMTextEntity && geometry is not (RectangleGeometry or LineGeometry);
                var key = fill ? (baseKey.Color, "fill:" + baseKey.Pattern) : baseKey;
                if (!styles.TryGetValue(key, out var style))
                {
                    var brush = new SolidColorBrush(PaperSafeColor(key.Color, true)); brush.Freeze();
                    var pen = fill ? new Pen(brush, 0) : CreateWorldPen(brush, entity.LineTypePattern, entity.LineTypeScale, eff, entity is ProxyCadEntity && entity.LineTypePattern is not { Count: > 0 });
                    if (pen.CanFreeze) pen.Freeze();
                    style = (new GeometryGroup(), pen, fill);
                    styles[key] = style;
                }
                style.Group.Children.Add(geometry);
            }
            if (token.IsCancellationRequested) break;
            var group = new DrawingGroup();
            using (var context = group.Open())
            {
                context.PushClip(new RectangleGeometry(rect));
                context.PushTransform(new MatrixTransform(new Matrix(eff, 0, 0, -eff,
                    width / 2 + (vp.PaperX - vp.ViewX * vs - center.X) * scale, height / 2 - (vp.PaperY - vp.ViewY * vs - center.Y) * scale)));
                foreach (var (styleGroup, stylePen, fillStyle) in styles.Values)
                    if (styleGroup.Children.Count > 0) context.DrawGeometry(fillStyle ? stylePen.Brush : null, stylePen, styleGroup);
                context.Pop(); context.Pop();
            }
            group.Freeze();
            groups.Add(group);
        }
        return groups;
    }

    /// <summary>Double-click inside a layout viewport: jump to Model space showing that viewport's view (like MSPACE).</summary>
    private bool TryEnterModelFromViewport(Point screenPoint)
    {
        if (!IsPaperSpaceActive || !_editor.Drawing.LayoutViewports.TryGetValue(_activeLayout, out var viewports)) return false;
        var paper = ScreenToWorld(screenPoint);
        var vp = viewports.LastOrDefault(v => v.ContainsPaperPoint(paper.X, paper.Y));
        if (vp is null) return false;
        var vs = vp.Scale;
        var targetScale = Math.Clamp(_viewScale * vs, MinViewportScale, MaxViewportScale);
        SwitchActiveSpace("Model");
        _viewCenter = new Point2(vp.ViewX, vp.ViewY);
        _viewScale = targetScale;
        UpdateZoomControls(); RenderDrawing(false);
        AppendCommandHistory($"Model space — view of viewport {vp.Id} (scale 1:{(vs > 0 ? 1 / vs : 0):0.###}). Use a layout tab to return.");
        return true;
    }

    private RenderImageKey GetRenderImageKey() => new(_activeSession.GeometryRevision, _viewCenter.X, _viewCenter.Y, _viewScale, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight, _skipTextMTextForPerfTest);

    /// <summary>
    /// After the first frame, prepare glyph/polyline/arc geometry for LOD 0/1 on a background thread so the first zooms and
    /// pans hit the cache instead of building text outlines on demand (the main cost of slow zoom frames).
    /// </summary>
    private void WarmGeometryCache(DrawingSession session)
    {
        var entities = session.Editor.Drawing.Entities;
        if (entities.Count == 0 || entities.Count > 400_000) return;
        var revision = session.GeometryRevision;
        var snapshot = entities.ToArray();
        _ = Task.Run(() =>
        {
            var warmed = 0;
            foreach (var entity in snapshot)
            {
                if (session.GeometryRevision != revision || _viewportClosed) return;
                if (entity is not (CadTextEntity or CadMTextEntity or PolylineEntity or ArcEntity or CircleEntity)) continue;
                for (var lod = 0; lod <= 1; lod++)
                {
                    if (_geometryCache.TryGet(entity, lod, out _)) continue;
                    var geometry = CadGeometryBuilder.BuildEntityGeometry(entity, lod);
                    if (geometry is null) continue;
                    if (!geometry.IsFrozen && geometry.CanFreeze) geometry.Freeze();
                    _geometryCache.Add(entity, lod, geometry);
                }
                if (++warmed >= 60_000) return; // stay inside the cache budget; keep what the viewport needs
            }
        });
    }

    private void MarkInitialViewportRendered(DrawingSession session)
    {
        if (!session.TrackInitialViewport || session.InitialViewportStartTimestamp == 0) return;
        _lastFirstViewportMilliseconds = Stopwatch.GetElapsedTime(session.InitialViewportStartTimestamp).TotalMilliseconds;
        session.TrackInitialViewport = false;
        session.InitialViewportStartTimestamp = 0;
        AppendCommandHistory($"First viewport ready in {_lastFirstViewportMilliseconds:0} ms.");
        WarmGeometryCache(session);
        // OSNAP candidates are intentionally deferred until the first draw/edit command.
        // If the user started a command before the first frame completed, service that
        // explicit request now; ordinary opening/pointer movement still does no full scan.
        if (IsDynamicCommandActive) QueueSnapCandidateBuild(session);
    }

    private void CancelGeometryBuild()
    {
        _geometryBuildCancellation?.Cancel();

        _lastRenderImageKey = null;
        _lastSelectionOverlayKey = null;
    }

    private const int DenseSelectionHighlightThreshold = 1500;

    private void RenderSelectionOverlay()
    {
        if (SelectionOverlayImage is null || DrawingCanvas is null) return;
        var renderStart = Stopwatch.GetTimestamp();
        if (_selectedEntityIds.Count == 0)
        {
            SelectionOverlayImage.Source = null; _renderedSelectionIds.Clear();
            _lastSelectionOverlayKey = null; _selectionOverlayIncrementalDepth = 0;
            _lastSelectionOverlayMilliseconds = Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
            return;
        }
        var width = DrawingCanvas.ActualWidth; var height = DrawingCanvas.ActualHeight;
        SelectionOverlayImage.Width = width; SelectionOverlayImage.Height = height;
        // Defer enormous highlight sets instead of blocking UI for every camera update.
        // Selection IDs are untouched; the overlay will be rebuilt after selection narrows.
        if (_selectedEntityIds.Count > 25000)
        {
            SelectionOverlayImage.Source = null;
            _lastSelectionOverlayKey = null;
            _lastSelectionOverlayMilliseconds = Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
            return;
        }
        var selectionHash = 17;
        foreach (var id in _selectedEntityIds) selectionHash = unchecked(selectionHash * 31 + id.GetHashCode());
        var key = new SelectionOverlayKey(_activeSession.GeometryRevision, selectionHash, _viewCenter.X, _viewCenter.Y, _viewScale, width, height);
        if (_lastSelectionOverlayKey == key && _renderedSelectionIds.SetEquals(_selectedEntityIds)) return;
        _lastDirtySelectionRegion = ComputeSelectionDirtyRegion();
        var previousKey = _lastSelectionOverlayKey;
        var previousImage = SelectionOverlayImage.Source as DrawingImage;
        var sameViewport = previousKey is { } prior && prior.Revision == key.Revision && prior.CenterX == key.CenterX &&
            prior.CenterY == key.CenterY && prior.Scale == key.Scale && prior.Width == key.Width && prior.Height == key.Height;
        var viewport = new Rect(0, 0, width, height);
        var canIncremental = sameViewport && previousImage is not null && _selectionOverlayIncrementalDepth < 12 && !_lastDirtySelectionRegion.IsEmpty;
        if (canIncremental)
        {
            _lastDirtySelectionRegion.Inflate(Math.Max(8, _gripSize), Math.Max(8, _gripSize));
            _lastDirtySelectionRegion.Intersect(viewport);
            canIncremental = !_lastDirtySelectionRegion.IsEmpty &&
                _lastDirtySelectionRegion.Width * _lastDirtySelectionRegion.Height <= width * height * 0.30;
        }
        var query = canIncremental
            ? new Rect(_lastDirtySelectionRegion.Left, _lastDirtySelectionRegion.Top, _lastDirtySelectionRegion.Width, _lastDirtySelectionRegion.Height)
            : viewport;
        var queryFirst = ScreenToWorld(new Point(query.Left, query.Bottom));
        var querySecond = ScreenToWorld(new Point(query.Right, query.Top));
        var selectedInRegion = new List<CadEntity>(_selectedEntityIds.Count);
        foreach (var id in _selectedEntityIds)
        {
            if (_editor.Drawing.HiddenEntityIds.Contains(id) || !_spatialIndex.TryGetBounds(id, out var bounds)) continue;
            if (bounds.MaxX < Math.Min(queryFirst.X, querySecond.X) || bounds.MinX > Math.Max(queryFirst.X, querySecond.X) ||
                bounds.MaxY < Math.Min(queryFirst.Y, querySecond.Y) || bounds.MinY > Math.Max(queryFirst.Y, querySecond.Y)) continue;
            if (_spatialIndex.TryGetEntity(id, out var entity)) selectedInRegion.Add(entity);
        }
        var visibleSelected = selectedInRegion.ToArray();
        var lod = CurrentGeometryLod;
        // Selected geometry is resolved right here (cache, or built on demand below) so the highlight never waits
        // for - or depends on - the viewport worker, which may be busy, cancelled or draw lines without caching them.
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            if (canIncremental && previousImage is not null)
            {
                context.PushClip(CreateOutsideClip(_lastDirtySelectionRegion, width, height));
                context.DrawImage(previousImage, viewport);
                context.Pop();
                context.PushClip(new RectangleGeometry(_lastDirtySelectionRegion));
            }
            context.PushTransform(new MatrixTransform(new Matrix(_viewScale, 0, 0, -_viewScale,
                width / 2 - _viewCenter.X * _viewScale, height / 2 + _viewCenter.Y * _viewScale)));
            var pen = CreateWorldPen(Brushes.Orange, dashed: false);
            var proxyPen = CreateWorldPen(Brushes.Orange, dashed: true);
            // Thousands of individually drawn highlight geometries are marshaled to the render thread on the UI thread (a visible freeze on
            // zoom with Select All). Past a threshold, highlight with ONE geometry of bounding boxes, like AutoCAD's cheap selection preview.
            var denseSelection = visibleSelected.Length > DenseSelectionHighlightThreshold;
            if (denseSelection)
            {
                var boxes = new StreamGeometry();
                using (var boxContext = boxes.Open())
                    foreach (var selected in visibleSelected)
                    {
                        if (!_spatialIndex.TryGetBounds(selected.Id, out var b)) continue;
                        double x0 = Math.Min(b.MinX, b.MaxX), x1 = Math.Max(b.MinX, b.MaxX), y0 = Math.Min(b.MinY, b.MaxY), y1 = Math.Max(b.MinY, b.MaxY);
                        if ((x1 - x0) * _viewScale < 1) x1 = x0 + 1 / _viewScale;
                        if ((y1 - y0) * _viewScale < 1) y1 = y0 + 1 / _viewScale;
                        boxContext.BeginFigure(new Point(x0, y0), false, true);
                        boxContext.LineTo(new Point(x1, y0), true, false); boxContext.LineTo(new Point(x1, y1), true, false); boxContext.LineTo(new Point(x0, y1), true, false);
                    }
                boxes.Freeze();
                context.DrawGeometry(null, pen, boxes);
            }
            else
            foreach (var entity in visibleSelected)
            {
                var drawPen = entity is ProxyCadEntity ? proxyPen : pen;
                if (_geometryCache.TryGet(entity, lod, out var geometry))
                    context.DrawGeometry(null, drawPen, geometry);
                else if (entity is PolylineEntity bigSelectedPolyline && _spatialIndex.TryGetBounds(entity.Id, out var polylineBounds) &&
                    CadGeometryBuilder.ShouldSimplifyPolyline(bigSelectedPolyline, _viewScale,
                        Math.Abs(polylineBounds.MaxX - polylineBounds.MinX) * _viewScale, Math.Abs(polylineBounds.MaxY - polylineBounds.MinY) * _viewScale))
                    context.DrawGeometry(null, drawPen, CadGeometryBuilder.BuildScreenSimplifiedPolylineGeometry(bigSelectedPolyline, _viewScale));
                else if (visibleSelected.Length <= 400 && !IsRenderSimplifiedAtCurrentScale(entity) &&
                    CadGeometryBuilder.BuildEntityGeometry(entity, lod) is { } builtGeometry)
                {
                    if (builtGeometry.CanFreeze && !builtGeometry.IsFrozen) builtGeometry.Freeze();
                    if (builtGeometry.IsFrozen) _geometryCache.Add(entity, lod, builtGeometry);
                    context.DrawGeometry(null, drawPen, builtGeometry);
                }
                else if (_spatialIndex.TryGetBounds(entity.Id, out var selectedBounds))
                    // Dot/box stand-in: highlight the entity's envelope when its full geometry is not cached.
                    context.DrawRectangle(null, drawPen, new Rect(
                        new Point(Math.Min(selectedBounds.MinX, selectedBounds.MaxX), Math.Min(selectedBounds.MinY, selectedBounds.MaxY)),
                        new Point(Math.Max(selectedBounds.MinX, selectedBounds.MaxX), Math.Max(selectedBounds.MinY, selectedBounds.MaxY))));
            }
            context.Pop();
            if (!denseSelection) DrawSelectionGrips(context, visibleSelected);
            if (canIncremental) context.Pop();
        }
        group.ClipGeometry = new RectangleGeometry(new Rect(0, 0, width, height));
        group.Freeze();
        var image = new DrawingImage(group); image.Freeze();
        SelectionOverlayImage.Source = image;
        SelectionOverlayImage.Clip = new RectangleGeometry(new Rect(0, 0, width, height));
        _renderedSelectionIds.Clear(); _renderedSelectionIds.UnionWith(_selectedEntityIds);
        _lastSelectionOverlayKey = key;
        _selectionOverlayIncrementalDepth = canIncremental ? _selectionOverlayIncrementalDepth + 1 : 0;
        _lastSelectionOverlayMilliseconds = Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds;
    }

    private static Geometry CreateOutsideClip(Rect dirty, double width, double height)
    {
        var clip = new GeometryGroup();
        if (dirty.Top > 0) clip.Children.Add(new RectangleGeometry(new Rect(0, 0, width, dirty.Top)));
        if (dirty.Bottom < height) clip.Children.Add(new RectangleGeometry(new Rect(0, dirty.Bottom, width, height - dirty.Bottom)));
        if (dirty.Left > 0) clip.Children.Add(new RectangleGeometry(new Rect(0, dirty.Top, dirty.Left, dirty.Height)));
        if (dirty.Right < width) clip.Children.Add(new RectangleGeometry(new Rect(dirty.Right, dirty.Top, width - dirty.Right, dirty.Height)));
        clip.Freeze();
        return clip;
    }

    private Rect ComputeSelectionDirtyRegion()
    {
        var changed = new HashSet<Guid>(_renderedSelectionIds);
        changed.SymmetricExceptWith(_selectedEntityIds);
        var region = Rect.Empty;
        foreach (var id in changed)
        {
            if (!_spatialIndex.TryGetBounds(id, out var bounds)) continue;
            var a = WorldToScreen(new Point2(bounds.MinX, bounds.MaxY));
            var b = WorldToScreen(new Point2(bounds.MaxX, bounds.MinY));
            var rect = new Rect(a, b);
            region.Union(rect);
        }
        return region;
    }

    private static Point ToPoint(Point2 point) => new(point.X, point.Y);

    private Pen CreateWorldPen(Brush brush, bool dashed) => CreateWorldPen(brush, dashed, _viewScale);

    private static Pen CreateWorldPen(Brush brush, bool dashed, double scale)
    {
        scale = Math.Clamp(scale, MinViewportScale, MaxViewportScale);
        var pen = new Pen(brush, 1.25 / scale);
        // WPF dash lengths are multiples of pen thickness, not drawing units.
        if (dashed) pen.DashStyle = new DashStyle([4, 3], 0);
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static Pen CreateWorldPen(Brush brush, IReadOnlyList<double>? pattern, double linetypeScale, double viewScale, bool proxyFallback)
    {
        viewScale = Math.Clamp(viewScale, MinViewportScale, MaxViewportScale);
        linetypeScale = double.IsFinite(linetypeScale) && linetypeScale > 0 ? linetypeScale : 1;
        var pen = new Pen(brush, 1.25 / viewScale);
        if (pattern is { Count: > 0 } && pattern.All(double.IsFinite) && pattern.Any(value => value != 0))
        {
            var periodPixels = 0.0;
            foreach (var value in pattern) periodPixels += Math.Abs(value * linetypeScale) * viewScale;
            // Below a pixel, individual dashes cannot be resolved. Avoid tessellating millions of them.
            // Dashing long/complex geometry is extremely slow in WPF's software rasterizer (a 1,100-figure frame took 36 s), and dashes shorter
            // than ~8 px read as a solid line anyway.
            if (double.IsFinite(periodPixels) && periodPixels >= 8)
            {
                var segments = new List<double>(pattern.Count + 2);
                foreach (var value in pattern)
                    segments.Add(Math.Abs(value * linetypeScale) / pen.Thickness);
                if (pattern[0] < 0) segments.Insert(0, 0);
                if (segments.Count % 2 != 0) segments.AddRange(segments.ToArray());
                if (segments.All(double.IsFinite)) pen.DashStyle = new DashStyle(segments, 0);
                if (pattern.Any(value => value == 0)) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
            }
        }
        // Proxy previews are drawn solid: a dash pattern over thousands of preview segments cost seconds per frame.
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private void PruneGeometryCache(IReadOnlyList<CadEntity> currentEntities)
    {
        _geometryCache.Prune(currentEntities);
        _lastRenderImageKey = null;
        _lastSelectionOverlayKey = null;
    }

    /// <summary>Rubber bands use the current linetype; ByLayer resolves to the current layer's linetype.</summary>
    private bool PreviewUsesDash
    {
        get
        {
            var d = _editor.Drawing;
            var name = d.CurrentLineStyle;
            if (string.IsNullOrWhiteSpace(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
                name = d.LayerStates.TryGetValue(d.CurrentLayer, out var state) ? state.LineType : "Continuous";
            return name is not null && d.LineStyles.TryGetValue(name, out var def) && def.Pattern.Count > 0;
        }
    }
    private void RenderPreview()
    {
        if (DrawingPreviewImage is null || DrawingCanvas is null) return;
        DrawingPreviewImage.Width = DrawingCanvas.ActualWidth;
        DrawingPreviewImage.Height = DrawingCanvas.ActualHeight;
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight));
            var styleColor = TryResolveCadColorValue(_editor.Drawing.CurrentColor, out var explicitCadColor, out _)
                ? Color.FromRgb(explicitCadColor.R, explicitCadColor.G, explicitCadColor.B)
                : _editor.Drawing.LayerColors.TryGetValue(_editor.Drawing.CurrentLayer, out var lc) ? Color.FromRgb(lc.R,lc.G,lc.B) : Color.FromRgb(140,197,177);
            var preview = new SolidColorBrush(styleColor);
            if (_editor.IsLineActive && _editor.LineStart is Point2 start && _editor.PreviewPoint is Point2 lineEnd)
                DrawLine(context, start, lineEnd, preview, 1.1, dashed: PreviewUsesDash);
            if (_editor.IsCircleActive && _editor.CircleCenter is Point2 center && _editor.PreviewPoint is Point2 radiusPoint)
                DrawCircle(context, center, Point2.Distance(center, radiusPoint), preview, 1.1, dashed: PreviewUsesDash);
            if (_rectangleActive && _rectangleFirstCorner is Point2 firstCorner && _editor.PreviewPoint is Point2 opposite)
                DrawRectanglePreview(context, firstCorner, opposite, preview);
            if (_arcActive && _arcCenter is Point2 arcCenter && _arcStartPoint is Point2 arcStart && _editor.PreviewPoint is Point2 arcEnd)
            {
                var startAngle = Math.Atan2(arcStart.Y - arcCenter.Y, arcStart.X - arcCenter.X) * 180 / Math.PI;
                var endAngle = Math.Atan2(arcEnd.Y - arcCenter.Y, arcEnd.X - arcCenter.X) * 180 / Math.PI;
                DrawArc(context, new ArcEntity(arcCenter, Point2.Distance(arcCenter, arcStart), startAngle, endAngle, Guid.Empty), preview, 1.1, dashed: PreviewUsesDash);
            }
            if (_polylineActive && _polylineVertices.Count > 0 && _editor.PreviewPoint is Point2 polylineEnd)
            {
                List<Point2> previewPointsList;
                if (_polylineArcMode)
                {
                    // Arc mode: the segment from the last real vertex to the cursor is a tangent-continuing arc
                    // (same bulge math as AddPolylineVertex/ComputeArcBulge), not a straight line - tessellate it
                    // with the same formula PolylineTessellator uses so the preview matches what will actually be saved.
                    previewPointsList = _polylineVertices.Select(v => v.Point).ToList();
                    var (previewBulge, _) = ComputeArcBulge(_polylineVertices[^1].Point, polylineEnd, _polylineTangent);
                    previewPointsList.AddRange(SampleBulgeSegment(_polylineVertices[^1].Point, polylineEnd, previewBulge));
                }
                else
                {
                    previewPointsList = _polylineVertices.Select(v => v.Point).Append(polylineEnd).ToList();
                }
                var previewPoints = previewPointsList.ToArray();
                if (_polygonRegionMode && previewPoints.Length > 2) previewPoints = previewPoints.Append(previewPoints[0]).ToArray();
                DrawPolyline(context, previewPoints, preview, 1.1, dashed: PreviewUsesDash);
                foreach (var vertex in _polylineVertices) context.DrawEllipse(preview, null, WorldToScreen(vertex.Point), 2.1, 2.1);
            }
            if (_transformActive && _transformBasePoint is Point2 transformBase && _editor.PreviewPoint is Point2 transformPoint)
                DrawTransformedSelectionPreview(context, transformBase, transformPoint - transformBase, 0, preview);
            if (_mirrorPointActive && _mirrorAxisStart is Point2 mirrorOrigin && _editor.PreviewPoint is Point2 mirrorPointer)
                DrawLine(context, mirrorOrigin, mirrorPointer, preview, 1.5, dashed: true);
            if (_distancePointActive && _distanceStart is Point2 distanceOrigin && _editor.PreviewPoint is Point2 distancePointer)
            {
                DrawLine(context, distanceOrigin, distancePointer, preview, 1.5, dashed: true);
                var mid = WorldToScreen(new Point2((distanceOrigin.X + distancePointer.X)/2, (distanceOrigin.Y + distancePointer.Y)/2));
                var distance = Point2.Distance(distanceOrigin, distancePointer);
                context.DrawText(new FormattedText($"{distance:0.###}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, preview, 1), new Point(mid.X+6,mid.Y-18));
            }
            if (_pickTool is not null && _pickTool != "ZOOMW" && _editor.PreviewPoint is Point2 pickPointer)
                DrawPickToolPreview(context, pickPointer, preview);
            if (_rotateActive && _rotateBasePoint is Point2 rotateOrigin && _editor.PreviewPoint is Point2 rotatePointer)
            {
                var angle = Math.Atan2(rotatePointer.Y - rotateOrigin.Y, rotatePointer.X - rotateOrigin.X);
                DrawTransformedSelectionPreview(context, rotateOrigin, default, angle, preview);
                DrawLine(context, rotateOrigin, rotatePointer, preview, 1, dashed: true);
            }
        }
        group.Freeze();
        var previewImage = new DrawingImage(group); previewImage.Freeze();
        DrawingPreviewImage.Source = previewImage;
    }

    private void DrawTransformedSelectionPreview(DrawingContext context, Point2 origin, Point2 offset, double angle, Brush stroke)
    {
        if (_selectedEntityIds.Count == 0 || !double.IsFinite(angle)) return;
        var width = DrawingCanvas.ActualWidth;
        var height = DrawingCanvas.ActualHeight;
        var c = Math.Cos(angle); var sin = Math.Sin(angle);
        var scale = _viewScale;
        var matrix = new Matrix(
            scale * c, -scale * sin,
            -scale * sin, -scale * c,
            width / 2 - _viewCenter.X * scale + scale * (origin.X - c * origin.X + sin * origin.Y + offset.X),
            height / 2 + _viewCenter.Y * scale + scale * (sin * origin.X + c * origin.Y - origin.Y - offset.Y));
        context.PushTransform(new MatrixTransform(matrix));
        var pen = CreatePen(stroke, 1.15 / Math.Max(scale, 1e-9), dashed: true);
        var remaining = 5000;
        var previewEntities = _transformActive ? _transformEntities : _rotateEntities;
        foreach (var entity in previewEntities)
        {
            if (_editor.Drawing.HiddenEntityIds.Contains(entity.Id)) continue;
            if (--remaining < 0) break;
            Geometry? geometry;
            if (_geometryCache.TryGet(entity, 0, out var cachedGeometry, CancellationToken.None, shareByContent: false))
                geometry = cachedGeometry;
            else
            {
                geometry = CadGeometryBuilder.BuildEntityGeometry(entity, 0, CancellationToken.None);
                if (geometry is not null && !geometry.IsFrozen) geometry.Freeze();
                if (geometry is not null) _geometryCache.Add(entity, 0, geometry, CancellationToken.None);
            }
            if (geometry is not null && !geometry.Bounds.IsEmpty) context.DrawGeometry(null, pen, geometry);
        }
        context.Pop();
    }

    private void DrawLine(DrawingContext context, Point2 a, Point2 b, Brush stroke, double thickness, bool dashed = false)
    {
        var from = WorldToScreen(a); var to = WorldToScreen(b);
        if (!double.IsFinite(from.X) || !double.IsFinite(from.Y) || !double.IsFinite(to.X) || !double.IsFinite(to.Y) ||
            Math.Abs(from.X) > 1e6 || Math.Abs(from.Y) > 1e6 || Math.Abs(to.X) > 1e6 || Math.Abs(to.Y) > 1e6) return;
        context.DrawLine(CreatePen(stroke, thickness, dashed), from, to);
    }

    private void DrawCircle(DrawingContext context, Point2 center, double radius, Brush stroke, double thickness = 1.25, bool dashed = false)
    {
        var screen = WorldToScreen(center); var r = Math.Abs(radius * _viewScale);
        // A preview circle with a radius of millions of pixels (or a dashed pen on one) makes WPF
        // tessellate for seconds and freezes the UI thread; such a circle is not visible anyway.
        if (!double.IsFinite(r) || !double.IsFinite(screen.X) || !double.IsFinite(screen.Y) || r > 200_000) return;
        context.DrawEllipse(null, CreatePen(stroke, thickness, dashed && r < 20_000), screen, r, r);
    }

    private void DrawRectanglePreview(DrawingContext context, Point2 first, Point2 opposite, Brush stroke)
    {
        DrawPolyline(context, new[] { first, new Point2(opposite.X, first.Y), opposite, new Point2(first.X, opposite.Y) }, stroke, 1.1, dashed: true, closed: true);
    }

    private void DrawPolyline(DrawingContext context, PolylineEntity polyline, Brush stroke, double thickness = 1.25, bool dashed = false)
    {
        DrawPolyline(context, PolylineTessellator.Tessellate(polyline), stroke, thickness, dashed, polyline.Closed);
    }

    private void DrawPolyline(DrawingContext context, IEnumerable<Point2> worldPoints, Brush stroke, double thickness, bool dashed, bool closed = false)
    {
        var points = worldPoints.Select(WorldToScreen).ToArray();
        if (points.Length < 2) return;
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(points[0], isFilled: false, isClosed: closed);
            builder.PolyLineTo(points.Skip(1).ToArray(), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        context.DrawGeometry(null, CreatePen(stroke, thickness, dashed), geometry);
    }

    private static Pen CreatePen(Brush brush, double thickness, bool dashed)
    {
        var pen = new Pen(brush, thickness);
        if (dashed) pen.DashStyle = new DashStyle([4, 3], 0);
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private void DrawProxy(DrawingContext context, ProxyCadEntity proxy, Brush stroke)
    {
        var topLeft = WorldToScreen(new Point2(proxy.BoundsMin.X, proxy.BoundsMax.Y));
        var bottomRight = WorldToScreen(new Point2(proxy.BoundsMax.X, proxy.BoundsMin.Y));
        var rect = new Rect(topLeft, bottomRight);
        if (rect.Width < 2 && rect.Height < 2)
        {
            var center = WorldToScreen(proxy.BoundsMin);
            context.DrawLine(CreatePen(stroke, 1, dashed: true), new Point(center.X - 3, center.Y), new Point(center.X + 3, center.Y));
            context.DrawLine(CreatePen(stroke, 1, dashed: true), new Point(center.X, center.Y - 3), new Point(center.X, center.Y + 3));
        }
        else context.DrawRectangle(null, CreatePen(stroke, 1, dashed: true), rect);
    }

    private void SelectCommand()
    {
        _pendingSelectionAction = SelectionAction.None;
        AppendCommandHistory("Click objects to select. Shift-click to add/remove. Press Esc to clear.");
        DrawingCanvas.Focus();
    }

    private void SelectTool_Click(object sender, RoutedEventArgs e) => SelectCommand();
    private void MoveTool_Click(object sender, RoutedEventArgs e) => BeginMove(copy: false);
    private void PanHelp_Click(object sender, RoutedEventArgs e)
    {
        AppendCommandHistory("Pan with the middle mouse button. Scroll the wheel to zoom.");
        DrawingCanvas.Focus();
    }

    private void BeginMove(bool copy)
    {
        _stretchActive = false;
        _transformCopy = copy;
        if (!RebuildSelectedProxies(copy ? "COPY" : "MOVE")) return;
        if (_selectedEntityIds.Count == 0)
        {
            _pendingSelectionAction = copy ? SelectionAction.Copy : SelectionAction.Move;
            AppendCommandHistory("Select objects, then press Enter:");
            DrawingCanvas.Focus();
            return;
        }
        StartMovePointStage();
    }

    private void BeginRotate()
    {
        if (_selectedEntityIds.Count == 0)
        {
            _pendingSelectionAction = SelectionAction.Rotate;
            AppendCommandHistory("Select objects to rotate, then press Enter:");
            DrawingCanvas.Focus();
            return;
        }
        if (!RebuildSelectedProxies("ROTATE")) return;
        _rotateEntities = _editor.Drawing.Entities.Where(entity => _selectedEntityIds.Contains(entity.Id)).ToArray();
        if (_rotateEntities.Length == 0) { AppendCommandHistory("No editable entities are selected."); return; }
        _rotateActive = true;
        _rotateBasePoint = null;
        _rotateCopy = false; _rotateReferenceStage = 0;
        _editor.MovePointer(ScreenToWorld(_lastPointer));
        DynamicPromptText.Text = "Specify rotation base point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        FocusDynamicInput();
        AppendCommandHistory("Specify rotation base point:");
    }

    private void CompleteRotate(double angleRadians)
    {
        if (_rotateBasePoint is not Point2 basePoint || !double.IsFinite(angleRadians)) return;
        var selected = _rotateEntities;
        var copy = _rotateCopy; _rotateCopy = false; _rotateReferenceStage = 0;
        var rotated = copy
            ? selected.Select(entity => (Source: entity, Result: RotateEntity(entity, basePoint, angleRadians))).Where(pair => !ReferenceEquals(pair.Source, pair.Result))
                .Select(pair => pair.Result with { Id = Guid.NewGuid(), NativeHandle = null }).ToArray()
            : selected.Select(entity => RotateEntity(entity, basePoint, angleRadians)).ToArray();
        _rotateActive = false;
        _rotateBasePoint = null;
        _rotateEntities = Array.Empty<CadEntity>();
        _editor.ApplyEntityChanges(copy ? Array.Empty<CadEntity>() : selected, rotated); // Copy option: the originals stay
        UpdateDynamicInputMode();
        RefreshPropertiesPalette();
        AppendCommandHistory($"{(copy ? "Rotated a copy of" : "Rotated")} {selected.Length} object(s) by {angleRadians * 180 / Math.PI:0.###} degrees.");
        DrawingCanvas.Focus();
        RenderDrawing(false);
    }

    private void CompleteRotateFromPoint(Point2 point)
    {
        if (_rotateBasePoint is not Point2 basePoint) return;
        var angle = Math.Atan2(point.Y - basePoint.Y, point.X - basePoint.X);
        RotateAnglePicked(angle); // plain rotation, or one of the two Reference steps
    }

    private CadEntity RotateEntity(CadEntity entity, Point2 origin, double angle)
    {
        var c = Math.Cos(angle); var sin = Math.Sin(angle);
        Point2 R(Point2 p)
        {
            var x = p.X - origin.X; var y = p.Y - origin.Y;
            return new Point2(origin.X + x * c - y * sin, origin.Y + x * sin + y * c);
        }
        return entity switch
        {
            LineEntity line => line with { Start = R(line.Start), End = R(line.End) },
            CircleEntity circle => circle with { Center = R(circle.Center) },
            ArcEntity arc => arc with { Center = R(arc.Center), StartAngleDegrees = arc.StartAngleDegrees + angle * 180 / Math.PI, EndAngleDegrees = arc.EndAngleDegrees + angle * 180 / Math.PI },
            PolylineEntity polyline => polyline with { Vertices = polyline.Vertices.Select(v => v with { Point = R(v.Point) }).ToArray() },
            CadTextEntity text => text with { Position = R(text.Position), RotationRadians = text.RotationRadians + angle },
            CadMTextEntity text => text with { Position = R(text.Position), RotationRadians = text.RotationRadians + angle },
            ProxyCadEntity { InsertData: not null } insertProxy => TransformProxy(insertProxy, R, angle, 1),
            ProxyCadEntity { Hatch: not null } hatchProxy => TransformProxy(hatchProxy, R, angle, 1),
            ProxyCadEntity { Leader: not null } leaderProxy => TransformProxy(leaderProxy, R, angle, 1),
            ProxyCadEntity { AttributeDef: not null } attrRotProxy => TransformProxy(attrRotProxy, R, angle, 1),
            ProxyCadEntity { Ellipse: not null } ellipseRotProxy => TransformProxy(ellipseRotProxy, R, angle, 1),
            ProxyCadEntity { Spline: not null } splineRotProxy => TransformProxy(splineRotProxy, R, angle, 1),
            ProxyCadEntity { Wipeout: not null } wipeoutRotProxy => TransformProxy(wipeoutRotProxy, R, angle, 1),
            ProxyCadEntity { Dimension: { } dim } proxy => RecomposeDimension(proxy, dim with
            {
                Points = dim.Points.Select(R).ToArray(),
                Kind = dim.Kind == "LINEAR" && Math.Abs(Math.Sin(angle * 2)) > 1e-6 ? "ALIGNED" : dim.Kind
            }),
            _ => entity
        };
    }

    private void StartMovePointStage()
    {
        _transformEntities = _editor.Drawing.Entities.Where(entity => _selectedEntityIds.Contains(entity.Id)).ToArray();
        if (_transformEntities.Length == 0) { AppendCommandHistory("No editable entities are selected."); return; }
        _transformActive = true;
        _transformBasePoint = null;
        _copyCount = 0;
        _copyPlacedStack.Clear();
        _transformDisplacement = false;
        DynamicPromptText.Text = "Specify base point or [Displacement]:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        FocusDynamicInput();
        AppendCommandHistory("Specify base point or [Displacement]:");
    }

    private void CompleteSelectionStage()
    {
        var action = _pendingSelectionAction;
        _pendingSelectionAction = SelectionAction.None;
        if (_selectedEntityIds.Count == 0)
        {
            AppendCommandHistory("No objects selected.");
            return;
        }
        if (action == SelectionAction.Erase) EraseSelection();
        else if (action is SelectionAction.Move or SelectionAction.Copy)
        {
            _transformCopy = action == SelectionAction.Copy;
            StartMovePointStage();
        }
        else if (action == SelectionAction.Rotate) BeginRotate();
        else if (action == SelectionAction.Corner) { _pendingSelectionAction = SelectionAction.Corner; ApplyCornerFromSelection(); _pendingSelectionAction = SelectionAction.None; }
        else if (action == SelectionAction.Stretch) StartStretchPointStage();
        else if (action == SelectionAction.Hatch) HatchSelected();
        else if (action == SelectionAction.Callback) { var callback = _selectionCallback; _selectionCallback = null; callback?.Invoke(); }
        RenderDrawing(false);
    }

    private void EraseSelection()
    {
        if (_selectedEntityIds.Count == 0)
        {
            _pendingSelectionAction = SelectionAction.Erase;
            AppendCommandHistory("Select objects to erase, then press Enter:");
            DrawingCanvas.Focus();
            return;
        }
        var removing = _editor.Drawing.Entities.Where(entity => _selectedEntityIds.Contains(entity.Id)).ToArray();
        _lastErased = removing; // OOPS restores these
        _editor.ApplyEntityChanges(removing, Array.Empty<CadEntity>());
        _selectedEntityIds.Clear();
        _pendingSelectionAction = SelectionAction.None;
        RefreshPropertiesPalette();
        AppendCommandHistory($"Erased {removing.Length} object(s).");
    }

    private void CompleteMove(Point2 secondPoint)
    {
        if (_transformBasePoint is not Point2 basePoint) return;
        var offset = secondPoint - basePoint;
        if (_stretchActive) { CompleteStretch(offset); return; }
        var selected = _transformEntities;
        var copy = _transformCopy;
        CadEntity[] changed;
        try
        {
            changed = selected.Select(entity => TranslateEntity(entity, offset, copy)).ToArray();
            if (copy && (changed.Any(entity => selected.Any(original => original.Id == entity.Id)) ||
                         changed.Select(entity => entity.Id).Distinct().Count() != changed.Length))
                throw new NotSupportedException("COPY contains unsupported entities that cannot be safely cloned.");
        }
        catch (Exception ex)
        {
            AppendCommandHistory($"COPY/MOVE aborted safely: {ex.Message}");
            _transformActive = false; _transformBasePoint = null; _transformEntities = Array.Empty<CadEntity>();
            DrawingCanvas.Focus(); return;
        }
        _transformActive = false;
        _transformBasePoint = null;
        _transformEntities = Array.Empty<CadEntity>();
        try
        {
            if (copy) _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), changed);
            else _editor.ApplyEntityChanges(selected, changed);
        }
        catch (Exception ex)
        {
            AppendCommandHistory($"COPY/MOVE failed: {ex.Message}");
            DrawingCanvas.Focus(); return;
        }
        if (copy)
        {
            // AutoCAD: COPY keeps placing copies from the same base point until Enter or Esc. The originals stay selected.
            _copyCount++;
            _copyPlacedStack.Add(changed);
            _transformActive = true; _transformBasePoint = basePoint; _transformEntities = selected;
            DynamicPromptText.Text = "Specify second point or [Exit/Undo] <Exit>:";
            DynamicValueTextBox.Clear();
            UpdateDynamicInputMode(); PositionDynamicInput(_lastPointer); FocusDynamicInput();
            AppendCommandHistory($"Copied {selected.Length} object(s). Specify second point or [Exit/Undo] <Exit>:");
            RenderDrawing(false);
            return;
        }
        UpdateDynamicInputMode();
        RefreshPropertiesPalette();
        AppendCommandHistory($"{(_transformCopy ? "Copied" : "Moved")} {selected.Length} object(s).");
        DrawingCanvas.Focus();
        RenderDrawing(false);
    }

    private CadEntity TranslateEntity(CadEntity entity, Point2 offset, bool newId)
    {
        var id = newId ? Guid.NewGuid() : entity.Id;
        var translated = entity switch
        {
            LineEntity line => line with { Start = line.Start + offset, End = line.End + offset },
            CircleEntity circle => circle with { Center = circle.Center + offset },
            ArcEntity arc => arc with { Center = arc.Center + offset },
            PolylineEntity polyline => polyline with { Vertices = polyline.Vertices.Select(vertex => vertex with { Point = vertex.Point + offset }).ToArray() },
            CadTextEntity text => text with { Position = text.Position + offset },
            CadMTextEntity text => text with { Position = text.Position + offset },
            ProxyCadEntity { Dimension: { } dim } proxy => RecomposeDimension(proxy, dim with { Points = dim.Points.Select(pt => pt + offset).ToArray() }),
            ProxyCadEntity { InsertData: not null } insertProxy => TransformProxy(insertProxy, pt => pt + offset, 0, 1),
            ProxyCadEntity { Hatch: not null } hatchProxy => TransformProxy(hatchProxy, pt => pt + offset, 0, 1),
            ProxyCadEntity { Leader: not null } leaderProxy => TransformProxy(leaderProxy, pt => pt + offset, 0, 1),
            ProxyCadEntity { AttributeDef: not null } attrMoveProxy => TransformProxy(attrMoveProxy, pt => pt + offset, 0, 1),
            ProxyCadEntity { Ellipse: not null } ellipseMoveProxy => TransformProxy(ellipseMoveProxy, pt => pt + offset, 0, 1),
            ProxyCadEntity { Spline: not null } splineMoveProxy => TransformProxy(splineMoveProxy, pt => pt + offset, 0, 1),
            ProxyCadEntity { Wipeout: not null } wipeoutMoveProxy => TransformProxy(wipeoutMoveProxy, pt => pt + offset, 0, 1),
            // TABLE's Position/ColumnWidth/RowHeight model has no rotation angle, so only a pure translation (MOVE/COPY,
            // this case) is exact - see the class remark in MainWindow.Tables.cs. ROTATE/MIRROR intentionally fall to
            // the `_ => entity` default below (object stays in place) rather than desyncing on-screen vs. saved shape.
            ProxyCadEntity { Table: { } tableMoveData } tableMoveProxy => TransformProxy(tableMoveProxy, pt => pt + offset, 0, 1) with
            {
                Table = tableMoveData with { Position = tableMoveData.Position + offset }
            },
            // VIEWPORT's Center is a paper-space point (moves with the drag, like the border itself); ViewCenter is a
            // MODEL-space point (what part of the model the window looks at) and must NOT shift just because the
            // window was moved around on the paper - matches real AutoCAD MVIEW. ROTATE/MIRROR intentionally fall to
            // the `_ => entity` default below (object stays in place), same disclosed gap as TABLE.
            ProxyCadEntity { Viewport: { } viewportMoveData } viewportMoveProxy => TransformProxy(viewportMoveProxy, pt => pt + offset, 0, 1) with
            {
                Viewport = viewportMoveData with { Center = viewportMoveData.Center + offset }
            },
            _ => entity
        };
        // Id is the base-record property: changing only EntityId left the copy with the ORIGINAL id, so COPY always failed its safety check.
        return translated with { Id = id, NativeHandle = newId ? null : entity.NativeHandle };
    }

    /// <summary>Ctrl+A: select every visible object of the active drawing (AutoCAD behavior).</summary>
    private void SelectAllEntities()
    {
        if (_activeSession is null) return;
        var drawing = _editor.Drawing;
        _selectedEntityIds.Clear();
        foreach (var entity in drawing.Entities)
            if (!drawing.HiddenEntityIds.Contains(entity.Id) && !IsLayerLocked(entity.Layer)) _selectedEntityIds.Add(entity.Id);
        AppendCommandHistory($"Selection: {_selectedEntityIds.Count:N0} object(s) found.");
        RefreshPropertiesPalette();
        RenderSelectionOverlay();
    }

    private void SelectAt(CadEntity hit, bool addToSelection)
    {
        if (!addToSelection) _selectedEntityIds.Clear();
        if (hit is not null)
        {
            if (addToSelection && _pendingSelectionAction == SelectionAction.None && _selectedEntityIds.Contains(hit.Id)) _selectedEntityIds.Remove(hit.Id);
            else _selectedEntityIds.Add(hit.Id);
            AppendCommandHistory($"Selection: {_selectedEntityIds.Count} object(s).");
        }
        RefreshPropertiesPalette();
        RenderSelectionOverlay();
        AfterPendingSelectionChanged(hit);
    }

    private bool _pickDebug;

    private CadEntity? FindEntityAt(Point screenPoint, bool recordSample = true)
    {
        var timer = recordSample ? Stopwatch.StartNew() : null;
        var bestDistance = Math.Max(8, _pickboxSize / 2 + 3);
        CadEntity? best = null;
        var world = ScreenToWorld(screenPoint);
        var tolerance = bestDistance / _viewScale;
        int dbgCandidates = 0, dbgHidden = 0, dbgLocked = 0; string? dbgLockedLayer = null;
        foreach (var entity in _spatialIndex.Query(world.X - tolerance, world.Y - tolerance, world.X + tolerance, world.Y + tolerance))
        {
            dbgCandidates++;
            if (_editor.Drawing.HiddenEntityIds.Contains(entity.Id)) { dbgHidden++; continue; }
            if (IsLayerLocked(entity.Layer)) { dbgLocked++; dbgLockedLayer ??= entity.Layer; continue; }
            var distance = entity switch
            {
                LineEntity line => DistanceToSegment(screenPoint, WorldToScreen(line.Start), WorldToScreen(line.End)),
                CircleEntity circle => Math.Abs(Point2.Distance(world, circle.Center) - Math.Abs(circle.Radius)) * _viewScale,
                ArcEntity arc => DistanceToArc(screenPoint, arc),
                PolylineEntity polyline => DistanceToPolyline(screenPoint, polyline),
                CadTextEntity or CadMTextEntity => Math.Max(0, DistanceToTextEntity(screenPoint, entity) - 4), // text gets a few pixels of extra grace
                ProxyCadEntity proxy when proxy.HasBounds => DistanceToProxyBounds(screenPoint, proxy),
                _ => double.PositiveInfinity
            };
            if (distance <= bestDistance) { bestDistance = distance; best = entity; }
        }
        if (best is null && _pickDebug && recordSample)
            AppendCommandHistory($"PICK: nothing under the cursor. Objects in range: {dbgCandidates}; hidden: {dbgHidden}; on locked layers: {dbgLocked}{(dbgLockedLayer is null ? "" : " (e.g. layer \"" + dbgLockedLayer + "\" - unlock it to select)")}.");
        if (timer is not null)
        {
            timer.Stop(); _lastPickMilliseconds = timer.Elapsed.TotalMilliseconds;
            _pickSamples.Enqueue(_lastPickMilliseconds);
            while (_pickSamples.Count > 100) _pickSamples.Dequeue();
        }
        return best;
    }

    /// <summary>Pick distance to the oriented text box (respects rotation, alignment, width factor); 0 inside.</summary>
    private double DistanceToTextEntity(Point screen, CadEntity entity) => DistanceToTextCorners(screen, CadTextExtents.GetCorners(entity));

    private double DistanceToTextCorners(Point screen, Point2[] corners)
    {
        if (corners.Length < 4) return double.PositiveInfinity;
        var pts = new Point[4];
        for (var i = 0; i < 4; i++) pts[i] = WorldToScreen(corners[i]);
        var inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
            if ((pts[i].Y > screen.Y) != (pts[j].Y > screen.Y) &&
                screen.X < (pts[j].X - pts[i].X) * (screen.Y - pts[i].Y) / (pts[j].Y - pts[i].Y) + pts[i].X)
                inside = !inside;
        if (inside) return 0;
        var best = double.PositiveInfinity;
        for (int i = 0, j = 3; i < 4; j = i++) best = Math.Min(best, DistanceToSegment(screen, pts[j], pts[i]));
        return best;
    }

    /// <summary>Pick distance to a block/DIMENSION preview text. Uses the text's own rotation, width and attachment point
    /// (the old axis-aligned, left-anchored box missed centered and rotated dimension texts, so they could not be clicked).</summary>
    private double DistanceToTextBounds(Point screen, Point2 position, string value, double height, double width, double rotation = 0, double lineSpacing = 1, int attachment = 1)
        => DistanceToTextCorners(screen, CadTextExtents.WrappedTextCorners(value, position, height, rotation, width, lineSpacing, attachment));

    /// <summary>
    /// Pick distance (screen DIPs) to a proxy/INSERT/DIMENSION/HATCH preview. Uses the real preview strokes and
    /// text boxes; the old code measured against the bounding box and returned 0 anywhere INSIDE it, so a click
    /// on empty space within a large block's box selected that block.
    /// </summary>
    private double DistanceToProxyBounds(Point screen, ProxyCadEntity proxy)
    {
        var topLeft = WorldToScreen(new Point2(proxy.BoundsMin.X, proxy.BoundsMax.Y));
        var bottomRight = WorldToScreen(new Point2(proxy.BoundsMax.X, proxy.BoundsMin.Y));
        var rect = new Rect(topLeft, bottomRight);
        var best = double.PositiveInfinity;
        var budget = 40_000; // segments examined per entity per pick
        var hasGeometry = false;
        void ConsiderPath(IReadOnlyList<Point2> points, bool closed)
        {
            if (points.Count < 2) return;
            hasGeometry = true;
            var previous = WorldToScreen(points[0]);
            for (var i = 1; i < points.Count && budget-- > 0; i++)
            {
                var current = WorldToScreen(points[i]);
                var d = DistanceToSegment(screen, previous, current);
                if (d < best) best = d;
                previous = current;
            }
            if (closed && budget > 0) best = Math.Min(best, DistanceToSegment(screen, previous, WorldToScreen(points[0])));
        }
        if (proxy.PreviewPaths is not null)
            foreach (var path in proxy.PreviewPaths) ConsiderPath(path, proxy.PreviewClosed);
        if (proxy.ProxyGraphicsPaths is not null)
            foreach (var path in proxy.ProxyGraphicsPaths) ConsiderPath(path.Points, path.Closed);
        if (!string.IsNullOrWhiteSpace(proxy.PreviewText))
        {
            hasGeometry = true;
            var position = proxy.PreviewTextPosition ?? proxy.BoundsMin;
            var height = proxy.PreviewTextHeight > 0 ? proxy.PreviewTextHeight : Math.Abs(proxy.BoundsMax.Y - proxy.BoundsMin.Y);
            best = Math.Min(best, DistanceToTextBounds(screen, position, proxy.PreviewText, height, proxy.PreviewTextWidth, proxy.PreviewTextRotation, proxy.PreviewTextLineSpacing, proxy.PreviewTextAttachment));
        }
        if (proxy.ProxyGraphicsTexts is not null)
            foreach (var text in proxy.ProxyGraphicsTexts)
            {
                hasGeometry = true;
                best = Math.Min(best, DistanceToTextBounds(screen, text.Position, text.Text, text.Height, text.Width, text.RotationRadians, text.LineSpacing, text.AttachmentPoint));
            }
        if (hasGeometry) return best;
        // Bounds-only proxy (nothing was previewed): only its outline is pickable, not its interior.
        var a = new Point(rect.Left, rect.Top); var b = new Point(rect.Right, rect.Top);
        var c = new Point(rect.Right, rect.Bottom); var d2 = new Point(rect.Left, rect.Bottom);
        return Math.Min(Math.Min(DistanceToSegment(screen, a, b), DistanceToSegment(screen, b, c)),
            Math.Min(DistanceToSegment(screen, c, d2), DistanceToSegment(screen, d2, a)));
    }

    private double DistanceToArc(Point screen, ArcEntity arc)
    {
        var world = ScreenToWorld(screen);
        var angle = Math.Atan2(world.Y - arc.Center.Y, world.X - arc.Center.X) * 180 / Math.PI;
        if (AngleOnArc(angle, arc.StartAngleDegrees, arc.EndAngleDegrees))
            return Math.Abs(Point2.Distance(world, arc.Center) - arc.Radius) * _viewScale;
        var start = arc.StartAngleDegrees * Math.PI / 180;
        var end = arc.EndAngleDegrees * Math.PI / 180;
        var p1 = WorldToScreen(new Point2(arc.Center.X + arc.Radius * Math.Cos(start), arc.Center.Y + arc.Radius * Math.Sin(start)));
        var p2 = WorldToScreen(new Point2(arc.Center.X + arc.Radius * Math.Cos(end), arc.Center.Y + arc.Radius * Math.Sin(end)));
        return Math.Min(Length(screen, p1), Length(screen, p2));
    }

    private static bool AngleOnArc(double angle, double start, double end)
    {
        angle = (angle % 360 + 360) % 360; start = (start % 360 + 360) % 360; end = (end % 360 + 360) % 360;
        return start <= end ? angle >= start && angle <= end : angle >= start || angle <= end;
    }

    private double DistanceToPolyline(Point screen, PolylineEntity polyline)
    {
        var p = ScreenToWorld(screen);
        var vertices = polyline.Vertices;
        var count = polyline.Closed ? vertices.Count : vertices.Count - 1;
        var best = double.PositiveInfinity;
        for (var i = 0; i < count; i++)
        {
            var current = vertices[i]; var a = current.Point; var b = vertices[(i + 1) % vertices.Count].Point;
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12 || Math.Abs(current.Bulge) < 1e-12 || !double.IsFinite(current.Bulge))
            {
                var t = chord < 1e-12 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (chord * chord), 0, 1);
                best = Math.Min(best, Point2.Distance(p, new Point2(a.X + t * dx, a.Y + t * dy)));
                continue;
            }
            var bulge = current.Bulge;
            var offset = chord * .25 * (1 / bulge - bulge);
            var center = new Point2(a.X + dx * .5 - dy / chord * offset, a.Y + dy * .5 + dx / chord * offset);
            var radius = chord * .25 * (Math.Abs(bulge) + 1 / Math.Abs(bulge));
            var start = Math.Atan2(a.Y - center.Y, a.X - center.X);
            var angle = Math.Atan2(p.Y - center.Y, p.X - center.X);
            var relative = bulge > 0 ? angle - start : start - angle;
            relative = (relative % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
            var distance = relative <= Math.Abs(4 * Math.Atan(bulge)) + 1e-12
                ? Math.Abs(Point2.Distance(p, center) - radius)
                : Math.Min(Point2.Distance(p, a), Point2.Distance(p, b));
            best = Math.Min(best, distance);
        }
        return best * _viewScale;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X; var dy = b.Y - a.Y; var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-9) return Length(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return Length(p, new Point(a.X + t * dx, a.Y + t * dy));
    }

    private static double Length(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void DrawArc(DrawingContext context, ArcEntity arc, Brush stroke, double thickness = 1.25, bool dashed = false)
    {
        var sweep = arc.EndAngleDegrees - arc.StartAngleDegrees;
        while (sweep <= 0) sweep += 360;
        var count = Math.Clamp((int)(sweep / 5), 8, 96);
        var points = new Point2[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var angle = (arc.StartAngleDegrees + sweep * i / count) * Math.PI / 180;
            points[i] = new Point2(arc.Center.X + arc.Radius * Math.Cos(angle), arc.Center.Y + arc.Radius * Math.Sin(angle));
        }
        DrawPolyline(context, points, stroke, thickness, dashed);
    }

    private void FitView(bool absolute = false)
    {
        _wheelDebounceTimer.Stop(); _pendingWheelZoom = false;
        ResetWheelZoomPreview();
        if (IsPaperSpaceActive && DrawingCanvas.ActualWidth >= 20 && DrawingCanvas.ActualHeight >= 20)
        {
            var (pw, ph) = GetPaperSize(_activeLayout);
            _viewCenter = new Point2(pw / 2, ph / 2);
            _viewScale = _fitScale = Math.Clamp(Math.Min((DrawingCanvas.ActualWidth - 100) / pw, (DrawingCanvas.ActualHeight - 100) / ph), MinViewportScale, MaxViewportScale);
            UpdateZoomControls();
            return;
        }
        var entities = _editor.Drawing.Entities;
        if (entities.Count == 0 || DrawingCanvas.ActualWidth < 20 || DrawingCanvas.ActualHeight < 20)
        { _viewCenter = new Point2(0, 0); _viewScale = _fitScale = 1; UpdateZoomControls(); return; }
        var minX = double.PositiveInfinity; var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity; var maxY = double.NegativeInfinity;
        void Include(Point2 p, double r = 0)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(r)) return;
            minX = Math.Min(minX, p.X - r); maxX = Math.Max(maxX, p.X + r);
            minY = Math.Min(minY, p.Y - r); maxY = Math.Max(maxY, p.Y + r);
        }
        void IncludeText(Point2 position, string value, double height, double width)
        {
            var h = double.IsFinite(height) && height > 0 ? height : 1;
            var lines = Math.Max(1, value.Count(character => character == '\n') + 1);
            var w = double.IsFinite(width) && width > 0 ? width : Math.Max(h * 0.6, value.Length * h * 0.65);
            Include(position);
            Include(new Point2(position.X + w, position.Y - h * lines));
            Include(new Point2(position.X + w, position.Y + h * 0.2));
        }
        if (!absolute && _spatialIndex.TryGetRobustExtents(out var robustExtents, out var outlierCount))
        {
            // Stray objects millions of units away made the whole drawing a few pixels wide ("nothing visible").
            minX = robustExtents.MinX; minY = robustExtents.MinY;
            maxX = robustExtents.MaxX; maxY = robustExtents.MaxY;
            AppendCommandHistory($"Zoom extents: {outlierCount:N0} far-away object(s) outside the main drawing were ignored. Use ZOOM A for the absolute extents.");
        }
        else if (_spatialIndex.TryGetExtents(out var indexedExtents))
        {
            minX = indexedExtents.MinX; minY = indexedExtents.MinY;
            maxX = indexedExtents.MaxX; maxY = indexedExtents.MaxY;
        }
        else
        {
            foreach (var entity in entities)
            {
                if (_editor.Drawing.HiddenEntityIds.Contains(entity.Id) || IsLayerLocked(entity.Layer)) continue;
                switch (entity)
                {
                    case LineEntity l: Include(l.Start); Include(l.End); break;
                    case CircleEntity c: Include(c.Center, c.Radius); break;
                    case ArcEntity a: Include(a.Center, a.Radius); break;
                    case PolylineEntity polyline:
                        foreach (var point in PolylineTessellator.Tessellate(polyline)) Include(point);
                        break;
                    case CadTextEntity singleText:
                        IncludeText(singleText.Position, singleText.Value, singleText.Height, 0); break;
                    case CadMTextEntity multiText:
                        IncludeText(multiText.Position, multiText.Value, multiText.Height, multiText.Width); break;
                    case ProxyCadEntity proxy when proxy.HasBounds:
                        Include(proxy.BoundsMin); Include(proxy.BoundsMax);
                        break;
                }
            }
        }
        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY))
        { _viewCenter = new Point2(0, 0); _viewScale = _fitScale = 1; UpdateZoomControls(); return; }
        var spanX = Math.Max(1, maxX - minX); var spanY = Math.Max(1, maxY - minY);
        _viewCenter = new Point2((minX + maxX) / 2, (minY + maxY) / 2);
        _viewScale = _fitScale = Math.Clamp(Math.Min((DrawingCanvas.ActualWidth - 80) / spanX, (DrawingCanvas.ActualHeight - 80) / spanY), MinViewportScale, MaxViewportScale);
        UpdateZoomControls();
    }

    private void ShowCadError(string title, Exception exception)
    {
        AppendCommandHistory($"Error: {title}: {exception.Message}");
        MessageBox.Show(this, $"{title}.\n\n{exception.Message}", "iStCAD_1", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
