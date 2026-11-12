namespace XiangqiOverlay.Core;

/// <summary>
/// 棋盘在屏幕上的校准信息
/// 中国象棋棋盘有 9 列 × 10 行的交叉点
/// 棋盘的四角点用于建立坐标系
/// </summary>
public sealed class BoardCalibration
{
    /// <summary>左上角（第0列，第0行交叉点）</summary>
    public ScreenPoint TopLeft { get; set; }

    /// <summary>右上角（第8列，第0行交叉点）</summary>
    public ScreenPoint TopRight { get; set; }

    /// <summary>左下角（第0列，第9行交叉点）</summary>
    public ScreenPoint BottomLeft { get; set; }

    /// <summary>右下角（第8列，第9行交叉点）</summary>
    public ScreenPoint BottomRight { get; set; }

    /// <summary>校准是否有效</summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// 屏幕方向是否与标准坐标系相反（执黑时游戏会翻转棋盘：红上黑下）。
    /// 为 true 时棋盘坐标 (col,row) 对应屏幕上的 (8-col, 9-row) 交叉点。
    /// </summary>
    public bool Flipped { get; set; }

    /// <summary>校准时的时间戳</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 校准时的窗口客户区宽度（像素）。0 = 未知（不启用等比缩放）。
    /// JJ象棋窗口缩放时棋盘内容随窗口等比变化，工作循环据此自动缩放网格，
    /// 否则窗口一变网格就整体错位——越靠边缘偏差越大，识别全面出错。
    /// </summary>
    public int CalibratedClientWidth { get; set; }

    /// <summary>校准时的窗口客户区高度（像素）。0 = 未知。</summary>
    public int CalibratedClientHeight { get; set; }

    /// <summary>
    /// 按窗口缩放比例等比缩放全部角点（窗口客户区尺寸变化时调用）
    /// </summary>
    public void ScaleTo(double scaleX, double scaleY)
    {
        TopLeft = new ScreenPoint(TopLeft.X * scaleX, TopLeft.Y * scaleY);
        TopRight = new ScreenPoint(TopRight.X * scaleX, TopRight.Y * scaleY);
        BottomLeft = new ScreenPoint(BottomLeft.X * scaleX, BottomLeft.Y * scaleY);
        BottomRight = new ScreenPoint(BottomRight.X * scaleX, BottomRight.Y * scaleY);
    }

    /// <summary>
    /// 将棋盘坐标 (col, row) 转换为屏幕坐标
    /// 使用双线性插值计算交叉点位置
    /// </summary>
    public ScreenPoint BoardToScreen(int col, int row)
    {
        if (!IsValid)
            return new ScreenPoint(0, 0);

        // 翻转棋盘：标准棋盘坐标映射到镜像屏幕交叉点
        if (Flipped)
        {
            col = 8 - col;
            row = 9 - row;
        }

        // 棋盘网格有 8 列间隔和 9 行间隔
        double tx = col / 8.0;
        double ty = row / 9.0;

        // 上下边插值
        var top = ScreenPoint.Lerp(TopLeft, TopRight, tx);
        var bottom = ScreenPoint.Lerp(BottomLeft, BottomRight, tx);

        // 上下插值得到最终位置
        return ScreenPoint.Lerp(top, bottom, ty);
    }

    /// <summary>
    /// 将屏幕坐标转换为棋盘坐标
    /// 返回最近的交叉点 (col, row)，如果不在棋盘范围内则返回 null
    /// </summary>
    public (int col, int row)? ScreenToBoard(ScreenPoint screen)
    {
        if (!IsValid)
            return null;

        // 逆向求解双线性插值
        // 先用行列方向二分搜索找到最近交叉点
        int bestCol = -1, bestRow = -1;
        double bestDist = double.MaxValue;

        for (int c = 0; c < 9; c++)
        {
            for (int r = 0; r < 10; r++)
            {
                var p = BoardToScreen(c, r);
                double dist = (p - screen).LengthSquared;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestCol = c;
                    bestRow = r;
                }
            }
        }

        // 距离阈值：超过半个格子则不算
        var gridSize = (BoardToScreen(1, 0) - BoardToScreen(0, 0)).Length;
        if (bestDist > gridSize * gridSize * 0.25)
            return null;

        return (bestCol, bestRow);
    }

    /// <summary>
    /// 复制当前校准并覆盖翻转标记
    /// </summary>
    public BoardCalibration WithFlipped(bool flipped) => new()
    {
        TopLeft = TopLeft,
        TopRight = TopRight,
        BottomLeft = BottomLeft,
        BottomRight = BottomRight,
        IsValid = IsValid,
        Flipped = flipped,
        Timestamp = Timestamp,
        CalibratedClientWidth = CalibratedClientWidth,
        CalibratedClientHeight = CalibratedClientHeight
    };

    /// <summary>
    /// 获取一个格子的采样半径（用于棋子识别）
    /// </summary>
    public double GetCellRadius()
    {
        if (!IsValid)
            return 20.0;

        // 用相邻格子的间距的一半作为半径
        var p0 = BoardToScreen(0, 0);
        var p1 = BoardToScreen(1, 0);
        return (p1 - p0).Length * 0.42;
    }

}
