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
