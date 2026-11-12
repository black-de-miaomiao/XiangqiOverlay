# 快速走子回合恢复与思考稳定性 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 防止快速连续走子时旧 Pikafish 结果污染当前盘面和回合提示，并提供稳定、可配置的 4 秒/深度 30 默认搜索。

**Architecture:** 保留现有进程通信和主控制器。在 Engine 层增加纯 C# 搜索会话协调器与可测试的输出解析器，`AnalysisManager` 按真实 FEN 丢弃旧结果；UI 用统一的提示策略在盘面提交后立即刷新。真实截图只验证已观察到的黑卒走子，不把它误当作完整运行时日志。

**Tech Stack:** .NET 8、C#、WPF、Pikafish UCI/UCCI、现有 LogicTest 控制台断言程序、Git。

## Global Constraints

- 已确认棋子的兵种只能随合法走子移动；本计划不恢复自动重锚定或兵种降级。
- 每个分析结果必须保留发起搜索时的真实 FEN 和会话号，禁止用接收时的当前 FEN 覆盖。
- 旧 FEN 的结果不得进入 UI、悬浮层或预期盘面通道。
- `currmove` 不是最佳着，不能单独产生推荐。
- 默认 `MaxDepth = 30`、`MaxTimeMs = 4000`；节点限制为 0 时也必须应用毫秒时间限制。
- 不自动点击、不自动落子、不读取目标进程内存。
- 本机 `settings.json` 只作为用户配置更新，不提交到 Git。
- 所有提交信息末尾必须包含 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。

---

### Task 1: 用纯 C# 会话协调器固定搜索生命周期

**Files:**
- Create: `XiangqiOverlay/src/XiangqiOverlay.Engine/EngineSearchScheduler.cs`
- Create: `LogicTest/EngineSearchSchedulerTests.cs`
- Modify: `LogicTest/LogicTest.csproj`
- Modify: `LogicTest/Program.cs`

**Interfaces:**
- Produces: `EngineSearchRequest(Id, Fen, MaxDepth, MaxNodes, MaxTimeMs)`、`BuildGoCommand()`。
- Produces: `EngineSearchScheduler.Submit(...)`、`CompleteActive()`、`CancelPending()`、`Reset()`、`Active`、`Pending`。

- [ ] **Step 1: 添加 Engine 测试引用和失败测试**

在 `LogicTest/LogicTest.csproj` 的 `<ItemGroup>` 中保留 Vision 引用并添加：

```xml
<ProjectReference Include="..\XiangqiOverlay\src\XiangqiOverlay.Engine\XiangqiOverlay.Engine.csproj" />
```

Create `LogicTest/EngineSearchSchedulerTests.cs`:

```csharp
using XiangqiOverlay.Engine;

namespace LogicTest;

public static class EngineSearchSchedulerTests
{
    public static void Run(Action<string, bool> check)
    {
        var scheduler = new EngineSearchScheduler();
        var first = scheduler.Submit("fen-a", 30, 0, 4000, out bool startFirst);
        check("首个搜索请求立即成为活动搜索",
            startFirst && scheduler.Active == first && first.Id == 1 && first.Fen == "fen-a");
        check("搜索命令同时包含深度和毫秒时间",
            first.BuildGoCommand() == "go depth 30 movetime 4000");

        scheduler.Submit("fen-b", 30, 0, 4000, out bool startSecond);
        var latest = scheduler.Submit("fen-c", 30, 0, 4000, out bool startThird);
        check("活动搜索期间不直接启动新搜索",
            !startSecond && !startThird && scheduler.Active == first);
        check("连续请求只保留最新待搜索盘面",
            scheduler.Pending == latest && latest.Fen == "fen-c");

        var started = scheduler.CompleteActive();
        check("旧 bestmove 完成后启动最新待处理请求",
            started == latest && scheduler.Active == latest && scheduler.Pending == null);

        scheduler.Submit("fen-d", 30, 0, 4000, out _);
        scheduler.CancelPending();
        check("停止操作取消待处理请求但保留活动会话归属",
            scheduler.Pending == null && scheduler.Active == latest);
        check("没有待处理请求时完成活动搜索后进入空闲",
            scheduler.CompleteActive() == null && scheduler.Active == null);

        scheduler.Submit("fen-e", 30, 0, 4000, out _);
        scheduler.Reset();
        check("引擎重启清空旧会话",
            scheduler.Active == null && scheduler.Pending == null);
    }
}
```

