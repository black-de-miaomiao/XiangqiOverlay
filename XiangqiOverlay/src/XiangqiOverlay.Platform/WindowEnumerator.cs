using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Platform;

/// <summary>
/// Win32 API 声明
/// </summary>
public static class Win32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth,
        int nHeight, IntPtr hObjSource, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hObject, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hObject, IntPtr hGdiObj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    public const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const uint WDA_NONE = 0u;
    public const uint WDA_MONITOR = 1u;
    public const uint WDA_EXCLUDEFROMCAPTURE = 17u;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static bool TryGetClientBounds(IntPtr handle, out System.Drawing.Rectangle bounds)
    {
        bounds = default;
        if (!IsWindow(handle) || !GetClientRect(handle, out var rect))
            return false;

        var p1 = new POINT { X = rect.Left, Y = rect.Top };
        var p2 = new POINT { X = rect.Right, Y = rect.Bottom };
        if (!ClientToScreen(handle, ref p1) || !ClientToScreen(handle, ref p2))
            return false;

        bounds = System.Drawing.Rectangle.FromLTRB(p1.X, p1.Y, p2.X, p2.Y);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public static uint GetDpi(IntPtr handle)
    {
        uint dpi = GetDpiForWindow(handle);
        return dpi != 0 ? dpi : 96u;
    }
}

/// <summary>
/// 窗口枚举器：查找可用的目标窗口
/// </summary>
public sealed class WindowEnumerator
{
    /// <summary>
    /// 枚举所有可见窗口
    /// </summary>
    public List<TargetWindow> EnumerateWindows()
    {
        var windows = new List<TargetWindow>();
        int currentPid = Environment.ProcessId;

        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.IsWindowVisible(hWnd))
                return true;

            string title = "";
            int titleLen = Win32.GetWindowTextLength(hWnd);
            if (titleLen > 0)
            {
                var sb = new StringBuilder(titleLen + 1);
                Win32.GetWindowText(hWnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            // 过滤自身窗口或标题为 XiangqiOverlay 的窗口
            if (title.Contains("XiangqiOverlay", StringComparison.OrdinalIgnoreCase))
                return true;

            Win32.GetWindowRect(hWnd, out var rect);
            Win32.GetWindowThreadProcessId(hWnd, out uint pid);

            if ((int)pid == currentPid)
                return true;

            string procName = "Unknown";
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
                if (procName.Contains("XiangqiOverlay", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true; // 无法访问的系统进程跳过
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            // 微信小程序支持：如果是 WeChatAppEx 进程
            bool isWeChatApp = procName.Equals("WeChatAppEx", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(title))
            {
                if (isWeChatApp && width >= 200 && height >= 300)
                {
                    // 尝试通过子窗口查找真实标题
                    string childTitle = GetSubtreeTitle(hWnd);
                    title = !string.IsNullOrEmpty(childTitle) ? childTitle : $"[微信小程序] JJ象棋 ({width}x{height})";
                }
                else
                {
                    // 其他无标题且非微信小程序的窗口跳过
                    return true;
                }
            }
            else if (isWeChatApp && !title.Contains("JJ") && !title.Contains("象棋"))
            {
                string childTitle = GetSubtreeTitle(hWnd);
                if (childTitle.Contains("象棋") || childTitle.Contains("JJ"))
                {
                    title = $"{childTitle} ({title})";
                }
            }

            // 跳过极小窗口
            if (width < 80 || height < 80)
                return true;

            int left = rect.Left;
            int top = rect.Top;
            int right = rect.Right;
            int bottom = rect.Bottom;

            if (Win32.TryGetClientBounds(hWnd, out var clientBounds))
            {
                left = clientBounds.Left;
                top = clientBounds.Top;
                right = clientBounds.Right;
                bottom = clientBounds.Bottom;
            }

            windows.Add(new TargetWindow
            {
                Handle = hWnd,
                Title = title,
                ProcessName = procName,
                ProcessId = (int)pid,
                Left = left,
                Top = top,
                Right = right,
                Bottom = bottom
            });

            return true;
        }, IntPtr.Zero);

        // 优先级排序：象棋/JJ 窗口最优先，微信小程序次之
        windows = windows
            .OrderByDescending(w => w.Title.Contains("象棋", StringComparison.OrdinalIgnoreCase) || w.Title.Contains("JJ", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.ProcessName.Contains("WeChatAppEx", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(w => w.ProcessName.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
            .ThenBy(w => w.Title)
            .ToList();

        try
        {
            var lines = windows.Select(w => $"PID:{w.ProcessId} | Proc:{w.ProcessName} | HWND:0x{w.Handle:X} | Title:[{w.Title}] | Size:{w.Width}x{w.Height}");
            var debugDir = Path.Combine(Path.GetTempPath(), "XiangqiOverlay");
            Directory.CreateDirectory(debugDir);
            File.WriteAllLines(Path.Combine(debugDir, "windows_debug.txt"), lines);
        }
        catch { }

        return windows;
    }

    /// <summary>
    /// 递归或枚举子窗口以提取标题
    /// </summary>
    private static string GetSubtreeTitle(IntPtr hWnd)
    {
        string foundTitle = "";
        try
        {
            Win32.EnumChildWindows(hWnd, (childHwnd, _) =>
            {
                int len = Win32.GetWindowTextLength(childHwnd);
                if (len > 0)
                {
                    var sb = new StringBuilder(len + 1);
                    Win32.GetWindowText(childHwnd, sb, sb.Capacity);
                    string t = sb.ToString().Trim();
                    if (t.Contains("象棋") || t.Contains("JJ") || t.Contains("福利"))
                    {
                        foundTitle = t;
                        return false; // 命中核心词，终止枚举
                    }
                    if (string.IsNullOrEmpty(foundTitle) && t.Length > 1)
                    {
                        foundTitle = t;
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch {}
        return foundTitle;
    }

    /// <summary>
    /// 查找微信中的 JJ象棋窗口
    /// </summary>
    public TargetWindow? FindJJChessWindow()
    {
        var windows = EnumerateWindows();

        // 优先查找标题包含 "象棋" 的窗口
        foreach (var w in windows)
        {
            if (w.Title.Contains("象棋") || w.Title.Contains("JJ") || w.Title.Contains("chess"))
                return w;
        }

        // 其次查找微信窗口中可能包含小程序的
        foreach (var w in windows)
        {
            if ((w.ProcessName.Contains("WeChat") || w.ProcessName.Contains("微信")) &&
                (w.Title.Contains("JJ") || w.Title.Contains("象棋")))
                return w;
        }

        return null;
    }
}
