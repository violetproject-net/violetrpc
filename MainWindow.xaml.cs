using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace Violetproject;

public partial class MainWindow : Window
{
    private readonly string server;
    private readonly bool selftest;
    private readonly string? rpPipe;
    private readonly string? rpDebug;
    private readonly DiscordRpc rpc;

    private string? gameDetails;
    private string? gameState;
    private string? gameSlug;
    private int gameRoom;

    private bool fullscreen;
    private bool preFullscreenMaximized;
    private int[]? preFullscreenRect;
    private bool hasPreFullscreenNormal;
    private NativeRect preFullscreenNormalRect;

    private static readonly Dictionary<string, string> Games = new(StringComparer.OrdinalIgnoreCase)
    {
        ["blackrain"] = "Black Rain",
        ["lovedrains"] = "Love Drains",
        ["loveyou"] = "Love You",
        ["unaccomplished"] = "Unaccomplished",
        ["yugennoyume"] = "Yugen no Yume",
        ["notedream"] = "Note Dream",
        ["scoobydoo"] = "Scooby Doo"
    };

    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        selftest = args.Contains("--selftest");
        rpPipe = GetArg(args, "--rp-pipe=");
        rpDebug = GetArg(args, "--rp-debug=");
        rpc = new DiscordRpc(rpPipe, rpDebug);
        rpc.OnJoin = (secret) => Dispatcher.Invoke(() => HandleJoin(secret));
        rpc.OnJoinRequest = (userId) => Dispatcher.Invoke(() => HandleJoinRequest(userId));
        server = ResolveServer(args);
        if (selftest)
        {
            Opacity = 0;
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        Loaded += async (_, _) => await InitAsync();
        SourceInitialized += (_, _) =>
        {
            SetTitleIcon();
            InstallMaximizeFix();
            InstallKeyboardHook();
        };
        Closed += (_, _) =>
        {
            try
            {
                if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            }
            catch
            {
            }
            rpc.Dispose();
        };
    }

