using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopTaskNotes.Interop;

internal static class WindowNative
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const int SwShownoactivate = 4;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotopmost = new(-2);
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpShowwindow = 0x0040;

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

    public static void RestoreWithoutActivation(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        ShowWindow(handle, SwShownoactivate);
        SetWindowPos(handle, window.Topmost ? HwndTopmost : HwndNotopmost, 0, 0, 0, 0,
            SwpNomove | SwpNosize | SwpNoactivate | SwpShowwindow);
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
}
