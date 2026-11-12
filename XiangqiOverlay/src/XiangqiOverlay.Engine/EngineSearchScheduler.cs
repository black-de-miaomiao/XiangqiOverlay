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
