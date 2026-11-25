namespace iStCAD_1.Rendering;

/// <summary>Strict, UI-independent gate for experimental backend selection.</summary>
public static class ViewportBackendEvaluation
{
    public sealed record Evaluation(string Id, bool Selectable, bool UpstreamIntegrated, string Status);
    public static IReadOnlyList<Evaluation> All { get; } = Array.AsReadOnly(new[]
    {
        new Evaluation("native-wpf", true, false, "Production baseline, unchanged WPF input and layer arrangement"),
        new Evaluation("lightweight-wpf", true, false, "Experimental WPF compositor: retains native entities, R-tree and cached geometry"),
        new Evaluation("procad-adapter", false, false, "Blocked: ProCad Avalonia integration needs verified source and compatible runtime"),
        new Evaluation("tida-wpf-adapter", false, false, "Blocked: Tida.CAD WPF source not yet integrated or benchmarked")
    });
    public static bool CanActivate(string id) => All.Any(x => x.Id == id && x.Selectable);
}
