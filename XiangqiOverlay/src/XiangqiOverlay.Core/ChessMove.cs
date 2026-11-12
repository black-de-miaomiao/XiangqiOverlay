namespace XiangqiOverlay.Core;

/// <summary>
/// 一步棋的走法
/// </summary>
public readonly struct ChessMove : IEquatable<ChessMove>
{
    public BoardPoint From { get; }
    public BoardPoint To { get; }

    public ChessMove(BoardPoint from, BoardPoint to)
    {
        From = from;
        To = to;
    }

    /// <summary>
    /// 转换为 UCCI 坐标表示（如 "h2e2"）
    /// </summary>
    public string ToUcci()
    {
        return $"{From.ToUcci()}{To.ToUcci()}";
    }

    /// <summary>
    /// 从 UCCI 字符串解析
    /// </summary>
    public static ChessMove FromUcci(string ucci)
    {
        if (string.IsNullOrEmpty(ucci) || ucci.Length < 4)
            throw new ArgumentException("UCCI 棋步至少需要 4 个字符", nameof(ucci));

        var from = BoardPoint.FromUcci(ucci.Substring(0, 2));
        var to = BoardPoint.FromUcci(ucci.Substring(2, 2));
        return new ChessMove(from, to);
    }

    public bool Equals(ChessMove other) => From == other.From && To == other.To;
    public override bool Equals(object? obj) => obj is ChessMove m && Equals(m);
    public override int GetHashCode() => HashCode.Combine(From, To);
    public override string ToString() => ToUcci();

    public static bool operator ==(ChessMove a, ChessMove b) => a.Equals(b);
    public static bool operator !=(ChessMove a, ChessMove b) => !a.Equals(b);
}
