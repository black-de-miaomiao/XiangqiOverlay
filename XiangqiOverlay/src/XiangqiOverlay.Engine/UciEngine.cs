using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Engine;

/// <summary>
/// Pikafish / UCI 协议通信引擎
/// 通过 UCI 协议与 Pikafish 进程通信，发送局面并获取分析结果
/// </summary>
public sealed class UciEngine : IDisposable
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
        // 1. 严格校验 FEN 合法性，防止非法局面导致 Pikafish 进程崩溃退出
        if (!FenValidator.Validate(fen, out var error))
        {
            ErrorOccurred?.Invoke($"盘面暂不稳定 ({error})，等待稳定后再分析");
            return;
        }

        // 2. 如果引擎进程异常退出，自动重新唤醒拉起
        if (!IsRunning)
        {
            StatusChanged?.Invoke("检测到引擎离线，正在自动拉起重启...");
            if (!Initialize())
            {
                ErrorOccurred?.Invoke("引擎未运行且自动重启失败");
                return;
            }
        }

        // 先停止之前的分析
        SendCommand("stop");

        // 发送局面
        SendCommand($"position fen {fen}");

        // 构建分析命令
        var goBuilder = new StringBuilder("go");
        if (maxDepth > 0)
            goBuilder.Append($" depth {maxDepth}");
        if (maxNodes > 0)
            goBuilder.Append($" nodes {maxNodes}");
        if (maxTimeMs > 0)
            goBuilder.Append($" movetime {maxTimeMs}");
        if (maxDepth == 0 && maxNodes == 0 && maxTimeMs == 0)
            goBuilder.Append(" depth 24"); // 默认深度

        SendCommand(goBuilder.ToString());
    }

    /// <summary>
    /// 停止分析
    /// </summary>
    public void Stop()
    {
        if (IsRunning)
            SendCommand("stop");
    }

    /// <summary>
    /// 开始持续分析（类似 go infinite + 定期读取）
    /// </summary>
    public void AnalyzeInfinite(string fen)
    {
        if (!IsRunning)
            return;

        SendCommand("stop");
        Thread.Sleep(50);
        SendCommand($"position fen {fen}");
        SendCommand("go infinite");
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
            // 引擎给出了最终着法
            var result = ParseBestMove(line);
            if (result != null)
                AnalysisReceived?.Invoke(result);
        }
        else if (line.StartsWith("info"))
        {
            // 实时分析信息
            var result = ParseInfoLine(line);
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
    /// 解析 "info" 行
    /// 格式：info depth D seldepth S score cp X nodes N nps P time T pv M1 M2 ...
    /// 或：info depth D currmove M currmovenumber N
    /// 或：info string ...
    /// </summary>
    private AnalysisResult? ParseInfoLine(string line)
    {
        try
        {
            var result = new AnalysisResult { EngineName = _engineName };

            // 提取 depth
            var depthMatch = Regex.Match(line, @"\bdepth (\d+)\b");
            if (depthMatch.Success)
                result.Depth = int.Parse(depthMatch.Groups[1].Value);

            // 提取 nodes
            var nodesMatch = Regex.Match(line, @"\bnodes (\d+)\b");
            if (nodesMatch.Success)
                result.TotalNodes = long.Parse(nodesMatch.Groups[1].Value);

            // 提取 time
            var timeMatch = Regex.Match(line, @"\btime (\d+)\b");
            if (timeMatch.Success)
                result.TimeMs = long.Parse(timeMatch.Groups[1].Value);

            // 提取 score
            // score cp X 或 score mate Y 或 score lowerbound X 或 score upperbound X
            var scoreMatch = Regex.Match(line, @"\bscore (cp|mate|lowerbound|upperbound) (-?\d+)\b");
            if (scoreMatch.Success)
            {
                string scoreType = scoreMatch.Groups[1].Value;
                int scoreVal = int.Parse(scoreMatch.Groups[2].Value);

                if (scoreType == "cp")
                    result.Score = scoreVal;
                else if (scoreType == "mate")
                    result.Score = scoreVal > 0 ? 100000 - scoreVal : -100000 - scoreVal;

                // 将厘 UC-Pawn 分数转换为胜率（简化公式）
                // Pikafish 的分数范围大约 -3000 ~ 3000
                // 胜率 = 1 / (1 + exp(-score / 600))
                result.WinRate = 1.0 / (1.0 + Math.Exp(-result.Score / 600.0));
            }

            // 提取 pv（主要变着）
            var pvMatch = Regex.Match(line, @"\bpv\s+(.+)$");
            if (pvMatch.Success)
            {
                var pvStr = pvMatch.Groups[1].Value.Trim();
                var moves = ParsePvMoves(pvStr);

                if (moves.Count > 0)
                {
                    var bestMove = new AnalysisMove
                    {
                        Move = moves[0].ToUcci(),
                        WinRate = result.WinRate,
                        Score = result.Score,
                        Depth = result.Depth,
                        Nodes = result.TotalNodes,
                        PvString = pvStr
                    };
                    result.Moves.Add(bestMove);

                    // 如果有多手，也添加为候选
                    // 注意：UCCI 的 pv 通常只给出一个主要变着序列
                    // 多 PV 需要设置 MultiPV 选项
                }
            }

            // 提取 currmove（当前正在搜索的着法）
            if (result.Moves.Count == 0)
            {
                var currMoveMatch = Regex.Match(line, @"\bcurrmove (\S+)\b");
                if (currMoveMatch.Success)
                {
                    result.Moves.Add(new AnalysisMove
                    {
                        Move = currMoveMatch.Groups[1].Value,
                        WinRate = result.WinRate,
                        Score = result.Score,
                        Depth = result.Depth,
                        Nodes = result.TotalNodes
                    });
                }
            }

            _lastScore = result.Score;
            _lastWinRate = result.WinRate;
            _lastDepth = result.Depth;

            result.Fen = "current"; // 实际由调用方填充
            return result.Moves.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private int _lastScore;
    private double _lastWinRate;
    private int _lastDepth;

    /// <summary>
    /// 解析 bestmove 行
    /// 格式：bestmove h2e2 ponder e2e6
    /// </summary>
    private AnalysisResult? ParseBestMove(string line)
    {
        try
        {
            var match = Regex.Match(line, @"bestmove (\S+)(?:\s+ponder\s+(\S+))?");
            if (!match.Success)
                return null;

            var bestMoveStr = match.Groups[1].Value;
            var ponderStr = match.Groups[2].Success ? match.Groups[2].Value : null;

            var result = new AnalysisResult
            {
                EngineName = _engineName,
                Score = _lastScore,
                WinRate = _lastWinRate,
                Depth = _lastDepth,
                Timestamp = DateTime.Now
            };

            result.Moves.Add(new AnalysisMove
            {
                Move = bestMoveStr,
                Score = _lastScore,
                WinRate = _lastWinRate,
                Depth = _lastDepth,
                PvString = ponderStr ?? string.Empty
            });

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 PV 字符串中的走法列表
    /// </summary>
    private List<ChessMove> ParsePvMoves(string pvStr)
    {
        var moves = new List<ChessMove>();
        var tokens = pvStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (token.Length < 4)
                continue;

            try
            {
                moves.Add(ChessMove.FromUcci(token));
            }
            catch
            {
                break;
            }
        }

        return moves;
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