在 `LogicTest/Program.cs` 的 `BoardIntegrityTests.Run(Check);` 前加入：

```csharp
LogicTest.EngineSearchSchedulerTests.Run(Check);
```

- [ ] **Step 2: 运行并确认因协调器不存在而失败**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 编译错误包含 `EngineSearchScheduler` 和 `EngineSearchRequest` 不存在。

- [ ] **Step 3: 实现最小协调器**

Create `XiangqiOverlay/src/XiangqiOverlay.Engine/EngineSearchScheduler.cs`:

```csharp
using System.Text;

namespace XiangqiOverlay.Engine;

public sealed record EngineSearchRequest(
    long Id,
    string Fen,
    int MaxDepth,
    long MaxNodes,
    long MaxTimeMs)
{
    public string BuildGoCommand()
    {
        var builder = new StringBuilder("go");
        if (MaxDepth > 0)
            builder.Append($" depth {MaxDepth}");
        if (MaxNodes > 0)
            builder.Append($" nodes {MaxNodes}");
        if (MaxTimeMs > 0)
            builder.Append($" movetime {MaxTimeMs}");
        if (MaxDepth == 0 && MaxNodes == 0 && MaxTimeMs == 0)
            builder.Append(" depth 24");
        return builder.ToString();
    }
}

public sealed class EngineSearchScheduler
{
    private readonly object _sync = new();
    private long _nextId;
    private EngineSearchRequest? _active;
    private EngineSearchRequest? _pending;

    public EngineSearchRequest? Active
    {
        get { lock (_sync) return _active; }
    }

    public EngineSearchRequest? Pending
    {
        get { lock (_sync) return _pending; }
    }

    public EngineSearchRequest Submit(
        string fen,
        int maxDepth,
        long maxNodes,
        long maxTimeMs,
        out bool startImmediately)
    {
        lock (_sync)
        {
            var request = new EngineSearchRequest(++_nextId, fen, maxDepth, maxNodes, maxTimeMs);
            if (_active == null)
            {
                _active = request;
                startImmediately = true;
            }
            else
            {
                _pending = request;
                startImmediately = false;
            }
            return request;
        }
    }

    public EngineSearchRequest? CompleteActive()
    {
        lock (_sync)
        {
            _active = null;
            if (_pending == null)
                return null;

            _active = _pending;
            _pending = null;
            return _active;
        }
    }

    public void CancelPending()
    {
        lock (_sync)
            _pending = null;
    }

    public void Reset()
    {
        lock (_sync)
        {
            _active = null;
            _pending = null;
            _nextId = 0;
        }
    }
}
```

- [ ] **Step 4: 运行测试并提交**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 新会话测试全部 `PASS`，最后一行为 `全部通过`。

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Engine/EngineSearchScheduler.cs LogicTest/EngineSearchSchedulerTests.cs LogicTest/LogicTest.csproj LogicTest/Program.cs
git commit -m "fix: coordinate engine search sessions" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 2: 将真实搜索 FEN 绑定到 Pikafish 输出

**Files:**
- Create: `XiangqiOverlay/src/XiangqiOverlay.Engine/UciOutputParser.cs`
- Create: `LogicTest/UciOutputParserTests.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Engine/UciEngine.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Core/AnalysisResult.cs`
- Modify: `LogicTest/Program.cs`

**Interfaces:**
- Consumes: Task 1 的 `EngineSearchScheduler`。
- Produces: `UciOutputParser.ParseInfoLine(string line, string engineName)` 和 `ParseBestMove(string line, string engineName)`。
- Produces: `AnalysisResult.SearchId`；UciEngine 结果为每个结果设置真实 `Fen` 和 `SearchId`。

- [ ] **Step 1: 编写 currmove 与 PV 的失败回归**

Create `LogicTest/UciOutputParserTests.cs`:

```csharp
using XiangqiOverlay.Engine;

namespace LogicTest;

public static class UciOutputParserTests
{
    public static void Run(Action<string, bool> check)
    {
        var parser = new UciOutputParser();
        var currentMove = parser.ParseInfoLine(
            "info depth 12 currmove h2e2 currmovenumber 1", "Pikafish");
        check("currmove 只是正在尝试的走法，不产生推荐", currentMove == null);

        var pv = parser.ParseInfoLine(
            "info depth 18 seldepth 26 score cp 35 nodes 1000 time 20 pv h2e2 h9g7",
            "Pikafish");
        check("带 PV 的 info 产生真实首选和主要变着",
            pv != null && pv.BestMove?.Move == "h2e2" &&
            pv.BestMove.PvString == "h2e2 h9g7" && pv.Depth == 18 && pv.Score == 35);

        var best = parser.ParseBestMove("bestmove h2e2 ponder h9g7", "Pikafish");
        check("bestmove 保留最终走法和 ponder",
            best != null && best.BestMove?.Move == "h2e2" &&
            best.BestMove.PvString == "h9g7" && best.Depth == 18);
    }
}
```

