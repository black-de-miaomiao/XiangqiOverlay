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
