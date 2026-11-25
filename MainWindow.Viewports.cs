using System;
using System.Collections.Generic;
using System.Linq;
using iStCAD_1.Application.Spatial;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>AutoCAD-style VIEWPORT (MVIEW): a paper-space "window" onto model space - only usable on a Layout/Paper
/// tab (like real MVIEW). Simplified to a single drag of a rectangle (no "fit"/"2/3/4 viewports"/polygonal-boundary
/// sub-options); iStCAD fills in the view itself by fitting the current Model-space extents into the dragged
/// rectangle, preserving aspect ratio, so the viewport is immediately useful without extra steps - "cách vẽ gọn hơn,
/// nhưng object lưu ra đúng native" (same principle as ELLIPSE/SPLINE/WIPEOUT/MLEADER/TABLE). Saved as a real native
/// ACadSharp Viewport entity (confirmed supported by BOTH the DWG and DXF writers - unlike TABLE/MLEADER, there is no
/// asymmetric-writer-support risk here). ROTATE/MIRROR are not supported yet (object stays in place) - same disclosed
/// gap as TABLE. See docs/AUTOCAD_COMMANDS.md.</summary>
public partial class MainWindow
{
    /// <summary>Builds the rectangle-outline preview for a CadViewportData - the SAME shape used for the live drag
    /// preview and the final committed object. A real native Viewport also draws the clipped model content inside
    /// this rectangle once on screen (see the Layout-tab render merge in RenderDrawing), but that richer preview is
    /// computed separately from the current Model-space snapshot; this border is what makes the object selectable,
    /// movable and listable like every other proxy-based entity in this app.</summary>
    private static ProxyCadEntity? ComposeViewport(CadViewportData data, Guid id, string layer)
    {
        if (data.Width <= 1e-9 || data.Height <= 1e-9) return null;
        var minX = data.Center.X - data.Width / 2; var maxX = data.Center.X + data.Width / 2;
        var minY = data.Center.Y - data.Height / 2; var maxY = data.Center.Y + data.Height / 2;
        var border = new ProxyPreviewPath(new[]
        {
            new Point2(minX, minY), new Point2(maxX, minY), new Point2(maxX, maxY), new Point2(minX, maxY), new Point2(minX, minY)
        });
        return new ProxyCadEntity("VIEWPORT", new Point2(minX, minY), new Point2(maxX, maxY), true, id, layer,
            ProxyGraphicsPaths: new[] { border }, Viewport: data);
    }

    /// <summary>Fits <paramref name="modelExtents"/> into a <paramref name="paperWidth"/> x <paramref name="paperHeight"/>
    /// window, preserving aspect ratio (like AutoCAD's own "Zoom Extents" inside a new viewport) - picks the smallest
    /// ViewHeight that still shows the whole model in both directions, with a small 2% margin so edge geometry is not
    /// touching the viewport border. Falls back to a plain 1:1 view centered on the model (or the origin, if the model
    /// is empty) when the model has no usable extents.</summary>
    private static (Point2 ViewCenter, double ViewHeight) FitModelIntoViewport(
        (double MinX, double MinY, double MaxX, double MaxY)? modelExtents, double paperWidth, double paperHeight)
    {
        if (modelExtents is not { } ext)
            return (new Point2(0, 0), paperHeight > 1e-9 ? paperHeight : 1.0);
        var modelW = Math.Max(ext.MaxX - ext.MinX, 1e-9);
        var modelH = Math.Max(ext.MaxY - ext.MinY, 1e-9);
        var center = new Point2((ext.MinX + ext.MaxX) / 2, (ext.MinY + ext.MaxY) / 2);
        var viewHeight = Math.Max(modelH, modelW * paperHeight / Math.Max(paperWidth, 1e-9)) * 1.02;
        return (center, viewHeight > 1e-9 ? viewHeight : 1.0);
    }

    private void ViewportCommand()
    {
        if (!IsPaperSpaceActive)
        {
            AppendCommandHistory("VIEWPORT: switch to a Layout (paper space) tab first - a viewport can only be placed there, like AutoCAD's own MVIEW.");
            return;
        }
        (double MinX, double MinY, double MaxX, double MaxY)? modelExtents = null;
        try
        {
            var modelEntities = _editor.Drawing.GetSpaceEntities("Model");
            if (modelEntities.Count > 0 && new CadSpatialIndex(modelEntities).TryGetExtents(out var ext)) modelExtents = ext;
        }
        catch { /* a bad Model-space entity must not block placing a viewport */ }

        ProxyCadEntity? Build(Wizard x)
        {
            if (!x.Has(0) || !x.Has(1)) return null;
            var p1 = x.P(0); var p2 = x.P(1);
            var minX = Math.Min(p1.X, p2.X); var maxX = Math.Max(p1.X, p2.X);
            var minY = Math.Min(p1.Y, p2.Y); var maxY = Math.Max(p1.Y, p2.Y);
            var w = maxX - minX; var h = maxY - minY;
            if (w < 1e-9 || h < 1e-9) return null;
            var (viewCenter, viewHeight) = FitModelIntoViewport(modelExtents, w, h);
            var data = new CadViewportData(new Point2((minX + maxX) / 2, (minY + maxY) / 2), w, h, viewCenter, viewHeight, Array.Empty<string>());
            return ComposeViewport(data, Guid.NewGuid(), _editor.Drawing.CurrentLayer) is { } composed ? (ProxyCadEntity)_editor.StyleNew(composed) : null;
        }
        StartWizard(new Wizard
        {
            Name = "VIEWPORT",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Point, Prompt = "Specify first corner of viewport:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify opposite corner:" }
            },
            Preview = x => Build(x) is { } v ? new List<CadEntity> { v } : null,
            Commit = x =>
            {
                var item = Build(x);
                if (item is null) { AppendCommandHistory("VIEWPORT: invalid size."); return false; }
                AddNewEntities("VIEWPORT", new[] { item }, "viewport created - showing current Model space extents. Save as .dwg/.dxf and reopen in AutoCAD to confirm.");
                return true;
            }
        });
    }
}
