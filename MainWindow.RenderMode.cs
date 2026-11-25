namespace iStCAD_1;

/// <summary>Render quality tiers: light drawings get the best picture (smooth vector, text, hatch, dashes); heavy ones trade fidelity for speed.</summary>
public partial class MainWindow
{
    // 0 = Auto, 1 = Quality, 2 = Balanced, 3 = Fast.
    private static volatile int s_renderModeSetting;
    private static volatile int s_lastFrameTier = 2;

    /// <summary>Auto picks by drawing size, then escalates when WPF's raster measured slow (the measurement only updates when it rasterizes,
    /// so an escalated tier stays until a new drawing is opened).</summary>
    private static int ResolveRenderTier(int indexedEntities)
    {
        var mode = s_renderModeSetting;
        if (mode != 0) return mode;
        var tier = indexedEntities <= 30_000 ? 1 : indexedEntities <= 120_000 ? 2 : 3;
        var lastRaster = s_lastRasterMilliseconds;
        if (lastRaster > 3000) tier = Math.Max(tier, 3);
        else if (lastRaster > 1200) tier = Math.Max(tier, 2);
        return tier;
    }

    private static string RenderTierName(int tier) => tier switch { 1 => "Quality", 3 => "Fast", _ => "Balanced" };

    private void RenderModeCommand(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "A": case "AUTO": s_renderModeSetting = 0; break;
                case "Q": case "QUALITY": s_renderModeSetting = 1; break;
                case "B": case "BALANCED": s_renderModeSetting = 2; break;
                case "F": case "FAST": s_renderModeSetting = 3; break;
                default: AppendCommandHistory("RENDERMODE: Auto / Quality / Balanced / Fast."); return;
            }
            s_lastRasterMilliseconds = 0; s_rasterMsPerUnit = 0;
            _lastRenderImageKey = null; ClearFrameCache();
            RenderDrawing(false);
        }
        var current = s_renderModeSetting;
        AppendCommandHistory($"RENDERMODE: {(current == 0 ? "Auto" : RenderTierName(current))} (current frame tier: {RenderTierName(s_lastFrameTier)}). " +
            "Quality = smooth vectors, real text/hatch/dashes (light drawings); Balanced; Fast = pixel-plotted lines, draft text/hatch (very heavy drawings). Auto chooses by drawing size and measured speed.");
    }
}
