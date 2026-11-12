namespace XiangqiOverlay.Core;

/// <summary>
/// 程序运行配置
/// </summary>
public sealed class AppSettings
{
    /// <summary>Pikafish 可执行文件路径（相对于程序目录）</summary>
    public string PikafishExecutable { get; set; } = "runtime\\pikafish\\pikafish.exe";

    /// <summary>Pikafish 评估函数文件路径</summary>
    public string PikafishEvalFile { get; set; } = "runtime\\pikafish\\default_nnue.bin";

    /// <summary>棋盘列数（固定9）</summary>
    public int BoardCols { get; set; } = 9;

    /// <summary>棋盘行数（固定10）</summary>
    public int BoardRows { get; set; } = 10;

    /// <summary>最大搜索深度</summary>
    public int MaxDepth { get; set; } = 24;

    /// <summary>最大思考时间（毫秒，0=不限制）</summary>
    public long MaxTimeMs { get; set; } = 0;

    /// <summary>最大搜索节点数（0=不限制）</summary>
    public long MaxNodes { get; set; } = 0;

    /// <summary>分析间隔（秒）</summary>
    public double AnalysisIntervalSeconds { get; set; } = 0.3;

    /// <summary>悬浮层显示的候选着数量</summary>
    public int TopMoves { get; set; } = 5;

    /// <summary>我的执子颜色</summary>
    public string OwnColor { get; set; } = "Red";

    /// <summary>启动时的行棋方</summary>
    public string CurrentPlayer { get; set; } = "Red";

    /// <summary>盘面读取方式</summary>
    public string BoardReadMode { get; set; } = "Auto";

    /// <summary>视觉抓屏时排除悬浮层</summary>
    public bool ExcludeOverlayFromCapture { get; set; } = true;

    /// <summary>仅在我方回合推荐走法（对方思考时不画走法箭头，显示等待提示）</summary>
    public bool OnlyRecommendOwnMoves { get; set; } = true;

    /// <summary>线程数</summary>
    public int Threads { get; set; } = 4;

    /// <summary>哈希表大小（MB）</summary>
    public int HashSize { get; set; } = 128;

    /// <summary>UCI 选项：思考时间倍率</summary>
    public double MoveTime { get; set; } = 0;

    /// <summary>是否启用调试日志</summary>
    public bool EnableDebugLog { get; set; } = false;

    /// <summary>
    /// 从 JSON 文件加载配置
    /// </summary>
    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
            return new AppSettings();

        var json = File.ReadAllText(path);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    /// <summary>
    /// 保存配置到 JSON 文件
    /// </summary>
    public void Save(string path)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    // 便捷属性
    public PlayerSide OwnSide => OwnColor == "Black" ? PlayerSide.Black : PlayerSide.Red;
    public PlayerSide StartPlayer => CurrentPlayer == "Black" ? PlayerSide.Black : PlayerSide.Red;
    public BoardReadMode ReadMode => Enum.Parse<BoardReadMode>(BoardReadMode, true);
}
