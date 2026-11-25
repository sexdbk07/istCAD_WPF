namespace iStCAD_1.Core.Model;

public enum PlotAreaKind { Display, Extents, Window, Layout }

public enum PlotColorMode { Color, Monochrome, Grayscale, PenTable }

/// <summary>Layer rule evaluated in order; the first rule whose pattern matches the entity layer decides.</summary>
public sealed class PlotLayerRule
{
    /// <summary>Layer name or wildcard pattern (* and ?), case-insensitive.</summary>
    public string Pattern { get; set; } = "*";
    public bool Include { get; set; } = true;
    /// <summary>Optional output color override (#RRGGBB).</summary>
    public string? ColorHex { get; set; }
    /// <summary>Optional lineweight override in mm.</summary>
    public double? LineWeightMm { get; set; }
}

/// <summary>Pen-table row (like a CTB entry): maps an entity color to an output color and lineweight.</summary>
public sealed class PlotPenRule
{
    public string ColorHex { get; set; } = "#FF0000";
    public string? OutputColorHex { get; set; }
    public double LineWeightMm { get; set; } = 0.25;
}

public sealed class PlotSetup
{
    public string Name { get; set; } = "Standard";
    public string PaperName { get; set; } = "ISO A3";
    /// <summary>Short side and long side of the paper in mm (orientation is applied separately).</summary>
    public double PaperShortMm { get; set; } = 297;
    public double PaperLongMm { get; set; } = 420;
    public bool Landscape { get; set; } = true;
    public bool UpsideDown { get; set; }
    public double MarginLeftMm { get; set; } = 5;
    public double MarginTopMm { get; set; } = 5;
    public double MarginRightMm { get; set; } = 5;
    public double MarginBottomMm { get; set; } = 5;

    public PlotAreaKind Area { get; set; } = PlotAreaKind.Extents;
    public double WindowMinX { get; set; }
    public double WindowMinY { get; set; }
    public double WindowMaxX { get; set; } = 100;
    public double WindowMaxY { get; set; } = 100;

    public bool FitToPaper { get; set; } = true;
    /// <summary>Custom scale: <see cref="ScalePaperMm"/> mm on paper = <see cref="ScaleDrawingUnits"/> drawing units.</summary>
    public double ScalePaperMm { get; set; } = 1;
    public double ScaleDrawingUnits { get; set; } = 1;
    public bool CenterPlot { get; set; } = true;
    public double OffsetXMm { get; set; }
    public double OffsetYMm { get; set; }

    public PlotColorMode ColorMode { get; set; } = PlotColorMode.Color;
    public double DefaultLineWeightMm { get; set; } = 0.25;
    public bool PlotLineweights { get; set; } = true;
    public bool ScaleLineweights { get; set; }
    public bool IncludeHiddenObjects { get; set; }
    public bool PlotFrame { get; set; }
    public bool PlotStamp { get; set; }
    public string StampText { get; set; } = "";
    public bool OpenAfterPlot { get; set; } = true;

    public List<PlotLayerRule> LayerRules { get; set; } = new() { new PlotLayerRule { Pattern = "Defpoints", Include = false } };
    public List<PlotPenRule> PenRules { get; set; } = new();

    // Layout viewport (used when plotting a paper-space layout): model window shown at a fixed scale inside a paper rectangle.
    public bool UseViewport { get; set; }
    public double ViewportXMm { get; set; } = 10;
    public double ViewportYMm { get; set; } = 10;
    public double ViewportWidthMm { get; set; } = 400;
    public double ViewportHeightMm { get; set; } = 277;
    public double ViewportCenterX { get; set; }
    public double ViewportCenterY { get; set; }
    /// <summary>Paper millimetres per model unit.</summary>
    public double ViewportScale { get; set; } = 1;
    public bool ViewportFrame { get; set; } = true;

    public PlotSetup Clone()
    {
        var copy = (PlotSetup)MemberwiseClone();
        copy.LayerRules = LayerRules.Select(r => new PlotLayerRule { Pattern = r.Pattern, Include = r.Include, ColorHex = r.ColorHex, LineWeightMm = r.LineWeightMm }).ToList();
        copy.PenRules = PenRules.Select(r => new PlotPenRule { ColorHex = r.ColorHex, OutputColorHex = r.OutputColorHex, LineWeightMm = r.LineWeightMm }).ToList();
        return copy;
    }

    public double PaperWidthMm => Landscape ? Math.Max(PaperShortMm, PaperLongMm) : Math.Min(PaperShortMm, PaperLongMm);
    public double PaperHeightMm => Landscape ? Math.Min(PaperShortMm, PaperLongMm) : Math.Max(PaperShortMm, PaperLongMm);
}
