using System.Diagnostics;
using System.IO;
using System.Runtime;

namespace iStCAD_1;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // Multi-core JIT: records the methods used at startup and pre-compiles them on a background thread next launch.
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iStCAD_1");
            Directory.CreateDirectory(root);
            ProfileOptimization.SetProfileRoot(root);
            ProfileOptimization.StartProfile("startup.jitprofile");
        }
        catch { }
        PreferHighPerformanceGpu();
        base.OnStartup(e);
    }

    /// <summary>
    /// On laptops with an integrated + discrete GPU, Windows runs WPF on the integrated GPU by default.
    /// Writes the per-user "Graphics settings" preference (High performance) for this executable. Applies from the next launch.
    /// </summary>
    private static void PreferHighPerformanceGpu()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe) || !OperatingSystem.IsWindows()) return;
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
            if (key?.GetValue(exe) is null) key?.SetValue(exe, "GpuPreference=2;", Microsoft.Win32.RegistryValueKind.String);
        }
        catch { }
    }
}
