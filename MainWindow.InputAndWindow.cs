using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace iStCAD_1;

public partial class MainWindow
{
    private bool _dynamicCommandEntry;
    private bool _dynamicIdleVisible;
    private void WindowDynamicTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!_dynamicInputEnabled || string.IsNullOrEmpty(e.Text) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) return;
        // Preserve text editors, search and the command line; route viewport typing to DYN.
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox) return;
        if (!DrawingCanvas.IsKeyboardFocusWithin && !DrawingCanvas.IsMouseOver) return;
        if (!IsDynamicCommandActive)
        {
            _dynamicCommandEntry = true;
            _dynamicIdleVisible = false;
            DynamicPromptText.Text = string.Empty;
        }
        UpdateDynamicInputMode();
        PositionDynamicInput(_lastPointer);
        DynamicValueTextBox.Focus();
        DynamicValueTextBox.SelectedText = e.Text;
        DynamicValueTextBox.CaretIndex = DynamicValueTextBox.Text.Length;
        e.Handled = true;
    }

    private IntPtr WorkAreaHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0024) return IntPtr.Zero; // WM_GETMINMAXINFO: physical monitor coordinates.
        var monitor = MonitorFromWindow(hwnd, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        limits.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
