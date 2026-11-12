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
