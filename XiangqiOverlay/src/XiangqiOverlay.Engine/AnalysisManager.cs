using XiangqiOverlay.Core;

namespace XiangqiOverlay.Engine;

/// <summary>
/// 分析管理器：盘面读取 → 引擎分析 → 结果输出 的协调中心
/// </summary>
public sealed class AnalysisManager : IDisposable
{
    private readonly IUciEngine _engine;
    private readonly AppSettings _settings;
    private bool _disposed;

    /// <summary>
    /// 当收到分析结果时触发
    /// </summary>
    public event Action<AnalysisResult>? AnalysisReceived;

    /// <summary>
    /// 当盘面状态变化时触发
    /// </summary>
    public event Action<BoardReadResult>? BoardUpdated;

    /// <summary>
    /// 当状态文字变化时触发
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 当发生错误时触发
    /// </summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// 上次分析的盘面 FEN（用于检测变化）
    /// </summary>
    private string _lastFen = string.Empty;

    /// <summary>
    /// 当前盘面状态
    /// </summary>
    private BoardState? _currentBoard;

    /// <summary>
    /// 串行化引擎指令提交（工作线程 SubmitBoard 与 UI 线程 AnalyzeNow 并发时防止指令交错）
    /// </summary>
    private readonly object _submitLock = new();

    public bool IsRunning => _engine.IsRunning;
    public IUciEngine Engine => _engine;

    public AnalysisManager(AppSettings settings)
        : this(settings, new UciEngine(
            settings.PikafishExecutable,
            settings.PikafishEvalFile,
            settings.Threads,
            settings.HashSize))
    {
    }

    public AnalysisManager(AppSettings settings, IUciEngine engine)
    {
        _settings = settings;
        _engine = engine;
        _engine.AnalysisReceived += OnEngineAnalysisReceived;
        _engine.ErrorOccurred += msg => ErrorOccurred?.Invoke(msg);
        _engine.StatusChanged += msg => StatusChanged?.Invoke(msg);
    }

    /// <summary>
    /// 初始化引擎
    /// </summary>
    public bool Initialize()
    {
        StatusChanged?.Invoke("正在启动 Pikafish 引擎...");
        bool ok = _engine.Initialize();
        if (ok)
        {
            StatusChanged?.Invoke($"Pikafish 引擎就绪");
        }
        return ok;
    }

    /// <summary>
    /// 提交一个盘面进行分析
    /// </summary>
    public void SubmitBoard(BoardReadResult readResult)
    {
        _currentBoard = readResult.Board;
        BoardUpdated?.Invoke(readResult);

        string fen = readResult.Board.ToFen();

        // 若 FEN 不合法，不记录为 _lastFen，避免非法局面锁死后续分析
        if (!FenValidator.Validate(fen, out var error))
        {
            _lastFen = string.Empty;
            StatusChanged?.Invoke($"盘面调整中 ({error})，等待落子稳定...");
            return;
        }

        lock (_submitLock)
        {
            // 如果盘面变化了，重新分析
            if (fen != _lastFen)
            {
                _lastFen = fen;
                StatusChanged?.Invoke($"盘面变化，正在分析...");
                _engine.Analyze(fen, _settings.MaxDepth, _settings.MaxNodes, _settings.MaxTimeMs);
            }
        }
    }

    /// <summary>
    /// 立即分析当前盘面
    /// </summary>
    public void AnalyzeNow()
    {
        if (_currentBoard == null)
            return;

        lock (_submitLock)
        {
            string fen = _currentBoard.ToFen();
            _lastFen = fen;
            _engine.Analyze(fen, _settings.MaxDepth, _settings.MaxNodes, _settings.MaxTimeMs);
        }
    }

    /// <summary>
    /// 停止分析
    /// </summary>
    public void StopAnalysis()
    {
        _engine.Stop();
        StatusChanged?.Invoke("已停止分析");
    }

    private void OnEngineAnalysisReceived(AnalysisResult result)
    {
        string? currentFen;
        lock (_submitLock)
            currentFen = _currentBoard?.ToFen();

        if (currentFen == null || !string.Equals(result.Fen, currentFen, StringComparison.Ordinal))
            return;

        AnalysisReceived?.Invoke(result);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAnalysis();
            _engine.Dispose();
            _disposed = true;
        }
    }
}