在 `LogicTest/Program.cs` 的会话测试后加入：

```csharp
LogicTest.UciOutputParserTests.Run(Check);
```

- [ ] **Step 2: 运行并确认解析器尚不存在**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 编译错误包含 `UciOutputParser` 不存在。

- [ ] **Step 3: 提取输出解析器并拒绝 currmove 伪推荐**

Create `XiangqiOverlay/src/XiangqiOverlay.Engine/UciOutputParser.cs`：

```csharp
using System.Text.RegularExpressions;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Engine;

public sealed class UciOutputParser
{
    private int _lastScore;
    private double _lastWinRate;
    private int _lastDepth;

    public AnalysisResult? ParseInfoLine(string line, string engineName)
    {
        try
        {
            var result = new AnalysisResult { EngineName = engineName };
            var depthMatch = Regex.Match(line, @"\bdepth (\d+)\b");
            if (depthMatch.Success)
                result.Depth = int.Parse(depthMatch.Groups[1].Value);

            var nodesMatch = Regex.Match(line, @"\bnodes (\d+)\b");
            if (nodesMatch.Success)
                result.TotalNodes = long.Parse(nodesMatch.Groups[1].Value);

            var timeMatch = Regex.Match(line, @"\btime (\d+)\b");
            if (timeMatch.Success)
                result.TimeMs = long.Parse(timeMatch.Groups[1].Value);

            var scoreMatch = Regex.Match(line, @"\bscore (cp|mate) (-?\d+)\b");
            if (scoreMatch.Success)
            {
                int value = int.Parse(scoreMatch.Groups[2].Value);
                result.Score = scoreMatch.Groups[1].Value == "mate"
                    ? value > 0 ? 100000 - value : -100000 - value
                    : value;
                result.WinRate = 1.0 / (1.0 + Math.Exp(-result.Score / 600.0));
            }

            var pvMatch = Regex.Match(line, @"\bpv\s+(.+)$");
            if (pvMatch.Success)
            {
                string pvString = pvMatch.Groups[1].Value.Trim();
                var moves = ParsePvMoves(pvString);
                if (moves.Count > 0)
                {
                    result.Moves.Add(new AnalysisMove
                    {
                        Move = moves[0].ToUcci(),
                        WinRate = result.WinRate,
                        Score = result.Score,
                        Depth = result.Depth,
                        Nodes = result.TotalNodes,
                        PvString = pvString
                    });
                }
            }

            _lastScore = result.Score;
            _lastWinRate = result.WinRate;
            _lastDepth = result.Depth;
            return result.Moves.Count > 0 ? result : null;
        }
        catch
        {
            return null;
        }
    }

    public AnalysisResult? ParseBestMove(string line, string engineName)
    {
        try
        {
            var match = Regex.Match(line, @"bestmove (\S+)(?:\s+ponder\s+(\S+))?");
            if (!match.Success)
                return null;

            var result = new AnalysisResult
            {
                EngineName = engineName,
                Score = _lastScore,
                WinRate = _lastWinRate,
                Depth = _lastDepth,
                Timestamp = DateTime.Now
            };
            result.Moves.Add(new AnalysisMove
            {
                Move = match.Groups[1].Value,
                Score = _lastScore,
                WinRate = _lastWinRate,
                Depth = _lastDepth,
                PvString = match.Groups[2].Success ? match.Groups[2].Value : string.Empty
            });
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static List<ChessMove> ParsePvMoves(string pvString)
    {
        var moves = new List<ChessMove>();
        foreach (string token in pvString.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 4)
                continue;
            try { moves.Add(ChessMove.FromUcci(token)); }
            catch { break; }
        }
        return moves;
    }
}
```

- [ ] **Step 4: 在 AnalysisResult 中记录会话号**

在 `AnalysisResult` 的 `Fen` 属性后加入：

```csharp
/// <summary>发起本次搜索的递增会话号</summary>
public long SearchId { get; set; }
```

- [ ] **Step 5: 将 UciEngine 接入会话协调器**