    private void SetTitleIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
            var decoder = new IconBitmapDecoder(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var target = 16 * VisualTreeHelper.GetDpi(this).DpiScaleX;
            var best = decoder.Frames[0];
            foreach (var frame in decoder.Frames)
            {
                if (Math.Abs(frame.PixelWidth - target) < Math.Abs(best.PixelWidth - target)) best = frame;
            }
            TitleIcon.Source = best;
        }
        catch
        {
        }
    }

    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_SHOWMAXIMIZED = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public POINT MinPosition;
        public POINT MaxPosition;
        public NativeRect NormalPosition;
    }

    private void InstallMaximizeFix()
    {
        try
        {
            if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(WindowProc);
        }
        catch
        {
        }
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F11 = 0x7A;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private LowLevelKeyboardProc? keyboardProc;
    private IntPtr keyboardHook = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);

    private void InstallKeyboardHook()
    {
        try
        {
            keyboardProc = KeyboardHookProc; // keep alive for the hook
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
        }
        catch
        {
        }
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 &&
                (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) &&
                Marshal.ReadInt32(lParam) == VK_F11 &&
                IsActive)
            {
                Dispatcher.BeginInvoke(() => ToggleFullscreen());
                return (IntPtr)1; // handled: swallow the key
            }
        }
        catch
        {
        }
        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            try
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
                    mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
                    mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                }
                handled = true;
            }
            catch
            {
            }
        }
        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (e.ClickCount == 2)
            {
                if (fullscreen)
                {
                    ToggleFullscreen();
                    return;
                }
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                DragMove();
            }
        }
        catch
        {
        }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (fullscreen)
        {
            ToggleFullscreen();
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Full_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.F11)
        {
            e.Handled = true;
            ToggleFullscreen();
        }
    }


    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        UpdateWindowButtons();
    }

    private void UpdateWindowButtons()
    {
        if (MaxButton == null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "Restore" : "Maximize";
        FullButton.Content = fullscreen ? "\uE73F" : "\uE740";
        FullButton.ToolTip = fullscreen ? "Exit fullscreen" : "Fullscreen";
    }


    private void ToggleFullscreen()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (!fullscreen)
            {
                preFullscreenMaximized = WindowState == WindowState.Maximized;
                hasPreFullscreenNormal = false;
                if (preFullscreenMaximized)
                {

                    var wp = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                    if (GetWindowPlacement(hwnd, ref wp))
                    {
                        preFullscreenNormalRect = wp.NormalPosition;
                        hasPreFullscreenNormal = true;
                    }
                    WindowState = WindowState.Normal;
                }
                if (GetWindowRect(hwnd, out var now))
                {
                    preFullscreenRect = new[] { now.Left, now.Top, now.Right - now.Left, now.Bottom - now.Top };
                }
                TopBar.Visibility = Visibility.Collapsed;
                TopRow.Height = new GridLength(0);
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    SetWindowPos(
                        hwnd,
                        IntPtr.Zero,
                        info.rcMonitor.Left,
                        info.rcMonitor.Top,
                        info.rcMonitor.Right - info.rcMonitor.Left,
                        info.rcMonitor.Bottom - info.rcMonitor.Top,
                        SWP_NOZORDER | SWP_NOACTIVATE);
                }
                fullscreen = true;
                UpdateWindowButtons();
                Web.Focus(); // so F11 reaches the low-level keyboard hook
                ShowFullscreenHint();
            }
            else
            {
                fullscreen = false;
                TopBar.Visibility = Visibility.Visible;
                TopRow.Height = new GridLength(32);
                if (preFullscreenMaximized)
                {
                    WindowState = WindowState.Maximized;
                    if (hasPreFullscreenNormal)
                    {

                        var wp = new WINDOWPLACEMENT
                        {
                            Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                            ShowCmd = SW_SHOWMAXIMIZED,
                            NormalPosition = preFullscreenNormalRect
                        };
                        SetWindowPlacement(hwnd, ref wp);
                    }
                }
                else if (preFullscreenRect != null)
                {
                    SetWindowPos(hwnd, IntPtr.Zero, preFullscreenRect[0], preFullscreenRect[1], preFullscreenRect[2], preFullscreenRect[3], SWP_NOZORDER | SWP_NOACTIVATE);
                }
                UpdateWindowButtons();
            }
            Log("fullscreen: " + fullscreen);
        }
        catch
        {
        }
    }


    private void ShowFullscreenHint()
    {
        try
        {
            var js = @"
(() => {
  var d = document.getElementById('vp-fs-hint');
  if (!d) {
    d = document.createElement('div');
    d.id = 'vp-fs-hint';
    d.textContent = 'Fullscreen \u2014 press F11 to exit';
    var s = d.style;
    s.position = 'fixed'; s.top = '16px'; s.left = '50%'; s.transform = 'translateX(-50%)';
    s.background = 'rgba(37,16,47,0.92)'; s.color = '#e6e6fa';
    s.font = '12px ""Segoe UI"", sans-serif';
    s.padding = '8px 14px'; s.border = '1px solid #3d1b52'; s.borderRadius = '4px';
    s.zIndex = '2147483647'; s.pointerEvents = 'none'; s.transition = 'opacity .5s';
    document.body.appendChild(d);
  }
  d.style.opacity = '1';
  clearTimeout(window.__vpFsHint);
  window.__vpFsHint = setTimeout(function () { d.style.opacity = '0'; }, 3500);
})();";
            _ = Web.ExecuteScriptAsync(js);
        }
        catch
        {
        }
    }

    private static string? GetArg(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?.Substring(prefix.Length);

    private void Log(string message)
    {
        if (rpDebug == null) return;
        try
        {
            File.AppendAllText(rpDebug, DateTime.Now.ToString("HH:mm:ss.fff") + " [win] " + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private static string ResolveServer(string[] args)
    {
        var arg = GetArg(args, "--server=");
        if (!string.IsNullOrWhiteSpace(arg)) return arg.TrimEnd('/');
        var env = Environment.GetEnvironmentVariable("VIOLETPROJECT_SERVER");
        if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');
        return "https://violetproject.net";
    }

    private async Task InitAsync()
    {
        try
        {
            var webData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Violetproject",
                "webview");
            Directory.CreateDirectory(webData);
            var env = await CoreWebView2Environment.CreateAsync(null, webData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e)
        {
            MessageBox.Show(
                this,
                "WebView2 runtime not available.\nInstall the Edge WebView2 Runtime and retry.\n\n" + e.Message,
                "Violetproject",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Application.Current?.Shutdown(1);
            return;
        }

        var core = Web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternal(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!IsSameSite(e.Uri))
            {
                e.Cancel = true;
                OpenExternal(e.Uri);
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            Log("nav completed (" + (e.IsSuccess ? "ok" : "fail") + "): " + (Web.CoreWebView2?.Source ?? "?"));
            gameDetails = null;
            gameState = null;
            gameSlug = null;
            gameRoom = 0;
            UpdatePresence();
        };
        core.DocumentTitleChanged += (_, _) =>
        {
            Log("title: " + (Web.CoreWebView2?.DocumentTitle ?? "?"));
            UpdatePresence();
        };
        core.WebMessageReceived += (_, e) => HandleGamePresence(e);


        if (!selftest || rpPipe != null) rpc.Start();

        core.Navigate(server);

        if (selftest)
        {
            var tcs = new TaskCompletionSource<bool>();
            void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= Done;
                tcs.TrySetResult(e.IsSuccess);
            }
            core.NavigationCompleted += Done;
            var ok = await tcs.Task;
            if (rpPipe != null) await Task.Delay(4000); 
            Console.WriteLine(ok ? "SELFTEST_OK" : "SELFTEST_FAIL");
            Application.Current?.Shutdown(ok ? 0 : 1);
        }
    }

    private bool IsSameSite(string uri)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
            if (!Uri.TryCreate(server, UriKind.Absolute, out var s)) return false;
            return u.Host.Equals(s.Host, StringComparison.OrdinalIgnoreCase)
                || u.Host.EndsWith("." + s.Host, StringComparison.OrdinalIgnoreCase)
                || s.Host.EndsWith("." + u.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void OpenExternal(string url)
    {
        try
        {
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void UpdatePresence()
    {
        try
        {

            var urlNow = Web.CoreWebView2?.Source ?? "";
            if (gameDetails != null && gameSlug != null && UrlGameSlug(urlNow) != gameSlug)
            {
                gameDetails = null;
                gameState = null;
                gameSlug = null;
                gameRoom = 0;
            }
            string details;
            string? partyId = null;
            string? joinSecret = null;
            string? artSlug = null;
            string? artName = null;
            if (gameDetails != null)
            {

                details = gameState != null ? gameDetails + " - " + gameState : gameDetails;

                artName = gameDetails.StartsWith("Playing ", StringComparison.Ordinal)
                    ? gameDetails.Substring("Playing ".Length)
                    : gameDetails;
                if (gameSlug != null)
                {

                    partyId = gameSlug;
                    joinSecret = "vp:" + gameSlug;
                    artSlug = gameSlug;
                }
            }
            else
            {

                var url = Web.CoreWebView2?.Source ?? "";
                var title = Web.CoreWebView2?.DocumentTitle ?? "";
                var slug = SlugFromUrl(url);
                details = slug != null ? "Playing " + Games[slug] : DescribePage(title);
                if (slug != null)
                {
                    partyId = slug;
                    joinSecret = "vp:" + slug;
                    artSlug = slug;
                    artName = Games[slug];
                }
            }
            Log("presence: " + details + (partyId != null ? " | party=" + partyId : "") + (joinSecret != null ? " join=" + joinSecret : "") + (artSlug != null ? " art=" + artSlug : ""));
            rpc.SetPresence(details, server, partyId, joinSecret, artSlug, artName);
        }
        catch
        {
        }
    }

    private static string? SlugFromUrl(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                if (Games.ContainsKey(seg)) return seg.ToLowerInvariant();
            }
        }
        catch
        {
        }
        return null;
    }


    private static string? UrlGameSlug(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            {
                var seg = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault() ?? "";
                seg = seg.ToLowerInvariant();
                if (seg.Length == 0 || seg.Length > 32) return null;
                foreach (var c in seg)
                {
                    var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                    if (!ok) return null;
                }
                return seg;
            }
        }
        catch
        {
        }
        return null;
    }

    private static string DescribePage(string title)
    {
        var name = title.Trim();
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) name = name.Substring(0, dash).Trim();
        if (name.Length > 0 && !name.Contains("violetproject", StringComparison.OrdinalIgnoreCase))
            return name;
        return "Violetproject";
    }

    private void HandleGamePresence(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            Log("web message: " + (e.WebMessageAsJson.Length > 400 ? e.WebMessageAsJson.Substring(0, 400) + "..." : e.WebMessageAsJson));
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "vp-presence") return;
            var name = root.TryGetProperty("gameName", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return;
            var room = 0;
            if (root.TryGetProperty("room", out var r) && r.ValueKind == JsonValueKind.Number)
            {
                _ = r.TryGetInt32(out room);
            }
            var map = root.TryGetProperty("mapName", out var m) ? m.GetString() : null;
            var slug = root.TryGetProperty("game", out var g) ? g.GetString() : null;
            gameDetails = "Playing " + name.Trim();
            gameState = room > 0 && !string.IsNullOrWhiteSpace(map) ? map.Trim() : null;
            gameSlug = string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();
            gameRoom = room > 0 ? room : 0;
            UpdatePresence();
        }
        catch
        {
        }
    }


    private void HandleJoinRequest(string userId)
    {
        try
        {
            if (gameDetails == null) return; 
            Log("join request from " + userId + ": accepting");
            rpc.SendJoinInvite(userId);
        }
        catch
        {
        }
    }


    private void HandleJoin(string secret)
    {
        try
        {
            if (!secret.StartsWith("vp:", StringComparison.Ordinal)) return;
            var slug = secret.Substring(3).Trim().ToLowerInvariant();
            if (slug.Length == 0 || slug.Length > 64) return;
            foreach (var c in slug)
            {
                var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) return;
            }
            if (UrlGameSlug(Web.CoreWebView2?.Source ?? "") == slug)
            {
                Log("join: already on " + slug + ", staying");
                return;
            }
            Log("join: opening " + server + "/" + slug);
            Web.CoreWebView2?.Navigate(server + "/" + slug);
        }
        catch
        {
        }
    }
}
