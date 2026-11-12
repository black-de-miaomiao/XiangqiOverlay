# 象棋盘面完整性与“变子”修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 确保正常对局中的已确认棋子只能随合法走法移动，彻底阻止自动自愈、异常重锚定和截图通道差异把未移动棋子改成其他兵种。

**Architecture:** 保留现有视觉分类器和主循环，新增纯规则组件 `ConfirmedBoardUpdater`，所有正常走子提交都从上一已确认盘面执行走法，而不是采纳候选盘面的无关类型。随后移除视觉层的自动自愈和规则降级，并统一 BGR/BGRA 输入。平台模板库及天天象棋、QQ/欢乐象棋接入使用后续独立计划。

**Tech Stack:** .NET 8、C#、WPF、OpenCvSharp 4.13、现有 LogicTest 控制台断言程序、Git。

## Global Constraints

- 已确认棋子的兵种只能随合法走子移动，不能在同一格自动变成另一兵种。
- 未移动且颜色未变的格子必须保留上一已确认兵种。
- 异常重锚定不得顺带修改无走子证据格子的兵种；无法由合法走法解释时保留旧基准。
- 象棋规则用于校验和拒绝矛盾，不再把棋子强制降级为另一兵种。
- 运行时不得把未确认的分类预测反哺为模板。
- 生产截图与离线样本必须走相同的 BGR 三通道识别路径。
- 第一阶段仅支持 Windows 可截取客户区的可见窗口；本计划不宣称完成天天象棋或 QQ/欢乐象棋识别验证。
- 所有提交信息末尾必须包含 `Co-Authored-By: Claude Code <noreply@anthropic.com>`。

## Scope

本计划覆盖总设计 `docs/superpowers/specs/2026-11-13-multi-platform-recognition-design.md` 的输入一致性、规则用途、状态不变量、异常提交修复、相关回归和构建验证。

平台注册表、独立 `BoardObservation` 模型、平台模板包、平台选择 UI，以及天天象棋/QQ/欢乐象棋真实样本验证属于紧随其后的独立实施计划；它们依赖本计划先建立稳定的已确认盘面边界。

---

### Task 1: 建立可用的 Git 基线

**Files:**
- Create: `.gitignore`
- Use: `XiangqiOverlay/.gitignore`
- Use: `LogicTest/Program.cs`

**Interfaces:**
- Consumes: 已初始化的仓库根目录 `C:\Users\123\Desktop\xiangqi`，当前分支 `main`。
- Produces: 工作分支 `fix/board-integrity`；不包含构建产物、用户配置和 Pikafish 二进制的干净基线。

- [ ] **Step 1: 在仓库根目录写入统一的 `.gitignore`**

```gitignore
# .NET build output
**/bin/
**/obj/
**/.vs/
*.user
*.suo
*.pdb

# Publish and temporary output
**/dist/
**/publish/
**/scratch/
**/TestResults/
*.tmp
*.bak
crash.log
in.txt
windows_debug.txt
logs/

# Local user settings; keep settings.example.json
**/settings.json

# IDE
.idea/
.vscode/

# Third-party engine binaries and bundled wiki stay on the local machine
XiangqiOverlay/src/XiangqiOverlay.App/runtime/pikafish/
```

- [ ] **Step 2: 验证现有基线，再创建功能分支**

Run:

```powershell
dotnet run --project "LogicTest\LogicTest.csproj"
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Debug
git switch -c fix/board-integrity
git status --short
```

Expected:

- LogicTest 最后一行是 `全部通过`。
- 解决方案构建结果为 `0 个错误` / `0 Error(s)`。
- 当前分支为 `fix/board-integrity`。
- `git status --short` 不显示任何 `bin/`、`obj/`、`settings.json`、`crash.log` 或 Pikafish 二进制。

- [ ] **Step 3: 提交项目基线**