在 `UciEngine` 增加字段：

```csharp
private readonly EngineSearchScheduler _searchScheduler = new();
private readonly object _searchLock = new();
private readonly UciOutputParser _outputParser = new();
```

`Start()` 在进程启动成功后、发送 `uci` 前执行：

```csharp
lock (_searchLock)
    _searchScheduler.Reset();
```

将 `Analyze` 和 `Stop` 完整替换为：

```csharp
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
```

删除未使用且绕过会话管理的 `AnalyzeInfinite`。将输出处理中的 `bestmove` 和 `info` 分支替换为：

```csharp
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
```

删除旧 `ParseInfoLine`、`ParseBestMove`、`ParsePvMoves` 以及 `_lastScore`、`_lastWinRate`、`_lastDepth` 字段；保留 `System.Text.RegularExpressions` 不再使用后移除对应 `using`，`System.Text` 也不再使用后移除。

- [ ] **Step 6: 运行测试、构建并提交**

Run:

```powershell
dotnet run --project "LogicTest\LogicTest.csproj"
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Debug
```

Expected: 测试全部通过，构建 0 错误。

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Engine/UciOutputParser.cs XiangqiOverlay/src/XiangqiOverlay.Engine/UciEngine.cs XiangqiOverlay/src/XiangqiOverlay.Core/AnalysisResult.cs LogicTest/UciOutputParserTests.cs LogicTest/Program.cs
git commit -m "fix: bind engine output to its search position" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 在 AnalysisManager 中央拦截旧盘面结果

**Files:**
- Create: `XiangqiOverlay/src/XiangqiOverlay.Engine/IUciEngine.cs`
- Create: `LogicTest/AnalysisManagerTests.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Engine/AnalysisManager.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Engine/UciEngine.cs`
- Modify: `LogicTest/Program.cs`

**Interfaces:**
- Produces: `IUciEngine` 和 `AnalysisManager(AppSettings settings, IUciEngine engine)`，便于用真实事件序列测试旧结果过滤。
- Consumes: Task 2 的真实结果 FEN。

- [ ] **Step 1: 编写旧 FEN 结果与统一时间限制的失败测试**

Create `LogicTest/AnalysisManagerTests.cs`:

```csharp
using XiangqiOverlay.Core;
using XiangqiOverlay.Engine;

namespace LogicTest;

public static class AnalysisManagerTests
{
    public static void Run(Action<string, bool> check)
    {
        var settings = new AppSettings { MaxDepth = 30, MaxNodes = 0, MaxTimeMs = 4000 };
        using var engine = new FakeEngine();
        using var manager = new AnalysisManager(settings, engine);
        int received = 0;
        manager.AnalysisReceived += _ => received++;

        var current = BoardState.InitialPosition();
        current.CurrentPlayer = PlayerSide.Red;
        var old = current.Clone();
        old.CurrentPlayer = PlayerSide.Black;
        manager.SubmitBoard(new BoardReadResult { Board = current });

        engine.Emit(Result(old));
        check("旧 FEN 的分析结果不得发布到当前盘面", received == 0);
        engine.Emit(Result(current));
        check("当前 FEN 的分析结果正常发布", received == 1);
        check("自动提交统一使用深度 30 和 4000ms",
            engine.LastFen == current.ToFen() && engine.LastDepth == 30 &&
            engine.LastNodes == 0 && engine.LastTimeMs == 4000);

        manager.AnalyzeNow();
        check("手动立即分析也使用同一时间限制",
            engine.LastFen == current.ToFen() && engine.LastDepth == 30 && engine.LastTimeMs == 4000);
    }

    private static AnalysisResult Result(BoardState board)
    {
        var result = new AnalysisResult { Fen = board.ToFen() };
        result.Moves.Add(new AnalysisMove { Move = "h2e2" });
        return result;
    }

    private sealed class FakeEngine : IUciEngine
    {
        public event Action<AnalysisResult>? AnalysisReceived;
        public event Action<string>? ErrorOccurred;
        public event Action<string>? StatusChanged;
        public bool IsRunning => true;
        public string LastFen { get; private set; } = string.Empty;
        public int LastDepth { get; private set; }
        public long LastNodes { get; private set; }
        public long LastTimeMs { get; private set; }
        public bool Initialize() => true;
        public void Stop() { }
        public void Analyze(string fen, int maxDepth = 0, long maxNodes = 0, long maxTimeMs = 0)
        {
            LastFen = fen;
            LastDepth = maxDepth;
            LastNodes = maxNodes;
            LastTimeMs = maxTimeMs;
        }
        public void Emit(AnalysisResult result) => AnalysisReceived?.Invoke(result);
        public void Dispose() { }
    }
}
```

