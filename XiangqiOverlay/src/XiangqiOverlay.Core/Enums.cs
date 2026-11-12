namespace XiangqiOverlay.Core;

/// <summary>
/// 棋子类型（中国象棋）
/// </summary>
public enum PieceType
{
    Empty,
    King,       // 将/帅
    Advisor,    // 士/仕
    Elephant,   // 象/相
    Horse,      // 马/傌
    Chariot,    // 车/俥
    Cannon,     // 炮/砲
    Pawn        // 兵/卒
}

/// <summary>
/// 棋子颜色
/// </summary>
public enum PieceColor
{
    Unknown,
    Red,        // 红方（下方）
    Black       // 黑方（上方）
}

/// <summary>
/// 盘面读取方式
/// </summary>
public enum BoardReadMode
{
    Auto,               // 自动（当前使用视觉识别）
    MemoryOnly,         // 兼容旧配置，当前未实现
    VisionOnly,         // 仅视觉
    MemoryWithVision    // 兼容旧配置，当前未实现
}

/// <summary>
/// 当前行棋方
/// </summary>
public enum PlayerSide
{
    Red,
    Black
}
