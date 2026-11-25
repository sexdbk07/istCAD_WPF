using System.Reflection;

namespace iStCAD_1;

internal static class BuildInfo
{
    public static string Version => typeof(BuildInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "v1.0.55";
}
