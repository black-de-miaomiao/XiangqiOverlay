namespace XiangqiOverlay.Core;

/// <summary>
/// 从已确认盘面和合法走法建立下一基准，禁止视觉噪声改写无关格子的兵种。
/// </summary>
public static class ConfirmedBoardUpdater
{
    public static bool TryBuildMoveCommit(
        BoardState committed,
        BoardState observed,
        IReadOnlyList<ChessMove> moves,
        IReadOnlyCollection<BoardPoint>? highlightedCells,
        out BoardState? next)
    {
        next = null;
        if (moves.Count == 0)
            return false;

        var current = committed.Clone();
        var affected = new HashSet<BoardPoint>();
        var destinations = new HashSet<BoardPoint>();

        foreach (var move in moves)
        {
            var applied = XiangqiMoveRules.ApplyMove(current, move);
            if (applied == null)
                return false;

            affected.Add(move.From);
            affected.Add(move.To);
            destinations.Add(move.To);
            current = applied;
        }

        var highlights = highlightedCells?.ToHashSet() ?? new HashSet<BoardPoint>();
        foreach (var point in affected)
        {
            var expected = current[point];
            var actual = observed[point];

            if (actual.IsEmpty != expected.IsEmpty)
            {
                bool highlightedLanding = destinations.Contains(point) &&
                    highlights.Contains(point) && actual.IsEmpty && !expected.IsEmpty;
                if (!highlightedLanding)
                    return false;
                continue;
            }

            if (!actual.IsEmpty && actual.Color != PieceColor.Unknown &&
                actual.Color != expected.Color)
                return false;
        }

        next = current;
        return true;
    }
}
