namespace XiangqiOverlay.Core;

/// <summary>
/// 棋盘上一个交叉点的棋子状态
/// </summary>
public sealed class BoardCell
{
    public PieceType Type { get; set; } = PieceType.Empty;
    public PieceColor Color { get; set; } = PieceColor.Unknown;

    public bool IsEmpty => Type == PieceType.Empty;

    /// <summary>
    /// 转换为 FEN 字符
    /// 红方：K A B N R C P
    /// 黑方：k a b n r c p
    /// 空：.（或数字表示连续空位）
    /// </summary>
    public char ToFenChar()
    {
        char c = Type switch
        {
            PieceType.King => 'K',
            PieceType.Advisor => 'A',
            PieceType.Elephant => 'B',
            PieceType.Horse => 'N',
            PieceType.Chariot => 'R',
            PieceType.Cannon => 'C',
            PieceType.Pawn => 'P',
            _ => '.'
        };

        return Color == PieceColor.Black ? char.ToLower(c) : c;
    }

    public void SetFromFenChar(char c)
    {
        Color = char.IsUpper(c) ? PieceColor.Red : PieceColor.Black;
        Type = char.ToLower(c) switch
        {
            'k' => PieceType.King,
            'a' => PieceType.Advisor,
            'b' => PieceType.Elephant,
            'n' => PieceType.Horse,
            'r' => PieceType.Chariot,
            'c' => PieceType.Cannon,
            'p' => PieceType.Pawn,
            _ => PieceType.Empty
        };
    }
}
