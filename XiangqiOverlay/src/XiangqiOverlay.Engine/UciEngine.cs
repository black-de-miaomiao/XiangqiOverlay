using System.Diagnostics;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Engine;

/// <summary>
/// Pikafish / UCI 协议通信引擎
/// 通过 UCI 协议与 Pikafish 进程通信，发送局面并获取分析结果
/// </summary>
public sealed class UciEngine : IUciEngine
{
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly string _exePath;
    private readonly string _evalFile;
    private readonly int _threads;
    private readonly int _hashSize;
    private bool _disposed;
    private bool _ready;
    private readonly ManualResetEventSlim _uciOkEvent = new(false);
    private readonly ManualResetEventSlim _readyOkEvent = new(false);
    private string _engineName = "Pikafish";
    private readonly EngineSearchScheduler _searchScheduler = new();
    private readonly object _searchLock = new();
    private readonly UciOutputParser _outputParser = new();

    /// <summary>
    /// 当收到分析输出时触发
    /// </summary>
    public event Action<AnalysisResult>? AnalysisReceived;

    /// <summary>
    /// 当引擎状态变化时触发
    /// </summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 当引擎输出错误时触发
    /// </summary>
    public event Action<string>? ErrorOccurred;

    public bool IsRunning => _process != null && !_process.HasExited;
    public bool IsReady => _ready;
    public string EngineName => _engineName;

    public UciEngine(string exePath, string evalFile, int threads = 4, int hashSize = 128)
    {
        _exePath = string.IsNullOrWhiteSpace(exePath) ? "" :
            (Path.IsPathRooted(exePath) ? exePath : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, exePath)));
        _evalFile = string.IsNullOrWhiteSpace(evalFile) ? "" :
            (Path.IsPathRooted(evalFile) ? evalFile : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, evalFile)));
        _threads = threads;
        _hashSize = hashSize;
    }

    /// <summary>
    /// 启动引擎进程
    /// </summary>
    public bool Start()
    {
        if (!File.Exists(_exePath))
        {
            ErrorOccurred?.Invoke($"引擎可执行文件不存在: {_exePath}");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _exePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_exePath) ?? ""
            };

            _process = new Process { StartInfo = startInfo };
            _process.Start();

            _stdin = _process.StandardInput;

            // 异步读取输出
            _process.OutputDataReceived += OnOutputReceived;
            _process.ErrorDataReceived += OnErrorReceived;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            StatusChanged?.Invoke("正在初始化引擎...");

            lock (_searchLock)
                _searchScheduler.Reset();

            // 发送 UCI 握手
            _uciOkEvent.Reset();
            SendCommand("uci");

            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"启动引擎失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 初始化引擎选项
    /// </summary>
    public bool Initialize()
    {
        if (!IsRunning)
        {
            if (!Start())
                return false;
        }

        // 等待 uciok
        if (!_uciOkEvent.Wait(5000))
        {
            ErrorOccurred?.Invoke("引擎初始化超时 (等待 uciok)");
            return false;
        }

        // 设置引擎选项
        if (!string.IsNullOrEmpty(_evalFile) && File.Exists(_evalFile))
            SendCommand($"setoption name EvalFile value {_evalFile}");

        SendCommand($"setoption name Threads value {_threads}");
        SendCommand($"setoption name Hash value {_hashSize}");

        // 等待 readyok
        _readyOkEvent.Reset();
        SendCommand("isready");

        if (!_readyOkEvent.Wait(5000))
        {
            ErrorOccurred?.Invoke("引擎初始化超时 (等待 readyok)");
            return false;
        }

        _ready = true;
        StatusChanged?.Invoke($"引擎就绪: {_engineName}");
        return true;
    }

    /// <summary>
    /// 分析指定 FEN 局面
    /// </summary>
    public void Analyze(string fen, int maxDepth = 0, long maxNodes = 0, long maxTimeMs = 0)
    {
        if (!FenValidator.Validate(fen, out var error))
        {
            ErrorOccurred?.Invoke($"盘面暂不稳定 ({error})，等待稳定后再分析");
            return;
        }

        if (!IsRunning)
        {
            StatusChanged?.Invoke("检测到引擎离线，正在自动拉起重启...");
            if (!Initialize())
            {
                ErrorOccurred?.Invoke("引擎未运行且自动重启失败");
                return;
            }
        }

        lock (_searchLock)
        {
            _searchScheduler.Submit(fen, maxDepth, maxNodes, maxTimeMs, out bool startImmediately);
            if (startImmediately)
            {
                StartSearch(_searchScheduler.Active!);
            }
            else
            {
                SendCommand("stop");
            }
        }
    }

    /// <summary>
    /// 停止分析
    /// </summary>
    public void Stop()
    {
        lock (_searchLock)
        {
            _searchScheduler.CancelPending();
            if (IsRunning && _searchScheduler.Active != null)
                SendCommand("stop");
        }
    }

    private void StartSearch(EngineSearchRequest request)
    {
        SendCommand($"position fen {request.Fen}");
        SendCommand(request.BuildGoCommand());
    }

    /// <summary>
    /// 发送命令到引擎
    /// </summary>
    public void SendCommand(string command)
    {
        if (_stdin == null)
            return;

        try
        {
            _stdin.WriteLine(command);
            _stdin.Flush();
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"发送命令失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理引擎输出
    /// </summary>
    private void OnOutputReceived(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;

        var line = e.Data.Trim();

        // 解析 UCI 输出
        if (line == "uciok")
        {
            _ready = true;
            _uciOkEvent.Set();
        }
        else if (line == "readyok")
        {
            _ready = true;
            _readyOkEvent.Set();
        }
        else if (line.StartsWith("id name"))
        {
            _engineName = line.Substring("id name".Length).Trim();
        }
        else if (line.StartsWith("bestmove"))
        {
            AnalysisResult? result;
            lock (_searchLock)
            {
                var active = _searchScheduler.Active;
                if (active == null)
                    return;

                result = _outputParser.ParseBestMove(line, _engineName);
                if (result != null)
                {
                    result.Fen = active.Fen;
                    result.SearchId = active.Id;
                }

                var next = _searchScheduler.CompleteActive();
                if (next != null)
                    StartSearch(next);
            }
            if (result != null)
                AnalysisReceived?.Invoke(result);
        }
        else if (line.StartsWith("info"))
        {
            AnalysisResult? result;
            lock (_searchLock)
            {
                var active = _searchScheduler.Active;
                if (active == null)
                    return;

                result = _outputParser.ParseInfoLine(line, _engineName);
                if (result != null)
                {
                    result.Fen = active.Fen;
                    result.SearchId = active.Id;
                }
            }
            if (result != null)
                AnalysisReceived?.Invoke(result);
        }
    }

    private void OnErrorReceived(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
            ErrorOccurred?.Invoke(e.Data);
    }

    /// <summary>
    /// 设置 MultiPV（多线路分析）
    /// </summary>
    public void SetMultiPv(int count)
    {
        SendCommand($"setoption name MultiPV value {count}");
    }

    /// <summary>
    /// 新游戏
    /// </summary>
    public void NewGame()
    {
        SendCommand("ucinewgame");
        SendCommand("isready");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                if (IsRunning)
                {
                    SendCommand("quit");
                    if (_process != null && !_process.WaitForExit(3000))
                        _process.Kill();
                }

                _process?.Dispose();
            }
            catch { }

            _disposed = true;
        }
    }
}
