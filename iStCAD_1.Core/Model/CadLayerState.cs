namespace iStCAD_1.Core.Model;

/// <summary>Layer table row as shown in the Layer Properties Manager (colors live in <see cref="CadDrawing.LayerColors"/>).</summary>
public sealed class CadLayerState
{
    public string Name { get; set; } = "0";
    public bool On { get; set; } = true;
    public bool Frozen { get; set; }
    public bool Locked { get; set; }
    public string LineType { get; set; } = "Continuous";
    /// <summary>Lineweight in mm; null = Default.</summary>
    public double? LineWeightMm { get; set; }
    /// <summary>0..90 percent.</summary>
    public int Transparency { get; set; }
    public bool Plot { get; set; } = true;
    public bool NewViewportFreeze { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool Visible => On && !Frozen;
    public CadLayerState Clone() => (CadLayerState)MemberwiseClone();
}