```powershell
git add .gitignore build_and_run.bat XiangqiOverlay LogicTest
git commit -m "chore: establish XiangqiOverlay baseline" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

Expected: 提交包含源代码、LogicTest、设计文档和本计划，不包含被忽略的本地运行产物。

---

### Task 2: 用纯规则提交组件复现并阻止无关棋子变种

**Files:**
- Create: `XiangqiOverlay/src/XiangqiOverlay.Core/ConfirmedBoardUpdater.cs`
- Create: `LogicTest/BoardIntegrityTests.cs`
- Modify: `LogicTest/Program.cs:461-462`

**Interfaces:**
- Consumes: `BoardState.Clone()`、`XiangqiMoveRules.ApplyMove`、`ChessMove`、`BoardPoint`。
- Produces: `ConfirmedBoardUpdater.TryBuildMoveCommit(BoardState committed, BoardState observed, IReadOnlyList<ChessMove> moves, IReadOnlyCollection<BoardPoint>? highlightedCells, out BoardState? next)`。

- [ ] **Step 1: 编写“炮不变车”、两步追赶和高亮落点回归**

Create `LogicTest/BoardIntegrityTests.cs`:

```csharp
using XiangqiOverlay.Core;

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
    }
}
```

在 `LogicTest/Program.cs` 最后的汇总前加入：

```csharp
LogicTest.BoardIntegrityTests.Run(Check);

Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n{failures} 个失败");
return failures == 0 ? 0 : 1;
```

- [ ] **Step 2: 运行测试并确认因组件尚不存在而失败**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 编译失败，错误包含 `ConfirmedBoardUpdater` 当前不存在；不得跳过该失败直接写其他功能。

- [ ] **Step 3: 实现最小纯规则提交组件**

Create `XiangqiOverlay/src/XiangqiOverlay.Core/ConfirmedBoardUpdater.cs`:

```csharp
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
```

- [ ] **Step 4: 运行回归并确认全部通过**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 新增的五个 `盘面完整性` 场景均为 `PASS`，最后一行为 `全部通过`。

- [ ] **Step 5: 提交纯规则组件和回归**

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Core/ConfirmedBoardUpdater.cs LogicTest/BoardIntegrityTests.cs LogicTest/Program.cs
git commit -m "fix: preserve piece identities during move commits" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 3: 将主控制器所有危险提交入口改为安全提交

**Files:**
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:137-157`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:399-413`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:452-472`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:888-950`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:952-989`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs:1141-1235`
- Test: `LogicTest/BoardIntegrityTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `ConfirmedBoardUpdater.TryBuildMoveCommit`。
- Produces: 正常走子使用重建盘面；持续异常和大幅异常不再自动提交候选类型；中局重新校准走 `RequestAnalysis` 的同一安全门控。

- [ ] **Step 1: 替换工作循环的单步提交盘面**

在 `MainController.WorkerLoop` 的 `if (legal && !(redLeft && blackLeft))` 成功分支中，先调用：

```csharp
if (!ConfirmedBoardUpdater.TryBuildMoveCommit(
        committedBoard, rawBoard, new[] { move!.Value },
        readResult.HighlightedCells, out var confirmedBoard) || confirmedBoard == null)
{
    illegalStreak++;
    StatusChanged?.Invoke("走子的原始占用证据不足，已保留基准盘面");
    Thread.Sleep(pollMs);
    continue;
}
```

随后将该分支原来的 `readResult.Board.CurrentPlayer = ...` 和 `committedBoard = readResult.Board.Clone()` 提交改为：

```csharp
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
_trackedBoard = confirmedBoard.Clone();
seenSeq = Interlocked.Increment(ref _commitSeq);
_analysisManager.SubmitBoard(confirmedResult);
recentFrames.Clear();
ClearExpectedBoard();
PublishBoardRead(confirmedResult);
```

保留原有 `else` 的非法走法计数，但该计数达到门槛后只执行：

```csharp
illegalStreak = 0;
StatusChanged?.Invoke("盘面持续异常且无法由合法走法解释，已保留基准；请重新校准或手动同步");
```

删除原 `IsSaneReanchor(readResult.Board, committedBoard)` 转正、发布和提交引擎的整个分支。

- [ ] **Step 2: 禁止大幅变化分支自动接受整张候选盘面**

将 `if (bigDiffStreak >= 2)` 的整个主体替换为：

```csharp
bigDiffStreak = 0;
StatusChanged?.Invoke("盘面大幅变化且无法由合法走法唯一解释，已保留基准；请手动同步");
```

不得在该分支修改 `CurrentPlayer`、`committedBoard`、`_trackedBoard`，也不得调用 `_analysisManager.SubmitBoard` 或 `PublishBoardRead`。

删除已不再使用的 `IsSaneReanchor` 方法；保留仍被多处使用的 `CountOccupancyColorDifferences`。

- [ ] **Step 3: 手动“下一手推荐”使用同一提交组件**

在 `RequestAnalysis` 的单步合法分支中，先执行：