在 Program 中加入：

```csharp
LogicTest.AnalysisManagerTests.Run(Check);
```

- [ ] **Step 2: 运行并确认接口和构造函数尚不存在**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 编译错误包含 `IUciEngine` 或对应 `AnalysisManager` 构造函数不存在。

- [ ] **Step 3: 增加可测试引擎接口**

Create `IUciEngine.cs`:

```csharp
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Engine;

public interface IUciEngine : IDisposable
{
    event Action<AnalysisResult>? AnalysisReceived;
    event Action<string>? ErrorOccurred;
    event Action<string>? StatusChanged;
    bool IsRunning { get; }
    bool Initialize();
    void Analyze(string fen, int maxDepth = 0, long maxNodes = 0, long maxTimeMs = 0);
    void Stop();
}
```

将 `UciEngine` 声明改为 `public sealed class UciEngine : IUciEngine`。

- [ ] **Step 4: 修复 AnalysisManager 的结果归属和搜索参数**

将字段、Engine 属性和构造函数改为：

```csharp
private readonly IUciEngine _engine;
private readonly AppSettings _settings;

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
```

在 `SubmitBoard` 中删除 `_engine.Stop()`、`Thread.Sleep(30)` 和 `MoveTime` 分支，统一调用：

```csharp
_engine.Analyze(fen, _settings.MaxDepth, _settings.MaxNodes, _settings.MaxTimeMs);
```

`AnalyzeNow` 同样删除 Stop/Sleep，并统一调用上述参数。

将 `OnEngineAnalysisReceived` 完整替换为：

```csharp
private void OnEngineAnalysisReceived(AnalysisResult result)
{
    string? currentFen;
    lock (_submitLock)
        currentFen = _currentBoard?.ToFen();

    if (currentFen == null || !string.Equals(result.Fen, currentFen, StringComparison.Ordinal))
        return;

    AnalysisReceived?.Invoke(result);
}
```

不再给 `result.Fen` 赋当前盘面值。

- [ ] **Step 5: 运行回归并提交**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 旧结果被丢弃、当前结果通过、自动和手动分析参数一致，全部测试通过。

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Engine/IUciEngine.cs XiangqiOverlay/src/XiangqiOverlay.Engine/AnalysisManager.cs XiangqiOverlay/src/XiangqiOverlay.Engine/UciEngine.cs LogicTest/AnalysisManagerTests.cs LogicTest/Program.cs
git commit -m "fix: discard stale engine analysis results" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 立即刷新回合提示并同步手动行棋方

**Files:**
- Create: `XiangqiOverlay/src/XiangqiOverlay.Core/RecommendationPromptPolicy.cs`
- Create: `LogicTest/RecommendationPromptPolicyTests.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Core/AppSettings.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:964-981`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.App/MainWindow.xaml`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.App/MainWindow.xaml.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.App/settings.example.json`
- Modify: `XiangqiOverlay/README.md`
- Modify: `LogicTest/Program.cs`
- Local only: `XiangqiOverlay/src/XiangqiOverlay.App/settings.json`

**Interfaces:**
- Produces: `RecommendationPromptPolicy.Resolve(BoardState board, PlayerSide ownSide, bool onlyRecommendOwnMoves, AnalysisResult? analysis)`。
- Consumes: 当前盘面和经过 FEN 闸门的结果。

- [ ] **Step 1: 编写“新盘面不得保留旧等待提示”的失败测试**

Create `RecommendationPromptPolicyTests.cs`:

```csharp
using XiangqiOverlay.Core;

namespace LogicTest;

public static class RecommendationPromptPolicyTests
{
    public static void Run(Action<string, bool> check)
    {
        var board = BoardState.InitialPosition();
        board.CurrentPlayer = PlayerSide.Red;
        var old = board.Clone();
        old.CurrentPlayer = PlayerSide.Black;

        check("我方新回合遇到旧分析时立即显示正在思考",
            RecommendationPromptPolicy.Resolve(board, PlayerSide.Red, true, Result(old)) ==
            RecommendationPromptKind.Thinking);
        check("对手回合且仅推荐我方时显示等待",
            RecommendationPromptPolicy.Resolve(old, PlayerSide.Red, true, null) ==
            RecommendationPromptKind.WaitingForOpponent);
        check("当前 FEN 的我方分析可显示推荐",
            RecommendationPromptPolicy.Resolve(board, PlayerSide.Red, true, Result(board)) ==
            RecommendationPromptKind.OwnRecommendation);
        check("允许预测对手时显示对手预测",
            RecommendationPromptPolicy.Resolve(old, PlayerSide.Red, false, Result(old)) ==
            RecommendationPromptKind.OpponentPrediction);
    }

    private static AnalysisResult Result(BoardState board)
    {
        var result = new AnalysisResult { Fen = board.ToFen() };
        result.Moves.Add(new AnalysisMove { Move = "h2e2" });
        return result;
    }
}
```

