namespace XiangqiOverlay.Core;

/// <summary>
/// 目标窗口信息
/// </summary>
public sealed class TargetWindow
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public override string ToString()
    {
        if (!string.IsNullOrEmpty(Title))
            return Title;
        return $"{ProcessName} [{ProcessId}]";
    }
}
