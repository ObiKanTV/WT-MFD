using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WtMfd;

/// <summary>
/// The MFD as its own app window: borderless, no browser chrome, the page drawn by WebView2 (the Edge engine
/// that ships with Windows). Drag it by the bezel, resize it at the edges, double-click the bezel or press F11 to
/// fill the screen it is on. Position and size are remembered in window.json next to the app.
/// Plain Win32 rather than WinForms/WPF, so the app stays a single small program.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfdWindow
{
    public const string ClassName = "WTMFDWindow";
    private const int EdgeDip = 6;                 // resize border around the page, in 96-dpi pixels
    private const uint WM_APP_RUN = 0x8000 + 1;    // "run queued UI work" (await continuations)

    private readonly string _url, _stateFile, _dataDir, _iconFile;
    private IntPtr _hwnd;
    private CoreWebView2Controller? _ctl;
    private UiContext? _ctx;
    private WndProc? _proc;                        // held so the delegate outlives the window
    private Exception? _fatal;

    public MfdWindow(string url, string appDir)
    {
        _url = url;
        _stateFile = Path.Combine(appDir, "window.json");
        _iconFile = Path.Combine(appDir, "wtmfd.ico");
        // WebView2 keeps its browser profile here (cache, local storage). Not next to the app: when the app runs
        // through dotnet.exe, "next to the program" would be Program Files.
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WT-MFD", "WebView2");
    }

    /// <summary>If WT-MFD is already open, bring that window forward and return true.</summary>
    public static bool BringExistingToFront()
    {
        var h = FindWindowW(ClassName, null);
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
        return true;
    }

    public static void Message(string text, bool error = true) =>
        MessageBoxW(IntPtr.Zero, text, "WT-MFD", error ? MB_ICONERROR : MB_ICONINFORMATION);

    /// <summary>Opens the window on its own UI thread and returns when it is closed.</summary>
    public void Run()
    {
        var t = new Thread(UiThread) { Name = "MFD window" };
        t.SetApartmentState(ApartmentState.STA); // WebView2 is COM and needs a single-threaded apartment
        t.Start();
        t.Join();
        if (_fatal != null) throw _fatal;
    }

    // ------------------------------------------------------------------ window
    private void UiThread()
    {
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); // sharp text on every screen
            var hInst = GetModuleHandleW(null);
            var icon = File.Exists(_iconFile) ? LoadImageW(IntPtr.Zero, _iconFile, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE) : IntPtr.Zero;
            _proc = WindowProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = hInst,
                hIcon = icon,
                hIconSm = icon,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                hbrBackground = CreateSolidBrush(0x000D0C0B), // exactly the bezel colour (#0b0c0d), so the resize edge is invisible
                lpszClassName = ClassName,
            };
            if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

            _hwnd = CreateWindowExW(WS_EX_APPWINDOW, ClassName, "WT-MFD",
                WS_POPUP | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX,
                CW_USEDEFAULT, CW_USEDEFAULT, 900, 860, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

            // Look like Claude Desktop / Spotify on Windows 11: rounded corners, dark window, and no light outline.
            // (Windows 11 draws a 1 px border around every window, light grey in light mode; turn it off.)
            // All three are ignored on Windows 10.
            var round = DWMWCP_ROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            var dark = 1;
            DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            var noBorder = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

            _ctx = new UiContext(_hwnd);
            SynchronizationContext.SetSynchronizationContext(_ctx);

            var wp = InitialPlacement();
            SetWindowPlacement(_hwnd, ref wp); // also shows the window
            _ = InitWebViewAsync();

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception e) { _fatal = e; }
    }

    private IntPtr WindowProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        switch (m)
        {
            case WM_APP_RUN:
                _ctx?.RunPending();
                return IntPtr.Zero;

            case WM_NCCALCSIZE when w != IntPtr.Zero:
                // no title bar or frame: the page fills the whole window. When maximized, Windows places the window
                // a frame's width outside the screen on every side, so pull the page back inside.
                if (IsZoomed(h))
                {
                    var r = Marshal.PtrToStructure<RECT>(l);
                    var dpi = (int)GetDpiForWindow(h);
                    var f = GetSystemMetricsForDpi(SM_CXFRAME, (uint)dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, (uint)dpi);
                    r.left += f; r.top += f; r.right -= f; r.bottom -= f;
                    Marshal.StructureToPtr(r, l, false);
                }
                return IntPtr.Zero;

            case WM_NCACTIVATE:
                // When the window gains or loses focus, Windows' default handling paints the old grey resize frame
                // straight over the window edges (our page reaches the edges, so there is no frame area to hide it in).
                // Accept the focus change without letting it paint, and repaint the edge in the bezel colour.
                InvalidateRect(h, IntPtr.Zero, true);
                return (IntPtr)1;

            case WM_NCPAINT:
                return IntPtr.Zero; // nothing outside the page to paint

            case WM_NCHITTEST:
                return (IntPtr)HitTest(h, l);

            case WM_SIZE:
                Layout();
                return IntPtr.Zero;

            case WM_MOVE:
                _ctl?.NotifyParentWindowPositionChanged();
                return IntPtr.Zero;

            case WM_SETFOCUS:
                _ctl?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
                return IntPtr.Zero;

            case WM_DPICHANGED:
            {
                // moved to a screen with another scale: take the size Windows suggests
                var r = Marshal.PtrToStructure<RECT>(l);
                SetWindowPos(h, IntPtr.Zero, r.left, r.top, r.right - r.left, r.bottom - r.top, SWP_NOZORDER | SWP_NOACTIVATE);
                return IntPtr.Zero;
            }

            case WM_GETMINMAXINFO:
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(l);
                mmi.ptMinTrackSize = new POINT { x = 320, y = 300 };
                Marshal.StructureToPtr(mmi, l, false);
                return IntPtr.Zero;
            }

            case WM_CLOSE:
                SaveState();
                DestroyWindow(h);
                return IntPtr.Zero;

            case WM_DESTROY:
                try { _ctl?.Close(); } catch { }
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(h, m, w, l);
    }

    private int Edge => EdgeDip * (int)GetDpiForWindow(_hwnd) / 96;

    // The page covers the window except a thin strip at the edges, which stays ours for resizing.
    private int HitTest(IntPtr h, IntPtr l)
    {
        if (IsZoomed(h)) return HTCLIENT;
        GetWindowRect(h, out var r);
        int x = (short)((long)l & 0xFFFF), y = (short)(((long)l >> 16) & 0xFFFF), e = Edge;
        bool left = x < r.left + e, right = x >= r.right - e, top = y < r.top + e, bottom = y >= r.bottom - e;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCLIENT;
    }

    private void Layout()
    {
        if (_ctl == null) return;
        GetClientRect(_hwnd, out var r);
        var e = IsZoomed(_hwnd) ? 0 : Edge;
        _ctl.Bounds = new Rectangle(e, e, Math.Max(0, r.right - 2 * e), Math.Max(0, r.bottom - 2 * e));
    }

    // ------------------------------------------------------------------ the page
    private async Task InitWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, _dataDir);
            _ctl = await env.CreateCoreWebView2ControllerAsync(_hwnd);
            _ctl.DefaultBackgroundColor = Color.FromArgb(255, 0x0b, 0x0c, 0x0d); // the bezel colour, also before the page has loaded
            var web = _ctl.CoreWebView2;
            var s = web.Settings;
            s.IsNonClientRegionSupportEnabled = true;   // the page marks the bezel as "drag the window here" (CSS app-region)
            s.AreDefaultContextMenusEnabled = false;    // no browser right-click menu
            s.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+R, Ctrl+P, F5... it is an instrument, not a browser
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDevToolsEnabled = false;
            web.WebMessageReceived += OnPageMessage;
            web.NewWindowRequested += (_, a) => a.Handled = true; // never open pop-ups
            web.DocumentTitleChanged += (_, _) => SetWindowTextW(_hwnd, web.DocumentTitle is { Length: > 0 } t ? t : "WT-MFD");
            Layout();
            web.Navigate(_url);
            _ctl.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Message("WT-MFD needs the Microsoft Edge WebView2 Runtime, which is normally part of Windows.\n\n" +
                    "Install it with:\n    winget install Microsoft.EdgeWebView2Runtime\n\nThe MFD is still available in a browser at " + _url);
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception e)
        {
            Message("The MFD window could not start:\n\n" + e.Message + "\n\nThe MFD is still available in a browser at " + _url);
            PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // window buttons and dragging, sent from the page with window.chrome.webview.postMessage(...)
    private void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string msg;
        try { msg = e.TryGetWebMessageAsString(); } catch { return; }
        switch (msg)
        {
            case "close": PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
            case "minimize": ShowWindow(_hwnd, SW_MINIMIZE); break;
            case "maximize": ShowWindow(_hwnd, IsZoomed(_hwnd) ? SW_RESTORE : SW_MAXIMIZE); break;
            case "drag":
                // fallback for WebView2 versions without app-region support: start a title-bar drag by hand
                ReleaseCapture();
                SendMessageW(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                break;
        }
    }

    // ------------------------------------------------------------------ remembered position
    private sealed record Saved(int Left, int Top, int Right, int Bottom, bool Maximized);

    private WINDOWPLACEMENT InitialPlacement()
    {
        var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>(), showCmd = SW_SHOWNORMAL };
        try
        {
            if (File.Exists(_stateFile))
            {
                var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(_stateFile));
                var r = s == null ? default : new RECT { left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom };
                // only if that spot is still on a connected screen
                if (s != null && r.right - r.left >= 200 && r.bottom - r.top >= 200 && MonitorFromRect(ref r, MONITOR_DEFAULTTONULL) != IntPtr.Zero)
                {
                    wp.rcNormalPosition = r;
                    wp.showCmd = s.Maximized ? SW_MAXIMIZE : SW_SHOWNORMAL;
                    return wp;
                }
            }
        }
        catch { /* unreadable: use the default */ }

        // First start: fill the first screen that isn't the main one (the MFD screen); with one screen, a window in the middle.
        IntPtr target = IntPtr.Zero, primary = IntPtr.Zero;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (mon, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(mon, ref mi);
            if ((mi.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = mon;
            else if (target == IntPtr.Zero) target = mon;
            return true;
        }, IntPtr.Zero);
        var use = target != IntPtr.Zero ? target : primary;
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (use != IntPtr.Zero && GetMonitorInfoW(use, ref info))
        {
            var work = info.rcWork;
            int ww = Math.Min(900, work.right - work.left), wh = Math.Min(860, work.bottom - work.top);
            int x = work.left + (work.right - work.left - ww) / 2, y = work.top + (work.bottom - work.top - wh) / 2;
            wp.rcNormalPosition = new RECT { left = x, top = y, right = x + ww, bottom = y + wh };
            wp.showCmd = target != IntPtr.Zero ? SW_MAXIMIZE : SW_SHOWNORMAL;
        }
        else
        {
            wp.rcNormalPosition = new RECT { left = 100, top = 100, right = 1000, bottom = 960 };
        }
        return wp;
    }

    private void SaveState()
    {
        try
        {
            var wp = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(_hwnd, ref wp)) return;
            var r = wp.rcNormalPosition;
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(new Saved(r.left, r.top, r.right, r.bottom, wp.showCmd == SW_MAXIMIZE)));
        }
        catch { /* a read-only folder just means the position isn't remembered */ }
    }

    // ------------------------------------------------------------------ await support on the window thread
    private sealed class UiContext : SynchronizationContext
    {
        private readonly IntPtr _hwnd;
        private readonly ConcurrentQueue<(SendOrPostCallback Cb, object? State)> _queue = new();
        public UiContext(IntPtr hwnd) { _hwnd = hwnd; }
        public override void Post(SendOrPostCallback d, object? state)
        {
            _queue.Enqueue((d, state));
            PostMessageW(_hwnd, WM_APP_RUN, IntPtr.Zero, IntPtr.Zero);
        }
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override SynchronizationContext CreateCopy() => this;
        public void RunPending()
        {
            while (_queue.TryDequeue(out var item)) item.Cb(item.State);
        }
    }

    // ------------------------------------------------------------------ Win32
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; public uint lPrivate; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT { public uint length, flags; public int showCmd; public POINT ptMinPosition, ptMaxPosition; public RECT rcNormalPosition; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint CS_VREDRAW = 0x1, CS_HREDRAW = 0x2, CS_DBLCLKS = 0x8;
    private const uint WS_POPUP = 0x80000000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
                       WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_EX_APPWINDOW = 0x00040000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const uint WM_DESTROY = 0x2, WM_MOVE = 0x3, WM_SIZE = 0x5, WM_SETFOCUS = 0x7, WM_CLOSE = 0x10,
                       WM_GETMINMAXINFO = 0x24, WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_NCLBUTTONDOWN = 0xA1,
                       WM_DPICHANGED = 0x2E0, WM_NCACTIVATE = 0x86, WM_NCPAINT = 0x85;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int SW_SHOWNORMAL = 1, SW_MINIMIZE = 6, SW_MAXIMIZE = 3, SW_RESTORE = 9;
    private const int SM_CXFRAME = 32, SM_CXPADDEDBORDER = 92;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;
    private const uint MONITOR_DEFAULTTONULL = 0, MONITORINFOF_PRIMARY = 1;
    private const uint MB_ICONERROR = 0x10, MB_ICONINFORMATION = 0x40;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private static readonly IntPtr IDC_ARROW = (IntPtr)32512;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}
