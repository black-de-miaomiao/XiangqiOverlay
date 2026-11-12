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
        public event Action<string>? ErrorOccurred { add { } remove { } }
        public event Action<string>? StatusChanged { add { } remove { } }
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
