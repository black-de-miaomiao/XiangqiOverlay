using OpenCvSharp;
using XiangqiOverlay.Core;
using XiangqiOverlay.Vision;

namespace LogicTest;

public static class TurnRecoveryTests
{
    public static void Run(Action<string, bool> check)
    {
        const string path = @"C:\Users\123\Pictures\Screenshots\屏幕截图 2026-11-13 022057.png";
        if (!File.Exists(path))
            return;

        using var screenshot = Cv2.ImRead(path);
        using var gameCrop = new Mat(screenshot, new Rect(0, 0, 600, screenshot.Height));
        using var reader = new VisionBoardReader();
        var calibration = new BoardCalibration
        {
            TopLeft = new ScreenPoint(140, 74),
            TopRight = new ScreenPoint(532, 74),
            BottomLeft = new ScreenPoint(140, 525),
            BottomRight = new ScreenPoint(532, 525),
            IsValid = true
        };

        reader.ReadBoard(gameCrop, calibration);
        var raw = reader.LastRawBoard.Clone();
        check("真实卡回合截图：黑卒起点已空、落点仍有黑子",
            raw[2, 5].IsEmpty && !raw[2, 6].IsEmpty && raw[2, 6].Color == PieceColor.Black);

        var previous = raw.Clone();
        previous[2, 5] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Black };
        previous[2, 6] = new BoardCell();
        previous.CurrentPlayer = PlayerSide.Black;

        var tracked = reader.ReadBoard(gameCrop, calibration, previous);
        var move = tracked.InferLastMove(previous, tracked.HighlightedCells);
        check("真实卡回合截图：推导出黑卒 (2,5)->(2,6)",
            move != null && move.Value.From == new BoardPoint(2, 5) &&
            move.Value.To == new BoardPoint(2, 6));

        BoardState? next = null;
        bool ok = move != null && ConfirmedBoardUpdater.TryBuildMoveCommit(
            previous, raw, new[] { move.Value }, tracked.HighlightedCells, out next);
        check("真实卡回合截图：安全提交保留黑卒兵种",
            ok && next != null && next[2, 5].IsEmpty &&
            next[2, 6].Type == PieceType.Pawn && next[2, 6].Color == PieceColor.Black);

        if (next != null)
            next.CurrentPlayer = PlayerSide.Red;
        check("真实卡回合截图：黑方走完后轮到红方",
            next != null && next.CurrentPlayer == PlayerSide.Red && next.ToFen().Contains(" w "));
    }
}
