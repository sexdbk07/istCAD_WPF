using System.Windows.Controls;
using System.Windows.Shapes;

namespace iStCAD_1.Rendering;

/// <summary>Isolated CAD viewport surface: drawing layers and transient interaction overlays only.</summary>
public partial class CadViewportControl : UserControl
{
    public Canvas DrawingCanvas => ViewportInputCanvas;
    public Image GridDotsImage => ViewportGridImage;
    public Image DrawingGeometryImage => ViewportGeometryImage;
    public Image FocusPatchImage => ViewportFocusImage;
    public Image SelectionOverlayImage => ViewportSelectionImage;
    public Image DrawingPreviewImage => ViewportPreviewImage;
    public Rectangle SelectionDragOverlay => ViewportSelectionDragOverlay;
    public Border DynamicInputPanel => ViewportDynamicInputPanel;
    public Border DynamicHintPanel => ViewportDynamicHintPanel;
    public TextBlock DynamicPromptText => ViewportDynamicPromptText;
    public DockPanel DynamicInputEntryPanel => ViewportDynamicInputEntryPanel;
    public TextBox DynamicValueTextBox => ViewportDynamicValueTextBox;
    public Path OsnapMarker => ViewportOsnapMarker;
    public TextBlock OsnapMarkerLabel => ViewportOsnapMarkerLabel;

    public IViewportBackend Backend { get; private set; } = ViewportBackendRegistry.Create();
    public ViewportPerformanceCounter Performance { get; } = new();
    public event EventHandler<string>? BackendChanged;
    public string BackendId => Backend.Id;

    public CadViewportControl()
    {
        InitializeComponent();
        Backend.Attach(this);
        Unloaded += (_, _) => Backend.Detach();
        Loaded += (_, _) => Backend.Attach(this);
    }

    /// <summary>Fail-safe backend selection. Rejected backends never disturb the current viewport.</summary>
    public bool TrySetBackend(string id, out string? error)
    {
        try
        {
            var next = ViewportBackendRegistry.Create(id);
            next.Attach(this);
            Backend.Detach();
            Backend = next;
            BackendChanged?.Invoke(this, next.Id);
            error = null;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