```csharp
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
```

将该分支后的 `chosen` 替换为只包含安全盘面的结果：

```csharp
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
```

在 `RequestAnalysis` 最后的首次同步/大幅变化分支中，只有 `_trackedBoard == null` 才允许直接建立初始盘面；非空基准无法由走法解释时提交旧盘面供分析并返回：

```csharp
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
```

删除原来的 `rawStable && IsSaneReanchor(...)` 手动重锚定逻辑。

- [ ] **Step 4: 中局重新校准不得直接覆盖已确认类型**

在 `AutoCalibrate` 的 `if (calibration.IsValid)` 中、`try` 读取盘面前加入：

```csharp
if (_trackedBoard != null)
{
    RequestAnalysis();
    return true;
}
```

在 `SetCalibration` 的 `if (_targetWindow != null)` 中、现有 `try` 前加入：

```csharp
if (_trackedBoard != null)
{
    RequestAnalysis();
    return;
}
```

无历史基准时仍保留原来的初始读取和提交逻辑。

- [ ] **Step 5: 运行回归与两种配置构建**

Run:

```powershell
dotnet run --project "LogicTest\LogicTest.csproj"
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Debug
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Release
```

Expected: LogicTest 为 `全部通过`；两次构建均为 `0 Error(s)`；源码中不再存在 `IsSaneReanchor`。

- [ ] **Step 6: 提交主控制器修复**

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Platform/MainController.cs
git commit -m "fix: reject unsafe automatic board reanchoring" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 4: 移除视觉层的自动自愈和兵种降级

**Files:**
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs:41-47`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs:75-81`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs:288-296`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs:502-507`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs:628-640`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs:1355-1372`
- Modify: `LogicTest/BoardIntegrityTests.cs`
- Modify: `LogicTest/Program.cs:200-221,395-459`

**Interfaces:**
- Consumes: 现有 `RecognizeRaw`、`TrackBoard`、开局位置真值模板学习。
- Produces: 视觉层不再调用 `HealFrozenTypes`、`LearnSpritesFromAgreement` 或 `EnforceXiangqiRules`，也不凭空补将帅。

- [ ] **Step 1: 添加“不凭空生成将帅”的失败回归**

在 `BoardIntegrityTests.Run` 末尾加入：

```csharp
var noKings = new BoardState();
noKings[0, 0] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Black };
noKings[0, 9] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
var noKingsTracked = PieceRecognizer.TrackBoard(noKings, noKings.Clone());
check("视觉追踪不得因规则保底而凭空生成将帅",
    !noKingsTracked.GetAllPieces().Any(p => p.type == PieceType.King));
```

在文件顶部补充：

```csharp
using XiangqiOverlay.Vision;
```

- [ ] **Step 2: 运行并确认新回归失败**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: `视觉追踪不得因规则保底而凭空生成将帅` 为 `FAIL`，因为当前 `TrackBoard` 末尾会补两个 King。

- [ ] **Step 3: 让 `Recognize` 只追踪，不修改冻结类型或学习未确认贴图**

将 `Recognize` 方法完整替换为：

```csharp
public BoardState Recognize(Mat screenshot, BoardCalibration calibration, BoardState? previousBoard = null)
{
    var raw = RecognizeRaw(screenshot, calibration);
    LastRawBoard = raw.Clone();
    var tracked = TrackBoard(previousBoard, raw, _highlightedCells);
    tracked.CurrentPlayer = previousBoard?.CurrentPlayer ?? tracked.CurrentPlayer;
    return tracked;
}
```

删除 `HealFrozenTypes` 和 `LearnSpritesFromAgreement` 方法及其调用；删除 `_typeHealVotes`、`TypeHealFrames`、`SetCellEvidenceForTest` 和 `_lastClassifiedTypes`。同时删除 `ResetOrientation` 中的 `_typeHealVotes.Clear()`、`RecognizeCell` 中对 `_lastClassifiedTypes` 的赋值，以及不再需要的 `InternalsVisibleTo` 和 `using System.Runtime.CompilerServices;`。

保留 `_lastLearnScores` 与 `_lastTypeMargins`，供后续平台观察模型读取置信度。

- [ ] **Step 4: 删除所有主动兵种改写入口**

在 `RecognizeRaw` 末尾只返回观察盘面：

