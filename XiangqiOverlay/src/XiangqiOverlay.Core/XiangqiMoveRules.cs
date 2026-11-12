namespace XiangqiOverlay.Core;

/// <summary>
/// 中国象棋走法合法性校验（纯规则，不涉及视觉）
/// 用于把"识别到的盘面变化"与"真实走子"区分开：
/// 只有能被某一方一步合法走法解释的变化才提交并翻转行棋方，
/// 高亮光晕、半格漂移等识别噪声产生的"瞬移"一律拒绝。
/// </summary>
public static class XiangqiMoveRules
{
    /// <summary>
    /// 判断 side 方位于 from 的棋子走到 to 是否符合行棋规则。
    /// board 为走子前局面；to 为空表示移动，为对方棋子表示吃子。
    /// </summary>
    public static bool IsLegalMove(BoardState board, BoardPoint from, BoardPoint to, PieceColor side)
    {
        if (from.Col == to.Col && from.Row == to.Row)
            return false;

        var piece = board[from.Col, from.Row];
        if (piece.IsEmpty || piece.Color != side)
            return false;

        var target = board[to.Col, to.Row];
        if (!target.IsEmpty && target.Color == side)
            return false;

        int dx = to.Col - from.Col;
        int dy = to.Row - from.Row;

        switch (piece.Type)
        {
            case PieceType.King:
                // 将/帅：九宫内横竖走一步
                return Math.Abs(dx) + Math.Abs(dy) == 1 && InPalace(to, side);

            case PieceType.Advisor:
                // 士/仕：九宫内斜走一步
                return Math.Abs(dx) == 1 && Math.Abs(dy) == 1 && InPalace(to, side);

            case PieceType.Elephant:
            {
                // 相/象：田字，不过河，象眼无阻塞
                if (Math.Abs(dx) != 2 || Math.Abs(dy) != 2)
                    return false;
                if (!SameSideOfRiver(to, side))
                    return false;
                return board[from.Col + dx / 2, from.Row + dy / 2].IsEmpty;
            }

            case PieceType.Horse:
            {
                // 马：日字，蹩马腿检查
                if (!(Math.Abs(dx) == 1 && Math.Abs(dy) == 2) &&
                    !(Math.Abs(dx) == 2 && Math.Abs(dy) == 1))
                    return false;
                int legCol = Math.Abs(dx) == 2 ? from.Col + dx / 2 : from.Col;
                int legRow = Math.Abs(dx) == 2 ? from.Row : from.Row + dy / 2;
                return board[legCol, legRow].IsEmpty;
            }

            case PieceType.Chariot:
                // 车：横竖任意步，路径无阻塞
                return (dx == 0 || dy == 0) && PathClear(board, from, to);

            case PieceType.Cannon:
            {
                if (dx != 0 && dy != 0)
                    return false;
                if (target.IsEmpty)
                    return PathClear(board, from, to); // 平移：路径必须全空
                // 吃子：路径上恰好一个炮架
                return CountPiecesBetween(board, from, to) == 1;
            }

            case PieceType.Pawn:
            {
                // 兵/卒：向前一步；过河后可横走一步，永远不能后退
                int forward = side == PieceColor.Red ? -1 : 1;
                if (dx == 0 && dy == forward)
                    return true;
                bool crossed = side == PieceColor.Red ? from.Row <= 4 : from.Row >= 5;
                return crossed && dy == 0 && Math.Abs(dx) == 1;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 从候选 (from,to) 对中选出距离最近的一对
    /// </summary>
    public static (BoardPoint from, BoardPoint to, int distSq)? NearestLegalPair(
        BoardState boardBeforeMove,
        List<BoardPoint> fromCandidates, List<BoardPoint> toCandidates)
    {
        (BoardPoint, BoardPoint, int)? best = null;
        foreach (var from in fromCandidates)
        {
            var fromColor = boardBeforeMove[from.Col, from.Row].Color;
            if (fromColor == PieceColor.Unknown)
                continue;
            foreach (var to in toCandidates)
            {
                int dSq = (from.Col - to.Col) * (from.Col - to.Col) + (from.Row - to.Row) * (from.Row - to.Row);
                if (best != null && dSq >= best.Value.Item3)
                    continue;
                if (IsLegalMove(boardBeforeMove, from, to, fromColor))
                    best = (from, to, dSq);
            }
        }
        return best;
    }

    /// <summary>
    /// 在盘面副本上执行一步走法（吃子/移动），返回新盘面；走法非法返回 null。
    /// 用于"预期盘面"：引擎给出推荐着法后预生成走完的盘面，下一帧命中即可跳过整轮识别。
    /// </summary>
    public static BoardState? ApplyMove(BoardState board, ChessMove move)
    {
        var piece = board[move.From.Col, move.From.Row];
        if (piece.IsEmpty || piece.Color == PieceColor.Unknown)
            return null;

        var target = board[move.To.Col, move.To.Row];
        if (!target.IsEmpty && target.Color == piece.Color)
            return null;

        if (!IsLegalMove(board, move.From, move.To, piece.Color))
            return null;

        var result = board.Clone();
        result[move.To.Col, move.To.Row] = new BoardCell { Color = piece.Color, Type = piece.Type };
        result[move.From.Col, move.From.Row] = new BoardCell();
        return result;
    }

    /// <summary>
    /// 在两个连续帧之间漏掉一至两步时，按棋规搜索一条能解释目标盘面
    /// 的走法序列。比较目标盘面时只比较空位和颜色，忽略视觉识别可能不
    /// 稳定的棋子类型。搜索目的地只取相对起始盘面发生占用/颜色变化的格，
    /// 将分支数限制在很小范围内，避免把普通识别噪声当成连续走子。
    /// </summary>
    public static bool TryFindMoveSequence(
        BoardState start,
        BoardState target,
        PlayerSide startingPlayer,
        int moveCount,
        out BoardState? finalBoard,
        out List<ChessMove> moves)
        => TryFindMoveSequence(start, target, startingPlayer, moveCount, null, out finalBoard, out moves);

    public static bool TryFindMoveSequence(
        BoardState start,
        BoardState target,
        PlayerSide startingPlayer,
        int moveCount,
        IReadOnlyCollection<BoardPoint>? wildcardOccupiedPoints,
        out BoardState? finalBoard,
        out List<ChessMove> moves)
    {
        BoardState? foundBoard = null;
        var foundMoves = new List<ChessMove>();
        finalBoard = null;
        moves = new List<ChessMove>();
        if (start == null || target == null || moveCount < 1 || moveCount > 2)
            return false;

        var changedPoints = new List<BoardPoint>();
        for (int col = 0; col < BoardState.Cols; col++)
        {
            for (int row = 0; row < BoardState.Rows; row++)
            {
                var before = start[col, row];
                var observed = target[col, row];
                bool occupancyChanged = before.IsEmpty != observed.IsEmpty;
                bool colorChanged = !before.IsEmpty && !observed.IsEmpty &&
                                    observed.Color != PieceColor.Unknown &&
                                    before.Color != observed.Color;
                if (occupancyChanged || colorChanged)
                    changedPoints.Add(new BoardPoint(col, row));
            }
        }

        if (changedPoints.Count == 0)
        {
            if (wildcardOccupiedPoints == null || wildcardOccupiedPoints.Count == 0)
                return false;
        }

        var wildcardPoints = wildcardOccupiedPoints == null
            ? new HashSet<BoardPoint>()
            : wildcardOccupiedPoints.ToHashSet();
        foreach (var point in wildcardPoints)
        {
            if (!changedPoints.Contains(point))
                changedPoints.Add(point);
        }

        static PieceColor ToColor(PlayerSide side)
            => side == PlayerSide.Red ? PieceColor.Red : PieceColor.Black;

        static PlayerSide Other(PlayerSide side)
            => side == PlayerSide.Red ? PlayerSide.Black : PlayerSide.Red;

        bool MatchesObservedOccupancy(BoardState candidate)
        {
            for (int col = 0; col < BoardState.Cols; col++)
            {
                for (int row = 0; row < BoardState.Rows; row++)
                {
                    var actual = candidate[col, row];
                    var observed = target[col, row];
                    var pt = new BoardPoint(col, row);

                    if (actual.IsEmpty != observed.IsEmpty)
                    {
                        // 唯一下放宽免：候选盘面该格有子，但视觉因高亮光晕冲淡被误判为空格，且该格确实为高亮格
                        if (!actual.IsEmpty && observed.IsEmpty && wildcardPoints.Contains(pt))
                            continue;

                        return false;
                    }
                    if (!actual.IsEmpty && observed.Color != PieceColor.Unknown &&
                        actual.Color != observed.Color)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        bool Search(BoardState current, PlayerSide side, int depth)
        {
            if (depth == moveCount)
            {
                if (!MatchesObservedOccupancy(current))
                    return false;

                foundBoard = current;
                return true;
            }

            var color = ToColor(side);
            foreach (var from in current.GetAllPieces()
                         .Where(piece => piece.color == color)
                         .Select(piece => piece.point))
            {
                // 物理铁律：该棋子若要作为走法起点离开，观测盘面中该格绝不能仍坐着同色棋子
                var observedAtFrom = target[from];
                if (!observedAtFrom.IsEmpty && observedAtFrom.Color == color)
                    continue;

                foreach (var to in changedPoints)
                {
                    if (!IsLegalMove(current, from, to, color))
                        continue;

                    var next = ApplyMove(current, new ChessMove(from, to));
                    if (next == null)
                        continue;

                    foundMoves.Add(new ChessMove(from, to));
                    if (Search(next, Other(side), depth + 1))
                        return true;
                    foundMoves.RemoveAt(foundMoves.Count - 1);
                }
            }

            return false;
        }

        if (!Search(start, startingPlayer, 0))
        {
            return false;
        }

        finalBoard = foundBoard;
        moves = foundMoves;
        return true;
    }

    private static bool InPalace(BoardPoint pt, PieceColor side)
    {
        bool colOk = pt.Col >= 3 && pt.Col <= 5;
        return side == PieceColor.Red
            ? colOk && pt.Row >= 7 && pt.Row <= 9
            : colOk && pt.Row >= 0 && pt.Row <= 2;
    }

    private static bool SameSideOfRiver(BoardPoint pt, PieceColor side)
        => side == PieceColor.Red ? pt.Row >= 5 : pt.Row <= 4;

    private static bool PathClear(BoardState board, BoardPoint from, BoardPoint to)
        => CountPiecesBetween(board, from, to) == 0;

    private static int CountPiecesBetween(BoardState board, BoardPoint from, BoardPoint to)
    {
        int count = 0;
        int stepCol = Math.Sign(to.Col - from.Col);
        int stepRow = Math.Sign(to.Row - from.Row);
        int c = from.Col + stepCol;
        int r = from.Row + stepRow;
        while (c != to.Col || r != to.Row)
        {
            if (!board[c, r].IsEmpty)
                count++;
            c += stepCol;
            r += stepRow;
        }
        return count;
    }
}
