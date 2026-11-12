namespace XiangqiOverlay.Core;

/// <summary>
/// 引擎分析结果中的单条候选着法
/// </summary>
public sealed class AnalysisMove
{
    /// <summary>UCCI 坐标，如 "h2e2"</summary>
    public string Move { get; set; } = string.Empty;

    /// <summary>该手的胜率评估（0~1，从当前行棋方视角）</summary>
    public double WinRate { get; set; }

    /// <summary>该手的评分（厘 UC-Pawn 单位）</summary>
    public int Score { get; set; }

    /// <summary>搜索深度</summary>
    public int Depth { get; set; }

    /// <summary>该手的访问节点数</summary>
    public long Nodes { get; set; }

    /// <summary>主要变着序列</summary>
    public string PvString { get; set; } = string.Empty;

    /// <summary>从 PV 字符串解析出的变着列表</summary>
    public IReadOnlyList<ChessMove> PvMoves
    {
        get
        {
            if (string.IsNullOrEmpty(PvString))
                return Array.Empty<ChessMove>();
            var moves = new List<ChessMove>();
            for (int i = 0; i + 3 < PvString.Length; i += 4)
            {
                if (PvString.Substring(i, 4) is { Length: 4 } sub)
                {
                    try { moves.Add(ChessMove.FromUcci(sub)); }
                    catch { break; }
                }
            }
            return moves;
        }
    }

    public ChessMove ToChessMove()
    {
        if (Move.Length >= 4)
            return ChessMove.FromUcci(Move);
        throw new InvalidOperationException($"无效的 UCCI 棋步: {Move}");
    }
}

/// <summary>
/// 引擎完整分析结果
/// </summary>
public sealed class AnalysisResult
{
    /// <summary>当前盘面 FEN</summary>
    public string Fen { get; set; } = string.Empty;

    /// <summary>所有候选着法（按评分排序）</summary>
    public List<AnalysisMove> Moves { get; set; } = new();

    /// <summary>最佳着法</summary>
    public AnalysisMove? BestMove => Moves.Count > 0 ? Moves[0] : null;

    /// <summary>
    /// 当前胜率（0~1，从当前行棋方视角）
    /// </summary>
    public double WinRate { get; set; }

    /// <summary>
    /// 当前评分（厘 UC-Pawn 单位）
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// 搜索深度
    /// </summary>
    public int Depth { get; set; }

    /// <summary>总访问节点数</summary>
    public long TotalNodes { get; set; }

    /// <summary>引擎名称</summary>
    public string EngineName { get; set; } = string.Empty;

    /// <summary>思考耗时（毫秒）</summary>
    public long TimeMs { get; set; }

    /// <summary>生成时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
