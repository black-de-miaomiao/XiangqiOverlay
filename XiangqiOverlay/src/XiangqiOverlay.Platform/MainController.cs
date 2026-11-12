using System.Runtime.InteropServices;
using XiangqiOverlay.Core;
using XiangqiOverlay.Engine;
using XiangqiOverlay.Vision;

namespace XiangqiOverlay.Platform;

/// <summary>
/// 主控制器：协调窗口捕获、棋盘识别、引擎分析和悬浮层显示
/// </summary>
public sealed class MainController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly WindowEnumerator _windowEnum = new();
    private readonly ScreenCapturer _capturer = new();
    private readonly VisionBoardReader _visionReader = new();
    private readonly OverlayRenderer _overlay;
    private readonly AnalysisManager _analysisManager;

    private TargetWindow? _targetWindow;
    private BoardCalibration? _calibration;
    private bool _running;
    private Thread? _workerThread;
    private bool _disposed;

    /// <summary>
    /// 当状态更新时触发
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 当分析结果更新时触发
    /// </summary>
    public event Action<AnalysisResult>? AnalysisReceived;

    /// <summary>
    /// 当盘面读取结果更新时触发
    /// </summary>
    public event Action<BoardReadResult>? BoardRead;

    /// <summary>
    /// 当发生错误时触发
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// 当行棋方自动切换时触发
    /// </summary>
    public event Action<PlayerSide>? CurrentPlayerChanged;

    private BoardState? _trackedBoard;

    // ── 预期盘面快速通道 ──
    // 引擎每次给出最佳着法后，预生成"走完之后的盘面"。下一帧若识别结果与
    // 预期盘面完全一致，直接确认该步并翻转行棋方，跳过整轮字模分类——
    // 走子高亮光晕/动画帧导致的误识被彻底绕开（借鉴 VinXiangQi / atopx chessboard）
    private readonly object _expectedLock = new();
    private BoardState? _expectedBoard;
    private ChessMove? _expectedMove;
    private PlayerSide _expectedMover;
    private DateTime _expectedAt;

    // 基准盘面版本号：工作循环与手动操作（下一手推荐）都会推进基准，
    // 循环每帧检查版本号，外部推进后立即采纳新基准，避免各持己见
    private int _commitSeq;

    public TargetWindow? TargetWindow => _targetWindow;
    public BoardCalibration? Calibration => _calibration;
    public OverlayRenderer Overlay => _overlay;
    public bool IsRunning => _running;
    public BoardState? TrackedBoard => _trackedBoard;

    /// <summary>
    /// 引擎给出最佳着法后，预生成"该着法走完后的盘面"供 WorkerLoop 快速比对。
    /// 仅当分析所依据的 FEN 与当前追踪盘面一致时才有效，防止过期结果错配。
    /// </summary>
    private void TryCaptureExpectedBoard(AnalysisResult result)
    {
        try
        {
            var best = result.BestMove;
            var baseBoard = _trackedBoard;
            if (best == null || baseBoard == null)
                return;

            if (!string.Equals(result.Fen, baseBoard.ToFen(), StringComparison.Ordinal))
                return;

            ChessMove move;
            try { move = ChessMove.FromUcci(best.Move); }
            catch { return; }

            var expected = XiangqiMoveRules.ApplyMove(baseBoard, move);
            if (expected == null)
                return;

            lock (_expectedLock)
            {
                _expectedBoard = expected;
                _expectedMove = move;
                _expectedMover = baseBoard.CurrentPlayer;
                _expectedAt = DateTime.Now;
            }
        }
        catch
        {
            // 预期盘面生成失败不影响主流程
        }
    }

    private void ClearExpectedBoard()
    {
        lock (_expectedLock)
        {
            _expectedBoard = null;
            _expectedMove = null;
        }
    }

    /// <summary>
    /// 推送盘面到显示层（小棋盘/悬浮层/状态栏）。
    /// 只在盘面真正变化（提交走子/新对局/重新锚定/手动操作）时调用，
    /// 保证"谁动了才变，没动就不变"。
    /// </summary>
    private void PublishBoardRead(BoardReadResult readResult)
    {
        _overlay.UpdateBoardReadResult(readResult);
        BoardRead?.Invoke(readResult);
    }

    private static int CountOccupancyColorDifferences(BoardState a, BoardState b)
    {
        int count = 0;
        for (int col = 0; col < BoardState.Cols; col++)
        {
            for (int row = 0; row < BoardState.Rows; row++)
            {
                var left = a[col, row];
                var right = b[col, row];
                if (left.IsEmpty != right.IsEmpty ||
                    (!left.IsEmpty && !right.IsEmpty && left.Color != right.Color))
                    count++;
            }
        }

        return count;
    }

    private static bool SameOccupancyAndColor(BoardState a, BoardState b)
        => CountOccupancyColorDifferences(a, b) == 0;

    /// <summary>
    /// 使用游戏高亮格辅助解释被光环遮挡的走子。
    /// </summary>
    private static bool TryHighlightRescue(
        BoardState committedBoard,
        BoardState rawBoard,
        IReadOnlyList<BoardPoint> highlightedCells,
        out BoardState rescued,
        out ChessMove rescueMove)
    {
        rescued = committedBoard;
        rescueMove = default;

        var readResult = new BoardReadResult
        {
            Board = rawBoard,
            HighlightedCells = highlightedCells.ToList()
        };
        var move = readResult.InferLastMove(committedBoard, highlightedCells);
        if (move == null)
            return false;

        var applied = XiangqiMoveRules.ApplyMove(committedBoard, move.Value);
        if (applied == null)
            return false;
        var landing = move.Value.To;
        var from = move.Value.From;
        var highlighted = highlightedCells.ToHashSet();
        int mismatches = 0;
        for (int col = 0; col < BoardState.Cols; col++)
        {
            for (int row = 0; row < BoardState.Rows; row++)
            {
                var point = new BoardPoint(col, row);
                var raw = rawBoard[point];
                var expected = applied[point];

                // 落点在光环下可能暂时被识别为空格
                if (point == landing && highlighted.Contains(point) && raw.IsEmpty && !expected.IsEmpty)
                    continue;

                // 起点棋子必须离开变空
                if (point == from)
                {
                    if (!raw.IsEmpty)
                        return false;
                    continue;
                }

                if (raw.IsEmpty != expected.IsEmpty)
                {
                    mismatches++;
                    if (mismatches > 1)
                        return false;
                    continue;
                }
                if (!raw.IsEmpty && raw.Color != PieceColor.Unknown && raw.Color != expected.Color)
                {
                    mismatches++;
                    if (mismatches > 1)
                        return false;
                    continue;
                }
            }
        }

        rescued = applied;
        rescueMove = move.Value;
        return true;
    }

    /// <summary>
    /// 当我方走子后对方秒下（连续两步），或者识别循环错过中间帧时，
    /// 按两步合法象棋走法追上当前盘面。
    /// 无论以 trackedBoard 还是 rawBoard 为目标，只要存在一条由当前行棋方开始的
    /// 两步合法走法能解释盘面，即直接确认并完成两步追赶。
    /// </summary>
    private static bool TryCatchUpTwoMoves(
        BoardState committedBoard,
        BoardState rawBoard,
        BoardState trackedBoard,
        IReadOnlyList<BoardPoint> highlightedCells,
        out BoardState rescued,
        out List<ChessMove> moves)
    {
        rescued = committedBoard;
        moves = new List<ChessMove>();

        var wildcardPoints = highlightedCells.ToList();

        // 1. 优先以结合了视觉和走子继承的 trackedBoard 作为目标进行两步搜索
        if (XiangqiMoveRules.TryFindMoveSequence(
                committedBoard,
                trackedBoard,
                committedBoard.CurrentPlayer,
                2,
                wildcardPoints,
                out var finalBoard1,
                out moves) && finalBoard1 != null)
        {
            rescued = finalBoard1;
            return true;
        }

        // 2. 其次以高亮修补后的 rawBoard（原始视觉识别）进行两步搜索
        var targetRaw = rawBoard.Clone();
        foreach (var point in highlightedCells)
        {
            var rawCell = targetRaw[point];
            var trackedCell = trackedBoard[point];
            if (rawCell.IsEmpty && !trackedCell.IsEmpty)
            {
                targetRaw[point.Col, point.Row] = new BoardCell
                {
                    Type = trackedCell.Type,
                    Color = trackedCell.Color
                };
            }
        }

        if (XiangqiMoveRules.TryFindMoveSequence(
                committedBoard,
                targetRaw,
                committedBoard.CurrentPlayer,
                2,
                wildcardPoints,
                out var finalBoard2,
                out moves) && finalBoard2 != null)
        {
            rescued = finalBoard2;
            return true;
        }

        moves.Clear();
        return false;
    }

    public MainController(AppSettings settings)
    {
        _settings = settings;
        _overlay = new OverlayRenderer
        {
            TopMoves = settings.TopMoves,
            ExcludeFromCapture = settings.ExcludeOverlayFromCapture,
            OwnColor = settings.OwnColor,
            OnlyRecommendOwnMoves = settings.OnlyRecommendOwnMoves
        };
        _analysisManager = new AnalysisManager(settings);

        _analysisManager.AnalysisReceived += result =>
        {
            TryCaptureExpectedBoard(result);
            _overlay.UpdateAnalysis(result);
            AnalysisReceived?.Invoke(result);
        };
        _analysisManager.StatusChanged += msg => StatusChanged?.Invoke(msg);
        _analysisManager.ErrorOccurred += msg => ErrorOccurred?.Invoke(msg);
    }

    /// <summary>
    /// 初始化引擎
    /// </summary>
    public bool Initialize()
    {
        StatusChanged?.Invoke("正在初始化...");
        bool ok = _analysisManager.Initialize();
        if (ok)
            StatusChanged?.Invoke("初始化完成");
        return ok;
    }

    /// <summary>
    /// 获取可用窗口列表
    /// </summary>
    public List<TargetWindow> GetWindows()
    {
        return _windowEnum.EnumerateWindows();
    }

    /// <summary>
    /// 设置目标窗口
    /// </summary>
    public void SetTargetWindow(TargetWindow window)
    {
        _targetWindow = window;
        StatusChanged?.Invoke($"目标窗口: {window.Title}");
        _overlay.FollowTarget(window);
    }

    /// <summary>
    /// 自动校准棋盘
    /// </summary>
    public bool AutoCalibrate()
    {
        if (_targetWindow == null)
        {
            ErrorOccurred?.Invoke("请先选择目标窗口");
            return false;
        }

        var screenshot = _capturer.CaptureWindow(_targetWindow.Handle);
        if (screenshot.Empty())
        {
            ErrorOccurred?.Invoke("截图失败");
            return false;
        }

        var (calibration, _) = _visionReader.AutoDetectAndRead(screenshot);
        // 记录校准时的客户区尺寸：之后窗口缩放时据此等比缩放网格
        calibration.CalibratedClientWidth = screenshot.Width;
        calibration.CalibratedClientHeight = screenshot.Height;
        _calibration = calibration;
        _overlay.UpdateCalibration(calibration);
        // 新校准可能是不同窗口/不同方向：重置方向锁，并清空实截贴图库
        //（可能是另一个游戏，贴图不可复用）；保留已跟踪盘面防止类型污染
        _visionReader.ResetOrientation();
        _visionReader.ResetSprites();
        ClearExpectedBoard();

        if (calibration.IsValid)
        {
            StatusChanged?.Invoke("棋盘校准完成");
            UpdateOverlayPosition();
            if (_trackedBoard != null)
            {
                RequestAnalysis();
                return true;
            }

            try
            {
                var readResult = _visionReader.ReadBoard(screenshot, _calibration, _trackedBoard);
                readResult.Board.CurrentPlayer = _settings.CurrentPlayer == "Black" ? PlayerSide.Black : PlayerSide.Red;
                _trackedBoard = readResult.Board.Clone();
                _overlay.UpdateBoardReadResult(readResult);
                BoardRead?.Invoke(readResult);
                _analysisManager.SubmitBoard(readResult);
            }
            catch {}
            return true;
        }
        else
        {
            StatusChanged?.Invoke("棋盘校准失败，请尝试手动框选");
            return false;
        }
    }

    /// <summary>
    /// 手动校准棋盘
    /// </summary>
    public void ManualCalibrate(ScreenPoint tl, ScreenPoint tr, ScreenPoint bl, ScreenPoint br)
    {
        SetCalibration(new BoardCalibration
        {
            TopLeft = tl,
            TopRight = tr,
            BottomLeft = bl,
            BottomRight = br,
            IsValid = true,
            Timestamp = DateTime.Now
        });
    }

    /// <summary>
    /// 设置已校准的棋盘数据
    /// </summary>
    public void SetCalibration(BoardCalibration calibration)
    {
        _calibration = calibration;
        _overlay.UpdateCalibration(_calibration);
        UpdateOverlayPosition();
        // 手动框选可能是不同窗口/不同方向：重置方向锁，并清空实截贴图库与预期盘面
        _visionReader.ResetOrientation();
        _visionReader.ResetSprites();
        ClearExpectedBoard();
        StatusChanged?.Invoke("棋盘校准完成");

        if (_targetWindow != null)
        {
            if (_trackedBoard != null)
            {
                RequestAnalysis();
                return;
            }

            try
            {
                var screenshot = _capturer.CaptureWindow(_targetWindow.Handle);
                if (!screenshot.Empty())
                {
                    // 记录校准时的客户区尺寸，供窗口缩放时等比缩放网格
                    _calibration.CalibratedClientWidth = screenshot.Width;
                    _calibration.CalibratedClientHeight = screenshot.Height;

                    // 传入已跟踪盘面：中局重新校准时保留已确认的棋子类型，不推倒重建
                    var readResult = _visionReader.ReadBoard(screenshot, _calibration, _trackedBoard);
                    readResult.Board.CurrentPlayer = _settings.CurrentPlayer == "Black" ? PlayerSide.Black : PlayerSide.Red;
                    _trackedBoard = readResult.Board.Clone();
                    PublishBoardRead(readResult);
                    _analysisManager.SubmitBoard(readResult);
                }
            }
            catch {}
        }
    }

    /// <summary>
    /// 更新悬浮层位置到目标窗口上方
    /// </summary>
    private void UpdateOverlayPosition()
    {
        if (_targetWindow == null)
            return;
        _overlay.FollowTarget(_targetWindow);
    }

    /// <summary>
    /// 开始运行
    /// </summary>
    public void Start()
    {
        if (_running)
            return;

        if (_targetWindow == null)
        {
            ErrorOccurred?.Invoke("请先选择目标窗口");
            return;
        }

        if (_calibration == null || !_calibration.IsValid)
        {
            if (!AutoCalibrate())
            {
                ErrorOccurred?.Invoke("棋盘校准失败");
                return;
            }
        }

        _running = true;
        _overlay.FollowTarget(_targetWindow);
        _overlay.Show();

        _workerThread = new Thread(WorkerLoop) { IsBackground = true, Name = "BoardReader" };
        _workerThread.Start();

        StatusChanged?.Invoke("已开始");
    }

    /// <summary>
    /// 停止运行
    /// </summary>
    public void Stop()
    {
        _running = false;
        _overlay.Hide();
        _analysisManager.StopAnalysis();
        _trackedBoard = null;
        ClearExpectedBoard();
        StatusChanged?.Invoke("已停止");
    }

    /// <summary>
    /// 工作循环：截图 → 识别盘面（带走子继承） → 稳定后检测走步并提交分析
    /// <para>帧间稳定性在"原始识别"上确认（VinXiangQi 方案）——追踪层的假配对
    /// 不会放大噪声；提交通道按优先级：①引擎预期盘面命中（直接确认）
    /// ②变化可被一步合法走法解释（合法走法优先+游戏高亮标记佐证，提交并翻转行棋方）
    /// ③无法解释的变化视为识别噪声继续观察，绝不据此翻转行棋方或刷新显示。
    /// 显示盘面 = 已提交盘面；手动刷新（下一手推荐）推进基准版本号后由本循环立即采纳。</para>
    /// </summary>
    private void WorkerLoop()
    {
        int bigDiffStreak = 0;                 // 连续"高度一致但与基准差异 >6"的轮数
        int illegalStreak = 0;                 // 连续"差异在[2,6]但无法用合法走法解释"的轮数（识别噪声）
        const int IllegalReanchorFrames = 12;  // 噪声持续该轮数后重新锚定（不翻转行棋方）
        BoardState? committedBoard = null;
        var recentFrames = new List<BoardState>(); // 最近 4 帧"原始识别"结果，[0] 最新
        int seenSeq = _commitSeq;

        while (_running && !_disposed)
        {
            try
            {
                if (_targetWindow == null || _calibration == null || !_calibration.IsValid)
                {
                    Thread.Sleep(500);
                    continue;
                }

                // 外部（如"下一手推荐"按钮）推进了基准盘面：立即采纳对齐，
                // 否则循环会继续拿旧基准做比对，表现为一直等待不恢复
                if (seenSeq != _commitSeq)
                {
                    seenSeq = _commitSeq;
                    committedBoard = _trackedBoard?.Clone();
                    recentFrames.Clear();
                    bigDiffStreak = 0;
                    illegalStreak = 0;
                    if (committedBoard == null)
                    {
                        Thread.Sleep(300);
                        continue;
                    }
                }

                // 轮询节奏上限 450ms：VinXiangQi 之所以稳，核心是轮询快——相邻帧
                // diff 几乎总是"一步走子"（2~3 格），大幅变化重锚定那条危险路径
                // 极少被走到。用户设置的间隔仍作为下限被尊重，但不允许更慢。
                int pollMs = Math.Clamp((int)(_settings.AnalysisIntervalSeconds * 1000), 120, 450);

                // 更新悬浮层位置（目标窗口可能移动或缩放了）
                if (Win32.TryGetClientBounds(_targetWindow.Handle, out var clientBounds))
                {
                    _targetWindow.Left = clientBounds.Left;
                    _targetWindow.Top = clientBounds.Top;
                    _targetWindow.Right = clientBounds.Right;
                    _targetWindow.Bottom = clientBounds.Bottom;
                    _overlay.FollowTarget(_targetWindow);
                }

                // 截图
                var screenshot = _capturer.CaptureWindow(_targetWindow.Handle);
                if (screenshot.Empty())
                {
                    Thread.Sleep(200);
                    continue;
                }

                // 窗口缩放检测：截图尺寸与校准时不同（窗口被拖动缩放过），
                // JJ象棋的棋盘内容随窗口等比缩放，校准网格必须同步等比缩放——
                // 否则越往边缘偏差越大（可超过一整格），识别全面错位。
                // 这正是"下着下着对面半边全乱"的根源（借鉴 chess-helper-app 的
                // 窗口几何缩放方案）；比对用截图尺寸，与校准记录同源
                {
                    var cal = _calibration;
                    if (cal != null && cal.CalibratedClientWidth > 0 && cal.CalibratedClientHeight > 0 &&
                        (Math.Abs(screenshot.Width - cal.CalibratedClientWidth) > 2 ||
                         Math.Abs(screenshot.Height - cal.CalibratedClientHeight) > 2))
                    {
                        cal.ScaleTo(
                            (double)screenshot.Width / cal.CalibratedClientWidth,
                            (double)screenshot.Height / cal.CalibratedClientHeight);
                        cal.CalibratedClientWidth = screenshot.Width;
                        cal.CalibratedClientHeight = screenshot.Height;
                        recentFrames.Clear();
                        bigDiffStreak = 0;
                        illegalStreak = 0;
                        StatusChanged?.Invoke($"检测到窗口大小变化({screenshot.Width}x{screenshot.Height})，棋盘校准已自动等比缩放");
                    }
                }

                // 读取盘面（传入已锁定的历史盘面，自动完成棋子类型继承）
                // 注意：本帧识别结果只用于"变化检测"，绝不直接推送显示——
                // 显示盘面 = 已提交盘面，仅在真正提交（走子/新对局/重新锚定）时刷新。
                // 这与 VinXiangQi 的做法一致：帧间识别噪声（高亮光晕/动画/marginal 格
                // 闪烁）永远不进显示层，表现为"谁动了才变，没动就不变"。
                var readResult = _visionReader.ReadBoard(screenshot, _calibration, committedBoard ?? _trackedBoard);
                var rawBoard = _visionReader.LastRawBoard.Clone();

                if (!readResult.Board.GetAllPieces().Any())
                {
                    // 空盘（结算界面/匹配中）：保留基准盘面，等待新对局出现
                    recentFrames.Clear();
                    bigDiffStreak = 0;
                    Thread.Sleep(pollMs);
                    continue;
                }

                // 帧间稳定性在"原始识别"上确认（借鉴 VinXiangQi）：
                // 原始识别不经过走子配对层，静止场景帧间差异 ≤1 格，不会被
                // 追踪层的假配对放大——此前用追踪盘面算稳定性，配对噪声使
                // similarCount 永远凑不齐，是对手下完后"一直等待"的根源
                recentFrames.Insert(0, rawBoard);
                if (recentFrames.Count > 4)
                    recentFrames.RemoveAt(recentFrames.Count - 1);
                int similarCount = recentFrames.Count(b => rawBoard.CountDifferences(b) <= 1);

                bool boardIsOpening = PieceRecognizer.CountInitialColorMatches(readResult.Board) >= 31;
                bool committedIsOpening = committedBoard != null &&
                    PieceRecognizer.CountInitialColorMatches(committedBoard) >= 31;

                // ── 新对局检测：当前帧几乎完整呈现初始阵列，而已提交盘面不在开局形态 ──
                if (boardIsOpening && !committedIsOpening)
                {
                    // 新对局永远红先行，同时同步 UI 与设置；重置方向锁以适配可能换边的持子
                    readResult.Board.CurrentPlayer = PlayerSide.Red;
                    _settings.CurrentPlayer = "Red";
                    CurrentPlayerChanged?.Invoke(PlayerSide.Red);
                    _visionReader.ResetOrientation();

                    committedBoard = readResult.Board.Clone();
                    _trackedBoard = committedBoard.Clone();
                    seenSeq = Interlocked.Increment(ref _commitSeq);
                    _analysisManager.SubmitBoard(readResult);
                    recentFrames.Clear();
                    bigDiffStreak = 0;
                    illegalStreak = 0;
                    ClearExpectedBoard();
                    PublishBoardRead(readResult);
                    StatusChanged?.Invoke("检测到新对局，已重置为初始盘面（红先行）");
                    Thread.Sleep(pollMs);
                    continue;
                }

                // ── 预期盘面快速通道 ──
                // 引擎上一轮给出的最佳着法若与当前识别完全一致，直接确认该步。
                // 高亮光晕/动画导致的字模误识被彻底绕开——盘面就是引擎预言的那个。
                if (committedBoard != null)
                {
                    BoardState? expBoard;
                    ChessMove? expMove;
                    PlayerSide expMover;
                    bool expValid;
                    lock (_expectedLock)
                    {
                        expBoard = _expectedBoard;
                        expMove = _expectedMove;
                        expMover = _expectedMover;
                        expValid = _expectedBoard != null && (DateTime.Now - _expectedAt).TotalSeconds < 90;
                    }

                    bool expectedRawMatches = expBoard != null && SameOccupancyAndColor(rawBoard, expBoard);
                    if (expValid && expBoard != null && !expectedRawMatches)
                        ClearExpectedBoard();
                    if (expValid && expBoard != null && similarCount >= 2 && expectedRawMatches &&
                        readResult.Board.CountDifferences(expBoard) == 0)
                    {
                        var nextPlayer = expMover == PlayerSide.Red ? PlayerSide.Black : PlayerSide.Red;
                        var confirmedBoard = expBoard.Clone();
                        confirmedBoard.CurrentPlayer = nextPlayer;
                        _settings.CurrentPlayer = nextPlayer == PlayerSide.Red ? "Red" : "Black";
                        CurrentPlayerChanged?.Invoke(nextPlayer);

                        var confirmedResult = new BoardReadResult
                        {
                            Board = confirmedBoard,
                            PreviousBoard = committedBoard,
                            LastMove = expMove,
                            HighlightedCells = readResult.HighlightedCells.ToList(),
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        };

                        committedBoard = confirmedBoard.Clone();
                        _trackedBoard = committedBoard.Clone();
                        seenSeq = Interlocked.Increment(ref _commitSeq);
                        _analysisManager.SubmitBoard(confirmedResult);
                        recentFrames.Clear();
                        bigDiffStreak = 0;
                        illegalStreak = 0;
                        ClearExpectedBoard();
                        PublishBoardRead(confirmedResult);
                        StatusChanged?.Invoke("命中引擎预期盘面，已直接确认该步");
                        Thread.Sleep(pollMs);
                        continue;
                    }
                }

                int diffFromCommitted = committedBoard == null
                    ? int.MaxValue
                    : readResult.Board.CountDifferences(committedBoard);

                int rawDiff = committedBoard == null
                    ? int.MaxValue
                    : CountOccupancyColorDifferences(rawBoard, committedBoard);

                bool hasHighlights = readResult.HighlightedCells.Count > 0;
                // 稳定性判定：静态画面要求最近 4 帧中 ≥2 帧相似；若落点带有游戏高亮（真值标记），则 1 帧即可确认，实现对面秒下零延迟
                bool frameStable = similarCount >= 2 || (hasHighlights && similarCount >= 1);

                // ── 连续两步追盘（我方走子后对方秒下，或识别循环跳过中间帧）：先于单步/高亮救援 ──
                int checkDiff = Math.Max(rawDiff, diffFromCommitted);
                if (committedBoard != null && (frameStable || checkDiff >= 2))
                {
                    if (checkDiff >= 2 && checkDiff <= 8 &&
                        TryCatchUpTwoMoves(committedBoard, rawBoard, readResult.Board,
                            readResult.HighlightedCells, out var caughtUpBoard, out var caughtUpMoves))
                    {
                        // 两步走完后行棋方保持为原行棋方（如我方走后对方秒回，仍轮到我方走）
                        caughtUpBoard.CurrentPlayer = committedBoard.CurrentPlayer;
                        _settings.CurrentPlayer = committedBoard.CurrentPlayer == PlayerSide.Red ? "Red" : "Black";
                        CurrentPlayerChanged?.Invoke(committedBoard.CurrentPlayer);
                        var caughtUpResult = new BoardReadResult
                        {
                            Board = caughtUpBoard,
                            PreviousBoard = committedBoard,
                            LastMove = caughtUpMoves[^1],
                            HighlightedCells = readResult.HighlightedCells.ToList(),
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        };

                        committedBoard = caughtUpBoard.Clone();
                        _trackedBoard = committedBoard.Clone();
                        seenSeq = Interlocked.Increment(ref _commitSeq);
                        _analysisManager.SubmitBoard(caughtUpResult);
                        recentFrames.Clear();
                        bigDiffStreak = 0;
                        illegalStreak = 0;
                        ClearExpectedBoard();
                        PublishBoardRead(caughtUpResult);
                        StatusChanged?.Invoke("检测到连续两步走子（对面快速落子），已追上当前盘面");
                        Thread.Sleep(pollMs);
                        continue;
                    }
                }

                // ── 高亮救援：游戏光环 = 上一步落点真值 ──
                // 对手走子后光环罩住落点棋子直到下一步，光环会把墨迹"洗白"，
                // 原始识别把落点读成空格 → 追踪层把棋子原位复活 → diff=0，
                // 表现为"永远等待对手"（chess-helper-app 用白点标记走同类通道）。
                // 光环在哪棋子就在哪：用"某子合法走到光环格"重建盘面确认提交。
                if (committedBoard != null && hasHighlights && frameStable)
                {
                    if (rawDiff >= 1 && rawDiff <= 6 &&
                        TryHighlightRescue(committedBoard, rawBoard, readResult.HighlightedCells,
                            out var rescued, out var rescueMove))
                    {
                        var movedColor = committedBoard[rescueMove.From.Col, rescueMove.From.Row].Color;
                        var nextPlayer = movedColor == PieceColor.Red ? PlayerSide.Black : PlayerSide.Red;
                        rescued.CurrentPlayer = nextPlayer;
                        _settings.CurrentPlayer = nextPlayer == PlayerSide.Red ? "Red" : "Black";
                        CurrentPlayerChanged?.Invoke(nextPlayer);

                        var rescueResult = new BoardReadResult
                        {
                            Board = rescued,
                            PreviousBoard = committedBoard,
                            LastMove = rescueMove,
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        };

                        committedBoard = rescued.Clone();
                        _trackedBoard = committedBoard.Clone();
                        seenSeq = Interlocked.Increment(ref _commitSeq);
                        _analysisManager.SubmitBoard(rescueResult);
                        recentFrames.Clear();
                        bigDiffStreak = 0;
                        illegalStreak = 0;
                        ClearExpectedBoard();
                        PublishBoardRead(rescueResult);
                        StatusChanged?.Invoke("已按游戏高亮标记确认走子（光环下棋子不可见）");
                        Thread.Sleep(pollMs);
                        continue;
                    }
                }

                if (committedBoard == null)
                {
                    // 首盘直接立即提交分析，无需等待匹配
                    readResult.Board.CurrentPlayer = _settings.CurrentPlayer == "Black" ? PlayerSide.Black : PlayerSide.Red;
                    committedBoard = readResult.Board.Clone();
                    _trackedBoard = committedBoard.Clone();
                    seenSeq = Interlocked.Increment(ref _commitSeq);
                    _analysisManager.SubmitBoard(readResult);
                    recentFrames.Clear();
                    bigDiffStreak = 0;
                    illegalStreak = 0;
                    PublishBoardRead(readResult);
                }
                else if (diffFromCommitted == 0)
                {
                    // 与基准一致：无需动作
                    bigDiffStreak = 0;
                    illegalStreak = 0;
                }
                else if (diffFromCommitted >= 2 && diffFromCommitted <= 6 && frameStable)
                {
                    // 走子判定：最近帧稳定，且与基准差异在 [2,6]，可能是一步真实走子或两步快棋
                    // 先检查是否红黑双方均有棋子离开（如果是，必定是连续两步，不能单步提交颠倒行棋方！）
                    bool redLeft = false, blackLeft = false;
                    for (int c = 0; c < BoardState.Cols; c++)
                    {
                        for (int r = 0; r < BoardState.Rows; r++)
                        {
                            var oldC = committedBoard[c, r];
                            var nowC = readResult.Board[c, r];
                            if (!oldC.IsEmpty && nowC.IsEmpty)
                            {
                                if (oldC.Color == PieceColor.Red) redLeft = true;
                                else if (oldC.Color == PieceColor.Black) blackLeft = true;
                            }
                        }
                    }

                    if (redLeft && blackLeft &&
                        TryCatchUpTwoMoves(committedBoard, rawBoard, readResult.Board,
                            readResult.HighlightedCells, out var twoMoveBoard, out var twoMoves))
                    {
                        twoMoveBoard.CurrentPlayer = committedBoard.CurrentPlayer;
                        _settings.CurrentPlayer = committedBoard.CurrentPlayer == PlayerSide.Red ? "Red" : "Black";
                        CurrentPlayerChanged?.Invoke(committedBoard.CurrentPlayer);
                        var twoMoveResult = new BoardReadResult
                        {
                            Board = twoMoveBoard,
                            PreviousBoard = committedBoard,
                            LastMove = twoMoves[^1],
                            HighlightedCells = readResult.HighlightedCells.ToList(),
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        };

                        committedBoard = twoMoveBoard.Clone();
                        _trackedBoard = committedBoard.Clone();
                        seenSeq = Interlocked.Increment(ref _commitSeq);
                        _analysisManager.SubmitBoard(twoMoveResult);
                        recentFrames.Clear();
                        bigDiffStreak = 0;
                        illegalStreak = 0;
                        ClearExpectedBoard();
                        PublishBoardRead(twoMoveResult);
                        StatusChanged?.Invoke("检测到连续两步走子（对面快速落子），已追上当前盘面");
                        Thread.Sleep(pollMs);
                        continue;
                    }

                    bigDiffStreak = 0;

                    // 推断谁走了这步棋：合法走法优先，落点带游戏高亮标记的更优先
                    var move = readResult.InferLastMove(committedBoard, readResult.HighlightedCells);
                    bool legal = false;
                    PieceColor movedColor = PieceColor.Unknown;
                    if (move != null)
                    {
                        movedColor = committedBoard[move.Value.From].Color;
                        legal = movedColor != PieceColor.Unknown &&
                                XiangqiMoveRules.IsLegalMove(committedBoard, move.Value.From, move.Value.To, movedColor);
                    }

                    if (legal && !(redLeft && blackLeft))
                    {
                        // 走法合法：从上一基准重建盘面，禁止无关格子的视觉类型噪声进入基准
                        if (!ConfirmedBoardUpdater.TryBuildMoveCommit(
                                committedBoard, rawBoard, new[] { move!.Value },
                                readResult.HighlightedCells, out var confirmedBoard) || confirmedBoard == null)
                        {
                            illegalStreak++;
                            StatusChanged?.Invoke("走子的原始占用证据不足，已保留基准盘面");
                            Thread.Sleep(pollMs);
                            continue;
                        }

                        illegalStreak = 0;
                        var nextPlayer = movedColor == PieceColor.Red ? PlayerSide.Black : PlayerSide.Red;
                        confirmedBoard.CurrentPlayer = nextPlayer;
                        _settings.CurrentPlayer = nextPlayer == PlayerSide.Red ? "Red" : "Black";
                        CurrentPlayerChanged?.Invoke(nextPlayer);

                        var confirmedResult = new BoardReadResult
                        {
                            Board = confirmedBoard.Clone(),
                            PreviousBoard = committedBoard,
                            LastMove = move,
                            HighlightedCells = readResult.HighlightedCells.ToList(),
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        };

                        committedBoard = confirmedBoard.Clone();
                        _trackedBoard = committedBoard.Clone();
                        seenSeq = Interlocked.Increment(ref _commitSeq);
                        _analysisManager.SubmitBoard(confirmedResult);
                        recentFrames.Clear();
                        ClearExpectedBoard();
                        PublishBoardRead(confirmedResult);
                    }
                    else
                    {
                        // 差异无法用任何一步合法走法解释（高亮光晕/半格漂移等识别噪声）：
                        // 不提交、不翻转行棋方、不刷新显示 —— 翻转错一次，"仅我方回合推荐"
                        // 就再也不给提示；显示噪声则会表现为棋子乱跳。
                        illegalStreak++;
                        if (illegalStreak >= IllegalReanchorFrames)
                        {
                            // 持续异常不能证明任何棋子发生了变化，更不能改变其兵种。
                            illegalStreak = 0;
                            StatusChanged?.Invoke("盘面持续异常且无法由合法走法解释，已保留基准；请重新校准或手动同步");
                        }
                    }
                }
                else if (diffFromCommitted > 6)
                {
                    // 大幅变化（连续多步未捕获 / 识别错乱 / 悔棋等）：
                    // 等盘面高度一致（4 帧中 ≥3 帧相同）持续 2 轮后提示手动同步；
                    // 稳定但无法由合法走法解释的候选绝不转正。
                    if (similarCount >= 3)
                        bigDiffStreak++;
                    else
                        bigDiffStreak = 0;

                    if (bigDiffStreak >= 2)
                    {
                        // 稳定并不等于正确；没有合法走法序列时绝不接受整张候选盘面。
                        bigDiffStreak = 0;
                        StatusChanged?.Invoke("盘面大幅变化且无法由合法走法唯一解释，已保留基准；请手动同步");
                    }
                }
                // 其余情况（diff=1 噪点 / [2,6] 但尚未凑齐 2 帧一致或无合法解释 / >6 但尚不稳定）：继续观察

                // 按分析间隔等待
                Thread.Sleep(pollMs);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"工作循环异常: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>
    /// 手动设置当前行棋方
    /// </summary>
    public void SetCurrentPlayer(PlayerSide player)
    {
        ClearExpectedBoard();
        _settings.CurrentPlayer = player == PlayerSide.Red ? "Red" : "Black";
        if (_trackedBoard != null)
        {
            _trackedBoard.CurrentPlayer = player;
            var res = new BoardReadResult
            {
                Board = _trackedBoard.Clone(),
                Source = BoardReadMode.VisionOnly,
                Timestamp = DateTime.Now
            };
            _analysisManager.SubmitBoard(res);
        }
    }

    /// <summary>
    /// 手动设置我方执子颜色
    /// </summary>
    public void SetOwnColor(string ownColor)
    {
        _settings.OwnColor = ownColor;
        _overlay.OwnColor = ownColor;
    }

    /// <summary>
    /// 设置是否仅在我方回合推荐走法
    /// </summary>
    public void SetOnlyRecommendOwnMoves(bool enabled)
    {
        _settings.OnlyRecommendOwnMoves = enabled;
        _overlay.OnlyRecommendOwnMoves = enabled;
    }

    /// <summary>
    /// 立即请求一次分析（强制刷新当前窗口盘面并提交引擎分析）。
    /// 手动刷新同样不能把单帧噪声直接上屏：隔 300ms 读两帧躲开走子动画，
    /// 与基准的差异优先按"一步合法走子"提交并翻转行棋方，
    /// 解释不了的才手动重锚（行棋方保持不变）。
    /// </summary>
    public void RequestAnalysis()
    {
        if (_targetWindow == null || _calibration == null || !_calibration.IsValid)
        {
            _analysisManager.AnalyzeNow();
            return;
        }

        try
        {
            // A manual refresh is authoritative for the current frame; an
            // older engine prediction must not survive into this decision.
            ClearExpectedBoard();
            var shot1 = _capturer.CaptureWindow(_targetWindow.Handle);
            if (shot1.Empty())
            {
                _analysisManager.AnalyzeNow();
                return;
            }
            var read1 = _visionReader.ReadBoard(shot1, _calibration, _trackedBoard);
            var raw1 = _visionReader.LastRawBoard.Clone();
            Thread.Sleep(300); // 躲开走子动画/高亮闪烁
            var shot2 = _capturer.CaptureWindow(_targetWindow.Handle);
            if (shot2.Empty())
            {
                _analysisManager.AnalyzeNow();
                return;
            }
            var read2 = _visionReader.ReadBoard(shot2, _calibration, _trackedBoard);
            BoardReadResult chosen = read2; // 两帧均取最新，双读只为躲开动画瞬间的噪声帧
            var rawBoard = _visionReader.LastRawBoard.Clone();
            bool rawStable = CountOccupancyColorDifferences(raw1, rawBoard) <= 2;
            if (!chosen.Board.GetAllPieces().Any())
            {
                _analysisManager.AnalyzeNow();
                return;
            }

            bool rescuedHighlightedMove = false;
            bool caughtUpTwoMoves = false;
            if (_trackedBoard != null && rawStable)
            {
                int rawDiff = CountOccupancyColorDifferences(rawBoard, _trackedBoard);
                int trackedDiff = chosen.Board.CountDifferences(_trackedBoard);
                int checkDiff = Math.Max(rawDiff, trackedDiff);
                if (checkDiff >= 2 && checkDiff <= 8 &&
                    TryCatchUpTwoMoves(_trackedBoard, rawBoard, chosen.Board,
                        chosen.HighlightedCells, out var caughtUpBoard, out var caughtUpMoves))
                {
                    caughtUpBoard.CurrentPlayer = _trackedBoard.CurrentPlayer;
                    chosen = new BoardReadResult
                    {
                        Board = caughtUpBoard,
                        PreviousBoard = _trackedBoard,
                        LastMove = caughtUpMoves[^1],
                        HighlightedCells = chosen.HighlightedCells.ToList(),
                        Source = BoardReadMode.VisionOnly,
                        Timestamp = DateTime.Now
                    };
                    caughtUpTwoMoves = true;
                    StatusChanged?.Invoke("检测到连续两步走子（对面快速落子），已追上当前盘面");
                }
            }

            if (!caughtUpTwoMoves && _trackedBoard != null && chosen.HighlightedCells.Count > 0)
            {
                int rawDiff = CountOccupancyColorDifferences(rawBoard, _trackedBoard);
                if (rawDiff >= 1 && rawDiff <= 6 &&
                    TryHighlightRescue(_trackedBoard, rawBoard, chosen.HighlightedCells,
                        out var rescued, out var rescueMove))
                {
                    var movedColor = _trackedBoard[rescueMove.From].Color;
                    if (movedColor != PieceColor.Unknown)
                    {
                        var nextPlayer = movedColor == PieceColor.Red ? PlayerSide.Black : PlayerSide.Red;
                        rescued.CurrentPlayer = nextPlayer;
                        chosen = new BoardReadResult
                        {
                            Board = rescued,
                            PreviousBoard = _trackedBoard,
                            LastMove = rescueMove,
                            HighlightedCells = chosen.HighlightedCells.ToList(),
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        };
                        rescuedHighlightedMove = true;
                        _settings.CurrentPlayer = nextPlayer == PlayerSide.Red ? "Red" : "Black";
                        CurrentPlayerChanged?.Invoke(nextPlayer);
                        StatusChanged?.Invoke("已按游戏高亮标记确认走子（光环下棋子不可见）");
                    }
                }
            }

            int diff = _trackedBoard == null ? int.MaxValue : chosen.Board.CountDifferences(_trackedBoard);

            if (!caughtUpTwoMoves && !rescuedHighlightedMove && _trackedBoard != null && diff >= 2 && diff <= 6)
            {
                // 有变化：优先按"一步合法走子"提交（与工作循环同一套门控）
                var move = chosen.InferLastMove(_trackedBoard, chosen.HighlightedCells);
                bool legal = false;
                PieceColor movedColor = PieceColor.Unknown;
                if (move != null)
                {
                    movedColor = _trackedBoard[move.Value.From].Color;
                    legal = movedColor != PieceColor.Unknown &&
                            XiangqiMoveRules.IsLegalMove(_trackedBoard, move.Value.From, move.Value.To, movedColor);
                }

                if (legal)
                {
                    if (!ConfirmedBoardUpdater.TryBuildMoveCommit(
                            _trackedBoard, rawBoard, new[] { move!.Value },
                            chosen.HighlightedCells, out var confirmedBoard) || confirmedBoard == null)
                    {
                        _analysisManager.SubmitBoard(new BoardReadResult
                        {
                            Board = _trackedBoard.Clone(),
                            Source = BoardReadMode.VisionOnly,
                            Timestamp = DateTime.Now
                        });
                        StatusChanged?.Invoke("走子的原始占用证据不足，已保留原盘面");
                        return;
                    }

                    var nextPlayer = movedColor == PieceColor.Red ? PlayerSide.Black : PlayerSide.Red;
                    confirmedBoard.CurrentPlayer = nextPlayer;
                    _settings.CurrentPlayer = nextPlayer == PlayerSide.Red ? "Red" : "Black";
                    CurrentPlayerChanged?.Invoke(nextPlayer);
                    chosen = new BoardReadResult
                    {
                        Board = confirmedBoard.Clone(),
                        PreviousBoard = _trackedBoard,
                        LastMove = move,
                        HighlightedCells = chosen.HighlightedCells.ToList(),
                        Source = BoardReadMode.VisionOnly,
                        Timestamp = DateTime.Now
                    };
                }
                else
                {
                    // 解释不了的多格变化可能是分类错乱；不要把候选盘面
                    // 写进基准，否则一次点击会把错误扩散到整局。
                    _analysisManager.SubmitBoard(new BoardReadResult
                    {
                        Board = _trackedBoard.Clone(),
                        Source = BoardReadMode.VisionOnly,
                        Timestamp = DateTime.Now
                    });
                    StatusChanged?.Invoke("当前变化无法由合法走法解释，已保留原盘面");
                    return;
                }
            }
            else if (!caughtUpTwoMoves && !rescuedHighlightedMove && _trackedBoard != null && diff == 0)
            {
                // 盘面没变：只需重新分析
                _analysisManager.SubmitBoard(new BoardReadResult
                {
                    Board = _trackedBoard.Clone(),
                    Source = BoardReadMode.VisionOnly,
                    Timestamp = DateTime.Now
                });
                return;
            }
            else if (!caughtUpTwoMoves && !rescuedHighlightedMove && _trackedBoard != null && diff == 1)
            {
                // 单格类型变化通常是模板抖动（例如马被暂时识别成兵），
                // 不允许手动按钮把它写进已确认基准。
                _analysisManager.SubmitBoard(new BoardReadResult
                {
                    Board = _trackedBoard.Clone(),
                    Source = BoardReadMode.VisionOnly,
                    Timestamp = DateTime.Now
                });
                StatusChanged?.Invoke("检测到单格识别抖动，已保留原盘面");
                return;
            }
            else if (rescuedHighlightedMove || caughtUpTwoMoves)
            {
                // 高亮救援已确认走子并设置行棋方，直接提交新基准。
            }
            else
            {
                // 只有首次同步可以直接建立盘面；已有基准的大幅变化必须由合法走法解释。
                if (_trackedBoard != null)
                {
                    _analysisManager.SubmitBoard(new BoardReadResult
                    {
                        Board = _trackedBoard.Clone(),
                        Source = BoardReadMode.VisionOnly,
                        Timestamp = DateTime.Now
                    });
                    StatusChanged?.Invoke("当前变化无法由合法走法解释，已保留原盘面；请检查校准");
                    return;
                }

                chosen.Board.CurrentPlayer = _settings.CurrentPlayer == "Black"
                    ? PlayerSide.Black : PlayerSide.Red;
                StatusChanged?.Invoke("已建立初始盘面");
            }

            _trackedBoard = chosen.Board.Clone();
            ClearExpectedBoard();
            Interlocked.Increment(ref _commitSeq); // 让工作循环采纳新基准
            PublishBoardRead(chosen);
            _analysisManager.SubmitBoard(chosen);
        }
        catch
        {
            _analysisManager.AnalyzeNow();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _running = false;
            if (_workerThread != null && _workerThread.IsAlive)
                _workerThread.Join(2000);

            _visionReader.Dispose();
            _overlay.Dispose();
            _capturer.Dispose();
            _analysisManager.Dispose();
            _disposed = true;
        }
    }
}
