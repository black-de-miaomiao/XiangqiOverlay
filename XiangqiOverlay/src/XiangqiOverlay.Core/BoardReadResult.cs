namespace XiangqiOverlay.Core;

/// <summary>
/// 盘面读取结果
/// </summary>
public sealed class BoardReadResult
{
    /// <summary>识别到的棋盘状态</summary>
    public BoardState Board { get; set; } = new();

    /// <summary>最后识别的一步走法（如果能确定）</summary>
    public ChessMove? LastMove { get; set; }

    /// <summary>读取方式</summary>
    public BoardReadMode Source { get; set; } = BoardReadMode.VisionOnly;

    /// <summary>本次读取时屏幕方向是否翻转（执黑时红方在上）</summary>
    public bool Flipped { get; set; }

    /// <summary>识别置信度（0~1）</summary>
    public double Confidence { get; set; } = 0;

    /// <summary>识别时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>是否成功</summary>
    public bool Success { get; set; } = true;

    /// <summary>错误信息</summary>
    public string? Error { get; set; }

    /// <summary>上次盘面状态（用于比较变化）</summary>
    public BoardState? PreviousBoard { get; set; }

    /// <summary>
    /// 本帧检测到的高亮格（游戏自带的"上一步落点"标记，如 JJ象棋 的绿色光环）。
    /// 游戏标注是真值：用于走法推断时的优先级判定与歧义消解。
    /// </summary>
    public List<BoardPoint> HighlightedCells { get; set; } = new();

    /// <summary>
    /// 比较当前盘面和上次盘面，推断最后一步走法。
    /// 选择优先级：合法走法 > 非法候选；同为合法时，落点带高亮标记的优先；
    /// 若不存在任何合法对，但有高亮格能唯一确定"某子合法走到该格"，据此兜底。
    /// </summary>
    public ChessMove? InferLastMove(BoardState previous) => InferLastMove(previous, null);

    public ChessMove? InferLastMove(BoardState previous, IEnumerable<BoardPoint>? highlightedCells)
    {
        if (previous == null)
            return null;

        var fromCandidates = new List<BoardPoint>();
        var toCandidates = new List<BoardPoint>();

        for (int c = 0; c < BoardState.Cols; c++)
        {
            for (int r = 0; r < BoardState.Rows; r++)
            {
                var pt = new BoardPoint(c, r);
                var old = previous[pt];
                var now = Board[pt];

                // 起点：原来有棋子，现在变空了
                if (!old.IsEmpty && now.IsEmpty)
                {
                    fromCandidates.Add(pt);
                }
                // 终点（普通移动）：原来是空的，现在有棋子
                else if (old.IsEmpty && !now.IsEmpty)
                {
                    toCandidates.Add(pt);
                }
                // 终点（吃子）：原来有对方棋子，现在变成己方棋子
                else if (!old.IsEmpty && !now.IsEmpty && old.Color != now.Color)
                {
                    toCandidates.Add(pt);
                }
            }
        }

        var highlights = highlightedCells?.ToHashSet() ?? new HashSet<BoardPoint>();

        // 寻找最优匹配对 (from, to)，要求：previous[from].Color == Board[to].Color。
        // 优先级：①合法且落点带高亮（游戏标注的真值） ②合法 ③最近邻兜底
        ChessMove? bestLit = null;   int bestLitDist = int.MaxValue;
        ChessMove? bestLegal = null; int bestLegalDist = int.MaxValue;
        ChessMove? bestAny = null;   int minDistanceSq = int.MaxValue;

        foreach (var from in fromCandidates)
        {
            var fromColor = previous[from].Color;
            foreach (var to in toCandidates)
            {
                var toColor = Board[to].Color;
                if (fromColor != toColor)
                    continue;

                int dSq = (from.Col - to.Col) * (from.Col - to.Col) + (from.Row - to.Row) * (from.Row - to.Row);
                if (dSq < minDistanceSq)
                {
                    minDistanceSq = dSq;
                    bestAny = new ChessMove(from, to);
                }

                if (!XiangqiMoveRules.IsLegalMove(previous, from, to, fromColor))
                    continue;

                if (dSq < bestLegalDist)
                {
                    bestLegalDist = dSq;
                    bestLegal = new ChessMove(from, to);
                }
                if (highlights.Contains(to) && dSq < bestLitDist)
                {
                    bestLitDist = dSq;
                    bestLit = new ChessMove(from, to);
                }
            }
        }

        if (bestLit != null) return bestLit;
        if (bestLegal != null) return bestLegal;

        // 兜底：diff 无法配对出合法走法，但恰有高亮格。
        // 高亮格 = 上一步的真实落点；即使光环把落点棋子识别成空格，
        // 仍按"走到该格合法"寻找来源棋子。
        if (highlights.Count > 0)
        {
            foreach (var hl in highlights)
            {
                ChessMove? bestHl = null;
                int bestHlDist = int.MaxValue;
                foreach (var from in fromCandidates)
                {
                    if (from == hl)
                        continue;

                    var moverColor = previous[from].Color;
                    if (moverColor == PieceColor.Unknown)
                        continue;
                    int dSq = (from.Col - hl.Col) * (from.Col - hl.Col) + (from.Row - hl.Row) * (from.Row - hl.Row);
                    if (dSq < bestHlDist && XiangqiMoveRules.IsLegalMove(previous, from, hl, moverColor))
                    {
                        bestHlDist = dSq;
                        bestHl = new ChessMove(from, hl);
                    }
                }
                if (bestHl != null)
                    return bestHl;
            }
        }

        return bestAny;
    }
}
