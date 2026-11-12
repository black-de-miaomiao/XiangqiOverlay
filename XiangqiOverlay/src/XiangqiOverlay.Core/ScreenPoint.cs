namespace XiangqiOverlay.Core;

/// <summary>
/// 屏幕坐标点（不依赖 WPF 或 WinForms 的独立类型）
/// </summary>
public readonly struct ScreenPoint : IEquatable<ScreenPoint>
{
    public double X { get; }
    public double Y { get; }

    public ScreenPoint(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static ScreenPoint operator +(ScreenPoint a, ScreenPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static ScreenPoint operator -(ScreenPoint a, ScreenPoint b) => new(a.X - b.X, a.Y - b.Y);

    public double LengthSquared => X * X + Y * Y;
    public double Length => Math.Sqrt(LengthSquared);

    public bool Equals(ScreenPoint other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is ScreenPoint p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X:F1},{Y:F1})";

    public static bool operator ==(ScreenPoint a, ScreenPoint b) => a.Equals(b);
    public static bool operator !=(ScreenPoint a, ScreenPoint b) => !a.Equals(b);

    /// <summary>
    /// 线性插值
    /// </summary>
    public static ScreenPoint Lerp(ScreenPoint a, ScreenPoint b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