在 Program 中加入：

```csharp
LogicTest.RecommendationPromptPolicyTests.Run(Check);
```

- [ ] **Step 2: 运行并确认提示策略不存在**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 编译错误包含 `RecommendationPromptPolicy` 不存在。

- [ ] **Step 3: 实现提示策略和默认搜索配置**

Create `RecommendationPromptPolicy.cs`:

```csharp
namespace XiangqiOverlay.Core;

public enum RecommendationPromptKind
{
    WaitingForOpponent,
    Thinking,
    OwnRecommendation,
    OpponentPrediction
}

public static class RecommendationPromptPolicy
{
    public static RecommendationPromptKind Resolve(
        BoardState board,
        PlayerSide ownSide,
        bool onlyRecommendOwnMoves,
        AnalysisResult? analysis)
    {
        bool ownTurn = board.CurrentPlayer == ownSide;
        if (onlyRecommendOwnMoves && !ownTurn)
            return RecommendationPromptKind.WaitingForOpponent;

        bool hasCurrentAnalysis = analysis?.BestMove != null &&
            string.Equals(analysis.Fen, board.ToFen(), StringComparison.Ordinal);
        if (!hasCurrentAnalysis)
            return RecommendationPromptKind.Thinking;

        return ownTurn
            ? RecommendationPromptKind.OwnRecommendation
            : RecommendationPromptKind.OpponentPrediction;
    }
}
```

在 `AppSettings` 中设置：

```csharp
public int MaxDepth { get; set; } = 30;
public long MaxTimeMs { get; set; } = 4000;
```

`settings.example.json` 同样设置 `"MaxDepth": 30` 和 `"MaxTimeMs": 4000`。

- [ ] **Step 4: 让手动行棋方同步到 UI 和工作循环**

将 `MainController.SetCurrentPlayer` 的提交部分改为：

```csharp
if (_trackedBoard != null)
{
    _trackedBoard.CurrentPlayer = player;
    var res = new BoardReadResult
    {
        Board = _trackedBoard.Clone(),
        Source = BoardReadMode.VisionOnly,
        Timestamp = DateTime.Now
    };
    PublishBoardRead(res);
    Interlocked.Increment(ref _commitSeq);
    _analysisManager.SubmitBoard(res);
}
```

这样工作循环下一帧会采纳新行棋方，主界面也会立即收到新盘面。

- [ ] **Step 5: 统一主界面提示刷新**

在 `MainWindow.xaml.cs` 增加：

