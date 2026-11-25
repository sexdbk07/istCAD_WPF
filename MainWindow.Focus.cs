using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace iStCAD_1;

/// <summary>
/// Focus-first rendering: when a full frame is going to be slow, the area around the pointer (where the eye is) is drawn first at full quality
/// as a soft-edged patch over the stale preview, then the rest of the frame is completed and the patch cross-fades into it.
/// </summary>
public partial class MainWindow
{
    private RenderImageKey? _focusPatchKey;
    private System.Windows.Controls.Image FocusPatchImage => CadViewport.FocusPatchImage;

    /// <summary>Returns the pointer-centred region to draw first, or null when the frame is expected to be fast (or no pointer / no stale frame).</summary>
    private Rect? TryGetFocusRect(RenderImageKey key, double width, double height)
    {
        if (_focusPatchKey == key) return null;                       // already done for this camera: go straight to the full frame
        _focusPatchKey = key;
        if (_lastRenderImage is null || width < 480 || height < 320) return null;
        var slow = _lastFullBuildMilliseconds > 400 || s_lastRasterMilliseconds > 350;
        if (!slow) return null;
        var p = _lastPointer;
        if (double.IsNaN(p.X) || p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) p = new Point(width / 2, height / 2);
        double w = width * 0.42, h = height * 0.52;
        var left = Math.Clamp(p.X - w / 2, 0, Math.Max(0, width - w));
        var top = Math.Clamp(p.Y - h / 2, 0, Math.Max(0, height - h));
        return new Rect(left, top, w, h);
    }

    private void ShowFocusPatch(DrawingImage image, Rect focus, double width, double height)
    {
        var patch = FocusPatchImage;
        patch.BeginAnimation(UIElement.OpacityProperty, null);
        patch.Width = width; patch.Height = height;
        patch.Source = image;
        patch.RenderTransform = Transform.Identity;
        // Soft elliptical edge: no hard rectangle boundary between the sharp patch and the stale preview.
        var mask = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = new Point(focus.Left + focus.Width / 2, focus.Top + focus.Height / 2),
            GradientOrigin = new Point(focus.Left + focus.Width / 2, focus.Top + focus.Height / 2),
            RadiusX = focus.Width / 2, RadiusY = focus.Height / 2
        };
        mask.GradientStops.Add(new GradientStop(Colors.White, 0.0));
        mask.GradientStops.Add(new GradientStop(Colors.White, 0.72));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1.0));
        mask.Freeze();
        patch.OpacityMask = mask;
        patch.Opacity = 0;
        patch.Visibility = Visibility.Visible;
        patch.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)));
    }

    private void HideFocusPatch(bool animated)
    {
        if (CadViewport is null) return;
        var patch = FocusPatchImage;
        if (patch.Visibility != Visibility.Visible) return;
        patch.BeginAnimation(UIElement.OpacityProperty, null);
        if (!animated) { patch.Visibility = Visibility.Collapsed; patch.Source = null; return; }
        var fade = new DoubleAnimation(patch.Opacity, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) =>
        {
            if (patch.Opacity <= 0.001) { patch.Visibility = Visibility.Collapsed; patch.Source = null; }
        };
        patch.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
