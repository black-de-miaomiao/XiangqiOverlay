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
