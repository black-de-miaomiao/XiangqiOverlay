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