```csharp
// 如果识别到接近开局阵列，自动将高置信度字模收录到自适应库中
TryLearnAdaptiveTemplates(screenshot, calibration, board);

// 原始视觉观察不执行兵种降级或数量整型；无效盘面由提交层拒绝。
return board;
```

删除 `RecognizeCell` 中根据当前预测补充 `_adaptiveTemplates` 的 `if (learnScore >= 0.72)` 整块；保留 `_lastLearnScores[col, row]` 和 `_lastTypeMargins[col, row]` 的赋值以及返回的 `BoardCell`。

在 `TrackBoard` 中删除“将帅保底”两个 `if` 块，方法末尾改为：

```csharp
// 走子追踪只继承和转移已有棋子，不执行兵种降级，也不凭空补将帅。
return result;
```

删除不再被调用的 `EnforceXiangqiRules`、`EnforceSideRules`、`IsTrustedType`、`ClampPieceTypeCount`、`PickFallbackType` 和 `GetPositionLegalityScore`。保留 `GetLegalPieceTypesForPosition`，它只限制视觉候选，不修改已确认盘面。

删除 `LogicTest/Program.cs` 中“冻结类型自愈”和“自愈证据门槛”两个旧测试区块；这些测试期待的行为正是本次要移除的自动变种行为。

- [ ] **Step 5: 运行完整回归**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 新将帅回归为 `PASS`，旧自愈测试已移除，其余有效测试和 Task 2 的提交测试全部 `PASS`，最后一行为 `全部通过`。

- [ ] **Step 6: 提交视觉层清理**

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Vision/PieceRecognizer.cs LogicTest/BoardIntegrityTests.cs LogicTest/Program.cs
git commit -m "fix: stop visual rules from mutating piece types" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
```

---

### Task 5: 统一生产截图与离线测试的 BGR 输入

**Files:**
- Create: `XiangqiOverlay/src/XiangqiOverlay.Vision/ScreenshotNormalizer.cs`
- Create: `LogicTest/ScreenshotNormalizationTests.cs`
- Modify: `XiangqiOverlay/src/XiangqiOverlay.Vision/VisionBoardReader.cs:18-58`
- Modify: `LogicTest/Program.cs:118-137,461-462`
- Modify: `XiangqiOverlay/README.md`

**Interfaces:**
- Consumes: OpenCvSharp `Mat`、现有 `VisionBoardReader.ReadBoard` 和 `AutoDetectAndRead`。
- Produces: `ScreenshotNormalizer.ToBgr(Mat screenshot)`；所有识别入口统一三通道。

- [ ] **Step 1: 编写通道转换失败测试**

Create `LogicTest/ScreenshotNormalizationTests.cs`:

```csharp
using OpenCvSharp;
using XiangqiOverlay.Vision;

namespace LogicTest;

public static class ScreenshotNormalizationTests
{
    public static void Run(Action<string, bool> check)
    {
        using var bgr = new Mat(1, 1, MatType.CV_8UC3);
        bgr.Set(0, 0, new Vec3b(10, 20, 30));

        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        using var normalized = ScreenshotNormalizer.ToBgr(bgra);
        var pixel = normalized.Get<Vec3b>(0, 0);
        check("BGRA 生产截图转换为相同 BGR 像素",
            normalized.Channels() == 3 &&
            pixel.Item0 == 10 && pixel.Item1 == 20 && pixel.Item2 == 30);

        using var clone = ScreenshotNormalizer.ToBgr(bgr);
        var clonedPixel = clone.Get<Vec3b>(0, 0);
        check("BGR 离线图片返回独立副本且像素不变",
            !ReferenceEquals(bgr, clone) && clone.Channels() == 3 &&
            clonedPixel.Item0 == 10 && clonedPixel.Item1 == 20 && clonedPixel.Item2 == 30);
    }
}
```

在 `LogicTest/Program.cs` 汇总前加入：

```csharp
LogicTest.ScreenshotNormalizationTests.Run(Check);
```

- [ ] **Step 2: 确认测试因规范化组件不存在而失败**

Run: `dotnet run --project "LogicTest\LogicTest.csproj"`

Expected: 编译错误包含 `ScreenshotNormalizer` 不存在。

- [ ] **Step 3: 实现规范化并接入识别入口**

Create `XiangqiOverlay/src/XiangqiOverlay.Vision/ScreenshotNormalizer.cs`:

```csharp
using OpenCvSharp;

namespace XiangqiOverlay.Vision;