```csharp
private void UpdateRecommendationPrompt()
{
    if (_lastBoard == null)
    {
        BestMoveText.Text = "—";
        ScoreText.Text = "评分: —";
        WinRateText.Text = "胜率: —";
        DepthText.Text = "深度: —";
        PvText.Text = "";
        return;
    }

    var mySide = _settings.OwnColor == "Black" ? PlayerSide.Black : PlayerSide.Red;
    string mySideStr = mySide == PlayerSide.Black ? "黑方" : "红方";
    string playerStr = _lastBoard.CurrentPlayer == PlayerSide.Black ? "黑方" : "红方";
    var kind = RecommendationPromptPolicy.Resolve(
        _lastBoard, mySide, _settings.OnlyRecommendOwnMoves, _lastAnalysis);
    var current = _lastAnalysis != null && _lastAnalysis.Fen == _lastBoard.ToFen()
        ? _lastAnalysis : null;

    if (kind == RecommendationPromptKind.WaitingForOpponent)
    {
        BestMoveText.Text = $"[等待对手走棋...] 我方执子: {mySideStr}";
        PvText.Text = "当前为对手思考，轮到我方落子时将自动给出最佳招法";
        SetAnalysisMetrics(null);
        return;
    }

    if (kind == RecommendationPromptKind.Thinking || current?.BestMove == null)
    {
        BestMoveText.Text = mySide == _lastBoard.CurrentPlayer
            ? $"[正在思考...] 我方执子: {mySideStr}"
            : $"[正在分析{playerStr}走法...]";
        PvText.Text = "正在分析最新盘面，旧结果不会用于当前棋盘";
        SetAnalysisMetrics(null);
        return;
    }

    var best = current!.BestMove!;
    string pieceName = "";
    try
    {
        var move = ChessMove.FromUcci(best.Move);
        string text = GetPieceText(_lastBoard[move.From].Type, _lastBoard[move.From].Color);
        if (!string.IsNullOrEmpty(text))
            pieceName = $"[{text}] ";
    }
    catch { }

    BestMoveText.Text = kind == RecommendationPromptKind.OwnRecommendation
        ? $"[{playerStr}推荐] {pieceName}{best.Move}"
        : $"[预测{playerStr}走法] {pieceName}{best.Move}";
    PvText.Text = string.IsNullOrEmpty(best.PvString)
        ? "" : $"变着: {best.PvString}";
    SetAnalysisMetrics(current);
}

private void SetAnalysisMetrics(AnalysisResult? result)
{
    ScoreText.Text = result == null ? "评分: —" : $"评分: {result.Score}";
    WinRateText.Text = result == null ? "胜率: —" : $"胜率: {result.WinRate * 100:F1}%";
    DepthText.Text = result == null ? "深度: —" : $"深度: {result.Depth}";
}
```

将 `OnAnalysisReceived` 的 UI 主体替换为：

```csharp
if (_lastBoard == null || result.Fen != _lastBoard.ToFen())
    return;
_lastAnalysis = result;
UpdateRecommendationPrompt();
DrawBoardPreview();
```

在 `OnBoardRead` 成功分支的 `_lastBoard = result.Board;` 后加入：

```csharp
if (_lastAnalysis?.Fen != _lastBoard.ToFen())
    _lastAnalysis = null;
UpdateRecommendationPrompt();
```

将同一成功分支原来的 `StatusText.Text` 赋值替换为：

```csharp
string lastMoveText = result.LastMove is { } lastMove
    ? $"，走子 {lastMove.From}->{lastMove.To}" : "";
string nextSideText = _lastBoard.CurrentPlayer == PlayerSide.Red ? "红方" : "黑方";
StatusText.Text = $"盘面已同步 (红方 {redCount} 子, 黑方 {blackCount} 子){lastMoveText}，轮到{nextSideText}";
```

在 `OnOnlyOwnMovesChanged` 的 `DrawBoardPreview();` 前调用 `UpdateRecommendationPrompt();`。

在 `OnPlayerSelectionChanged` 的 `_controller.SetCurrentPlayer(side);` 后加入：

```csharp
if (_lastBoard != null)
{
    _lastBoard.CurrentPlayer = side;
    _lastAnalysis = null;
    UpdateRecommendationPrompt();
}
```

将 `DrawBoardPreview` 的推荐绘制条件改为：

```csharp
bool hasCurrentAnalysis = _lastAnalysis?.BestMove != null &&
    _lastBoard != null && _lastAnalysis.Fen == _lastBoard.ToFen();
if (shouldDrawPreviewMove && hasCurrentAnalysis)
```

后续将首个走法读取明确写为：

```csharp
var mv = ChessMove.FromUcci(_lastAnalysis!.BestMove!.Move);
```

其余绘制逻辑保持不变。

- [ ] **Step 6: 添加思考时间 UI 并更新文档**

在 `MainWindow.xaml` 的“最大深度”下拉框后加入：

```xml
<TextBlock Text="最长思考" Style="{StaticResource LabelStyle}" Margin="0,8,0,0"/>
<ComboBox x:Name="ThinkTimeCombo" Style="{StaticResource ComboStyle}" SelectedIndex="2">
    <ComboBoxItem Content="1 秒" Tag="1000"/>
    <ComboBoxItem Content="2 秒" Tag="2000"/>
    <ComboBoxItem Content="4 秒" Tag="4000"/>
    <ComboBoxItem Content="6 秒" Tag="6000"/>
</ComboBox>
```

构造函数在深度初始化后加入：

```csharp
ThinkTimeCombo.SelectedIndex = settings.MaxTimeMs switch
{
    1000 => 0, 2000 => 1, 6000 => 3, _ => 2
};
```

`OnSaveSettingsClick` 在深度保存后加入：

