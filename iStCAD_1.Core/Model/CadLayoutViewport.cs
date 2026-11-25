namespace iStCAD_1.Core.Model;

/// <summary>A paper-space viewport: a rectangle on the sheet (mm) showing a model window at a fixed scale.</summary>
public sealed record CadLayoutViewport(int Id, double PaperX, double PaperY, double Width, double Height,
    double ViewX, double ViewY, double ViewHeight, IReadOnlyList<string> FrozenLayers)
{
    /// <summary>Paper millimetres per model unit.</summary>
    public double Scale => ViewHeight > 0 ? Height / ViewHeight : 1;
    public bool ContainsPaperPoint(double x, double y) =>
        Math.Abs(x - PaperX) <= Width / 2 && Math.Abs(y - PaperY) <= Height / 2;
}
