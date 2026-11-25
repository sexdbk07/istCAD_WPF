using iStCAD_1.Native;

namespace iStCAD_1;

/// <summary>NATIVESTATUS / NATIVE: diagnostics for the optional Rust native "heavy file" engine (native/istcad_native).</summary>
public partial class MainWindow
{
    private void NativeStatusCommand()
    {
        if (!NativeHeavyFileEngine.IsAvailable)
        {
            AppendCommandHistory($"NATIVESTATUS: {NativeHeavyFileEngine.StatusText}");
            return;
        }
        AppendCommandHistory($"NATIVESTATUS: {NativeHeavyFileEngine.RunSelfTest()}");
    }
}
