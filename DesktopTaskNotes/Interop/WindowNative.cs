using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DesktopTaskNotes.Services;

namespace DesktopTaskNotes.Interop;

internal static class WindowNative
{
    public const int WmMoving = 0x0216;
    public const int WmSysCommand = 0x0112;
    private const int ScMaximize = 0xF030;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const int SwShownoactivate = 4;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotopmost = new(-2);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;
    private const uint MonitorDefaultToNearest = 2;

    public static void ConfigureToolWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new nint((style | WsExToolWindow) & ~WsExAppWindow));
    }

    public static void ConfigureTaskbarWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new nint((style | WsExAppWindow) & ~WsExToolWindow));
    }

    public static void DisableMaximize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        SetWindowLongPtr(handle, GwlStyle, new nint(style & ~WsMaximizeBox));
    }

    public static bool IsMaximizeSystemCommand(int message, nint wParam) =>
        message == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScMaximize;

    public static bool SnapMovingRectangle(nint rectanglePointer, IReadOnlyList<SnapRectangle> obstacles)
    {
        if (rectanglePointer == 0) return false;
        var native = Marshal.PtrToStructure<NativeRectangle>(rectanglePointer);
        var current = ToSnapRectangle(native);
        if (current.Width <= 0 || current.Height <= 0) return false;
        var monitor = GetInteractionMonitor(ref native);
        var workArea = GetMonitorWorkArea(monitor, current);
        var distance = GetSnapDistance(monitor);
        var snapped = WindowSnapService.Snap(current, workArea, obstacles, distance, false);
        if (snapped == current) return false;
        Marshal.StructureToPtr(ToNativeRectangle(snapped), rectanglePointer, false);
        return true;
    }

    public static bool SnapWindow(Window window, IReadOnlyList<SnapRectangle> obstacles)
    {
        if (!TryGetWindowRectangle(window, out var current)) return false;
        var native = ToNativeRectangle(current);
        var monitor = GetInteractionMonitor(ref native);
        var snapped = WindowSnapService.Snap(current, GetMonitorWorkArea(monitor, current), obstacles,
            GetSnapDistance(monitor));
        if (snapped == current) return false;
        var handle = new WindowInteropHelper(window).Handle;
        return SetWindowPos(handle, 0, snapped.Left, snapped.Top, 0, 0,
            SwpNosize | SwpNozorder | SwpNoactivate);
    }

    public static bool TryGetWindowRectangle(Window window, out SnapRectangle rectangle)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && GetWindowRect(handle, out var native))
        {
            rectangle = ToSnapRectangle(native);
            return rectangle.Width > 0 && rectangle.Height > 0;
        }
        rectangle = default;
        return false;
    }

    public static void RestoreWithoutActivation(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        ShowWindow(handle, SwShownoactivate);
        SetWindowPos(handle, window.Topmost ? HwndTopmost : HwndNotopmost, 0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
    }

    public static (double X, double Y) GetScaleForPoint(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
                return (dpiX / 96d, dpiY / 96d);
        }
        catch
        {
            // Fall back to the system DPI when the per-monitor API is unavailable.
        }
        return (1, 1);
    }

    private static nint GetInteractionMonitor(ref NativeRectangle rectangle)
    {
        if (GetCursorPos(out var cursor))
        {
            var cursorMonitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
            if (cursorMonitor != 0) return cursorMonitor;
        }
        return MonitorFromRect(ref rectangle, MonitorDefaultToNearest);
    }

    private static SnapRectangle GetMonitorWorkArea(nint monitor, SnapRectangle fallback)
    {
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref information))
            return ToSnapRectangle(information.WorkArea);
        return fallback;
    }

    private static int GetSnapDistance(nint monitor)
    {
        if (monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0)
            return Math.Max(8, (int)Math.Round(WindowSnapService.DefaultSnapDistanceDip * dpiX / 96d));
        return WindowSnapService.DefaultSnapDistanceDip;
    }

    private static SnapRectangle ToSnapRectangle(NativeRectangle rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    private static NativeRectangle ToNativeRectangle(SnapRectangle rectangle) =>
        new() { Left = rectangle.Left, Top = rectangle.Top, Right = rectangle.Right, Bottom = rectangle.Bottom };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle MonitorArea;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRectangle rectangle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint handle, out NativeRectangle rectangle);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
