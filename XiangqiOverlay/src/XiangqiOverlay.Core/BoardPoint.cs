namespace XiangqiOverlay.Core;

/// <summary>
/// 棋盘坐标系中的一个交叉点
/// 中国象棋棋盘为 9 列 × 10 行，交叉点共 90 个
/// 列：0=最左(a) ~ 8=最右(i)
/// 行：0=最上 ~ 9=最下
/// </summary>
public readonly struct BoardPoint : IEquatable<BoardPoint>
{
    public int Col { get; }   // 0..8
    public int Row { get; }  // 0..9

    public BoardPoint(int col, int row)
    {
        if (col < 0 || col > 8)
            throw new ArgumentOutOfRangeException(nameof(col), $"列必须在 0~8 范围内，当前值 {col}");
        if (row < 0 || row > 9)
            throw new ArgumentOutOfRangeException(nameof(row), $"行必须在 0~9 范围内，当前值 {row}");
        Col = col;
        Row = row;
    }

    /// <summary>
    /// 转换为 Pikafish/UCCI 标准坐标表示法
    /// 列：a-i（0->a, 8->i）
    /// 行：0-9（从红方底部开始数，0=红方底线，9=黑方底线）
    /// </summary>
    public string ToUcci()
    {
        char colChar = (char)('a' + Col);
        // Row 0(最上/黑方底线) → UCCI 9
        // Row 9(最下/红方底线) → UCCI 0
        int ucciRow = 9 - Row;
        return $"{colChar}{ucciRow}";
    }

    /// <summary>
    /// 从 UCCI 坐标创建 BoardPoint
    /// </summary>
    public static BoardPoint FromUcci(string ucci)
    {
        if (string.IsNullOrEmpty(ucci) || ucci.Length < 2)
            throw new ArgumentException("UCCI 坐标至少需要 2 个字符", nameof(ucci));

        int col = char.ToLower(ucci[0]) - 'a';
        int row = 9 - (ucci[1] - '0');
        return new BoardPoint(col, row);
    }

    public bool Equals(BoardPoint other) => Col == other.Col && Row == other.Row;
    public override bool Equals(object? obj) => obj is BoardPoint p && Equals(p);
    public override int GetHashCode() => Col * 10 + Row;

    public override string ToString() => $"({Col},{Row})";

    public static bool operator ==(BoardPoint a, BoardPoint b) => a.Equals(b);
    public static bool operator !=(BoardPoint a, BoardPoint b) => !a.Equals(b);
}
