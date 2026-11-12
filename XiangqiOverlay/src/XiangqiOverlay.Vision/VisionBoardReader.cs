using OpenCvSharp;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Vision;

/// <summary>
/// 视觉棋盘读取器：截图 + 棋盘检测 + 棋子识别
/// </summary>
public sealed class VisionBoardReader : IDisposable
{
    private readonly BoardDetector _detector = new();
    private readonly PieceRecognizer _recognizer = new();
    private bool _disposed;

    /// <summary>
    /// 从截图读取完整棋盘状态（可传入历史棋盘进行走子继承）
    /// </summary>
    public BoardReadResult ReadBoard(Mat screenshot, BoardCalibration calibration, BoardState? previousBoard = null)
    {
        var result = new BoardReadResult
        {
            Source = BoardReadMode.VisionOnly,
            Timestamp = DateTime.Now
        };

        try
        {
            if (!calibration.IsValid)
            {
                result.Success = false;
                result.Error = "棋盘校准无效";
                return result;
            }

            using var normalized = ScreenshotNormalizer.ToBgr(screenshot);
            var board = _recognizer.Recognize(normalized, calibration, previousBoard);
            result.Board = board;
            result.Confidence = 0.8; // 简化置信度
            result.Flipped = _recognizer.Flipped;
            result.HighlightedCells = _recognizer.LastHighlightedCells.ToList();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// 自动检测棋盘并读取
    /// </summary>
    public (BoardCalibration calibration, BoardReadResult readResult) AutoDetectAndRead(Mat screenshot)
    {
        using var normalized = ScreenshotNormalizer.ToBgr(screenshot);
        var calibration = _detector.AutoDetect(normalized);
        var readResult = ReadBoard(normalized, calibration);
        return (calibration, readResult);
    }

    /// <summary>
    /// 手动校准并读取
    /// </summary>
    public BoardReadResult ReadWithManualCalibration(Mat screenshot,
        ScreenPoint tl, ScreenPoint tr, ScreenPoint bl, ScreenPoint br)
    {
        var calibration = _detector.ManualCalibrate(tl, tr, bl, br);
        return ReadBoard(screenshot, calibration);
    }

    public BoardDetector GetDetector() => _detector;

    /// <summary>
    /// 重置识别器方向锁（更换目标窗口/校准或新对局换边时调用）
    /// </summary>
    public void ResetOrientation() => _recognizer.ResetOrientation();

    /// <summary>
    /// 清空实截贴图库（更换目标窗口或重新校准时调用，可能是另一个游戏的贴图）
    /// </summary>
    public void ResetSprites() => _recognizer.ResetSprites();

    /// <summary>当前实截贴图库样本总数（诊断/测试用）</summary>
    public int GetRecognizerSpriteCount() => _recognizer.SpriteCount;

    /// <summary>
    /// 最近一次 ReadBoard 的原始识别盘面（未经走子继承/规则整型）。
    /// 帧间稳定性确认应以原始识别为准——它不经过配对层，静止场景下帧间差异极小
    /// </summary>
    public BoardState LastRawBoard => _recognizer.LastRawBoard;

    public void Dispose()
    {
        if (!_disposed)
        {
            _detector.Dispose();
            _recognizer.Dispose();
            _disposed = true;
        }
    }
}