public static class ScreenshotNormalizer
{
    public static Mat ToBgr(Mat screenshot)
    {
        if (screenshot.Empty())
            return new Mat();

        switch (screenshot.Channels())
        {
            case 3:
                return screenshot.Clone();
            case 4:
            {
                var bgr = new Mat();
                Cv2.CvtColor(screenshot, bgr, ColorConversionCodes.BGRA2BGR);
                return bgr;
            }
            case 1:
            {
                var bgr = new Mat();
                Cv2.CvtColor(screenshot, bgr, ColorConversionCodes.GRAY2BGR);
                return bgr;
            }
            default:
                throw new ArgumentException($"不支持的截图通道数: {screenshot.Channels()}", nameof(screenshot));
        }
    }
}
```

在 `VisionBoardReader.ReadBoard` 中，将 `_recognizer.Recognize(screenshot, ...)` 改为：

```csharp
using var normalized = ScreenshotNormalizer.ToBgr(screenshot);
var board = _recognizer.Recognize(normalized, calibration, previousBoard);
```

将 `AutoDetectAndRead` 完整替换为：

```csharp
public (BoardCalibration calibration, BoardReadResult readResult) AutoDetectAndRead(Mat screenshot)
{
    using var normalized = ScreenshotNormalizer.ToBgr(screenshot);
    var calibration = _detector.AutoDetect(normalized);
    var readResult = ReadBoard(normalized, calibration);
    return (calibration, readResult);
}
```

- [ ] **Step 4: 在现有合成棋盘测试中验证完整识别等价性**

在 `LogicTest/Program.cs` 的“连续两帧识别一致”测试之后、`reader.Dispose()` 之前加入：

```csharp
var expectedRaw = reader.LastRawBoard.Clone();
using var productionBgra = new Mat();
Cv2.CvtColor(board, productionBgra, ColorConversionCodes.BGR2BGRA);
var readProduction = reader.ReadBoard(productionBgra, cal);
Check("生产 BGRA 与离线 BGR 完整盘面识别一致",
    readProduction.Board.CountDifferences(readAgain.Board) == 0 &&
    reader.LastRawBoard.CountDifferences(expectedRaw) == 0);
```

- [ ] **Step 5: 更新开发文档**

在 `XiangqiOverlay/README.md` 的“开发构建”部分加入：

````markdown
### 回归测试

```powershell
dotnet run --project ..\LogicTest\LogicTest.csproj
```

盘面提交遵循两条不变量：未移动棋子的兵种不会被识别噪声改写；无法由合法走法解释的持续异常不会自动重锚定整张候选盘面。生产 BGRA 截图与离线 BGR 图片在识别前统一转换为三通道。
````

- [ ] **Step 6: 执行最终验证**

Run:

```powershell
dotnet run --project "LogicTest\LogicTest.csproj"
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Debug
dotnet build "XiangqiOverlay\XiangqiOverlay.sln" --configuration Release
```

Expected:

- 所有测试输出 `PASS`，包括 BGR/BGRA 等价性，最后为 `全部通过`。
- Debug 与 Release 均为 `0 Error(s)`。
- 不再存在自动“已重新锚定”提交分支、自动自愈调用或规则降级调用。

- [ ] **Step 7: 提交输入一致性与文档**

```powershell
git add XiangqiOverlay/src/XiangqiOverlay.Vision/ScreenshotNormalizer.cs XiangqiOverlay/src/XiangqiOverlay.Vision/VisionBoardReader.cs XiangqiOverlay/README.md LogicTest/ScreenshotNormalizationTests.cs LogicTest/Program.cs
git commit -m "test: normalize production screenshots before recognition" -m "Co-Authored-By: Claude Code <noreply@anthropic.com>"
git status --short
```

Expected: `git status --short` 无输出，工作区干净。

---

## 后续独立计划边界

1. **平台化识别基础**：实现 `BoardObservation`、置信度/未知兵种、`PlatformRegistry`、JJ 模板包和平台选择 UI；复用本计划的 `ConfirmedBoardUpdater`。
2. **天天象棋样本包**：收集开局、中局、翻转、缩放和高亮截图，人工标注期望盘面，加入模板与离线回归后再标记支持。
3. **QQ/欢乐象棋样本包**：使用同样的样本标准和验收门槛独立接入。
4. 用户提供的两张问题截图用于下一阶段 JJ 视觉诊断；本计划的“炮变车”验收使用确定性状态序列，避免把不同局面的两张图误当成逐帧样本。