```csharp
_settings.MaxTimeMs = long.Parse(((System.Windows.Controls.ComboBoxItem)ThinkTimeCombo.SelectedItem).Tag.ToString()!);
```

将 README 配置表默认深度改为 30，并添加：

```markdown
| MaxTimeMs | 每次最长思考时间（毫秒） | 4000 |
```

本机被忽略的 `XiangqiOverlay/src/XiangqiOverlay.App/settings.json` 设置为以下值，不提交该文件：

```json
"MaxDepth": 30,
"MaxTimeMs": 4000
```

- [ ] **Step 7: 运行回归和 Debug 构建后提交**

Run:

```powershell
dotnet run --project "LogicTest\LogicTest.csproj"
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Debug
```

Expected: 提示策略全部通过，构建 0 错误。

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Core/RecommendationPromptPolicy.cs XiangqiOverlay/src/XiangqiOverlay.Core/AppSettings.cs XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs XiangqiOverlay/src/XiangqiOverlay.App/MainWindow.xaml XiangqiOverlay/src/XiangqiOverlay.App/MainWindow.xaml.cs XiangqiOverlay/src/XiangqiOverlay.App/settings.example.json XiangqiOverlay/README.md LogicTest/RecommendationPromptPolicyTests.cs LogicTest/Program.cs
git commit -m "fix: refresh turn prompts without stale analysis" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 固化真实黑卒走子回归并执行最终验证

**Files:**
- Create: `LogicTest/TurnRecoveryTests.cs`
- Modify: `LogicTest/Program.cs`

**Interfaces:**
- Consumes: 现有 `VisionBoardReader`、`BoardReadResult.InferLastMove`、`ConfirmedBoardUpdater`。
- Uses: 用户提供的 `C:\Users\123\Pictures\Screenshots\屏幕截图 2026-11-13 022057.png`，只在本机样本存在时运行。

- [ ] **Step 1: 将临时诊断转换成最终断言**

Create `LogicTest/TurnRecoveryTests.cs`:

```csharp
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

        bool ok = move != null && ConfirmedBoardUpdater.TryBuildMoveCommit(
            previous, raw, new[] { move.Value }, tracked.HighlightedCells, out var next);
        check("真实卡回合截图：安全提交保留黑卒兵种",
            ok && next != null && next[2, 5].IsEmpty &&
            next[2, 6].Type == PieceType.Pawn && next[2, 6].Color == PieceColor.Black);

        if (next != null)
            next.CurrentPlayer = PlayerSide.Red;
        check("真实卡回合截图：黑方走完后轮到红方",
            next != null && next.CurrentPlayer == PlayerSide.Red && next.ToFen().Contains(" w "));
    }
}
```

在 Program 中加入：

```csharp
LogicTest.TurnRecoveryTests.Run(Check);
```

- [ ] **Step 2: 运行真实截图回归**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 四个真实截图断言均为 `PASS`。若失败，停止并检查截图是否变化、四角是否仍匹配，不修改断言来迁就错误识别。

- [ ] **Step 3: 执行最终完整验证**

Run:

```powershell
dotnet run --project "LogicTest\LogicTest.csproj"
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Debug
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Release
```

Expected:

- 所有测试最后输出 `全部通过`。
- Debug 和 Release 均为 0 错误。
- 源码中不再用 `_currentBoard.ToFen()` 覆盖引擎结果 FEN。
- 源码中不再把 `currmove` 添加为推荐。
- `AnalysisManager` 的自动和手动分析均直接使用 `MaxTimeMs`。

- [ ] **Step 4: 提交真实回归并确认工作区**

```powershell
git add LogicTest/TurnRecoveryTests.cs LogicTest/Program.cs
git commit -m "test: cover rapid opponent move turn recovery" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
git status --short
```

Expected: `git status --short` 无输出。本机 settings.json 已调整但被忽略，不进入提交。

---

## 实局验收边界

1. 重启程序，使新的引擎会话代码和本机 4 秒/深度 30 配置生效。
2. 在 JJ 象棋中验证对手普通走子、快速连续走子和手动切换当前行棋方。
3. 我方回合应先显示“正在思考”，随后显示当前盘面的推荐；不得继续显示旧盘面的等待提示。
4. 若视觉仍拒绝某步，保留状态栏中的具体拒绝原因；不得为了通过实局而恢复自动兵种改写。
5. 天天象棋和 QQ/欢乐象棋仍属于后续独立样本与模板计划。
