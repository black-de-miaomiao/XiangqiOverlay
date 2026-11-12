using XiangqiOverlay.Core;
using XiangqiOverlay.Vision;

namespace LogicTest;

public static class BoardIntegrityTests
{
    public static void Run(Action<string, bool> check)
    {
        var committed = new BoardState();
        committed[4, 0] = new BoardCell { Type = PieceType.King, Color = PieceColor.Black };
        committed[4, 9] = new BoardCell { Type = PieceType.King, Color = PieceColor.Red };
        committed[4, 6] = new BoardCell { Type = PieceType.Cannon, Color = PieceColor.Red };
        committed[0, 6] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Red };
        committed[0, 0] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Black };

        var blackMove = new ChessMove(new BoardPoint(0, 0), new BoardPoint(0, 1));
        var observed = XiangqiMoveRules.ApplyMove(committed, blackMove)!;
        observed[4, 6] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };

        bool ok = ConfirmedBoardUpdater.TryBuildMoveCommit(
            committed, observed, new[] { blackMove }, null, out var next);
        check("无关格误识：黑车走子时红炮不得变成车",
            ok && next != null &&
            next[4, 6].Type == PieceType.Cannon &&
            next[0, 1].Type == PieceType.Chariot &&
            next[0, 0].IsEmpty);

        var redMove = new ChessMove(new BoardPoint(0, 6), new BoardPoint(0, 5));
        var middle = XiangqiMoveRules.ApplyMove(committed, redMove)!;
        var final = XiangqiMoveRules.ApplyMove(middle, blackMove)!;
        final[4, 6] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
        ok = ConfirmedBoardUpdater.TryBuildMoveCommit(
            committed, final, new[] { redMove, blackMove }, null, out next);
        check("连续两步：只改变两步相关格，红炮类型保持",
            ok && next != null &&
            next[4, 6].Type == PieceType.Cannon &&
            next[0, 5].Type == PieceType.Pawn &&
            next[0, 1].Type == PieceType.Chariot);

        ok = ConfirmedBoardUpdater.TryBuildMoveCommit(
            committed, observed, Array.Empty<ChessMove>(), null, out next);
        check("无合法走法：拒绝把观察盘面作为自动重锚定结果", !ok && next == null);

        var missingLanding = XiangqiMoveRules.ApplyMove(committed, blackMove)!;
        missingLanding[0, 1] = new BoardCell();
        ok = ConfirmedBoardUpdater.TryBuildMoveCommit(
            committed, missingLanding, new[] { blackMove }, null, out next);
        check("非高亮落点被误读为空：拒绝提交", !ok && next == null);

        ok = ConfirmedBoardUpdater.TryBuildMoveCommit(
            committed, missingLanding, new[] { blackMove },
            new[] { new BoardPoint(0, 1) }, out next);
        check("高亮落点被光晕冲淡：仍由上一盘面重建正确兵种",
            ok && next != null && next[0, 1].Type == PieceType.Chariot);

        var noKings = new BoardState();
        noKings[0, 0] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Black };
        noKings[0, 9] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
        var noKingsTracked = PieceRecognizer.TrackBoard(noKings, noKings.Clone());
        check("视觉追踪不得因规则保底而凭空生成将帅",
            !noKingsTracked.GetAllPieces().Any(p => p.type == PieceType.King));
    }
}
