using OpenCvSharp;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Vision;

/// <summary>
/// 棋盘视觉识别器
/// 通过截图自动识别中国象棋棋盘边界和交叉点
/// </summary>
public sealed class BoardDetector : IDisposable
{
    private bool _disposed;

    /// <summary>Hough 直线分组项：线段中心位置与长度</summary>
    private readonly record struct GridLine(double Position, double Length);

    /// <summary>
    /// 从截图中自动寻找棋盘边界与9x10网格交叉点
    /// </summary>
    public BoardCalibration AutoDetect(Mat screenshot)
    {
        var calibration = new BoardCalibration();

        try
        {
            using var gray = new Mat();
            if (screenshot.Channels() == 4)
                Cv2.CvtColor(screenshot, gray, ColorConversionCodes.BGRA2GRAY);
            else if (screenshot.Channels() == 3)
                Cv2.CvtColor(screenshot, gray, ColorConversionCodes.BGR2GRAY);
            else
                screenshot.CopyTo(gray);

            // 预生成 HSV 供墨迹验证使用（校准阶段一次性开销）
            using var hsv = new Mat();
            if (screenshot.Channels() == 4)
            {
                using var bgr = new Mat();
                Cv2.CvtColor(screenshot, bgr, ColorConversionCodes.BGRA2BGR);
                Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            }
            else if (screenshot.Channels() == 3)
            {
                Cv2.CvtColor(screenshot, hsv, ColorConversionCodes.BGR2HSV);
            }

            using var blurred = new Mat();
            Cv2.GaussianBlur(gray, blurred, new OpenCvSharp.Size(3, 3), 0);
            using var edges = new Mat();
            Cv2.Canny(blurred, edges, 35, 110);

            int minDim = Math.Min(gray.Width, gray.Height);
            var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, 35,
                minLineLength: Math.Max(35, minDim * 0.12),
                maxLineGap: 20);

            if (lines.Length >= 8)
            {
                var hLines = new List<GridLine>();
                var vLines = new List<GridLine>();

                // 排除顶部6%（头像UI）与底部10%（技能栏UI）的干扰水平线
                double topIgnore = gray.Height * 0.06;
                double botIgnore = gray.Height * 0.90;

                foreach (var l in lines)
                {
                    double dx = Math.Abs(l.P2.X - l.P1.X);
                    double dy = Math.Abs(l.P2.Y - l.P1.Y);
                    double midY = (l.P1.Y + l.P2.Y) / 2.0;

                    // 水平线：横向跨度显著大于纵向，且在棋盘合理垂直区间内
                    if (dx > dy * 2.8 && dx >= minDim * 0.12)
                    {
                        if (midY >= topIgnore && midY <= botIgnore)
                        {
                            hLines.Add(new GridLine(midY, dx));
                        }
                    }
                    // 垂直线：纵向跨度显著大于横向
                    else if (dy > dx * 2.8 && dy >= minDim * 0.12)
                    {
                        vLines.Add(new GridLine((l.P1.X + l.P2.X) / 2.0, dy));
                    }
                }

                var groupedH = GroupLines(hLines);
                var groupedV = GroupLines(vLines);

                double minSpacing = Math.Max(18.0, minDim / 35.0);
                double maxSpacing = Math.Max(minSpacing + 5.0, minDim / 7.5);

                var best = FindBestJointGrid(groupedV, groupedH, screenshot, hsv, gray.Width, gray.Height, minSpacing, maxSpacing);

                if (best != null)
                {
                    var (bestX, bestY) = best.Value;

                    // 全盘墨迹精修：对原点与间距做局部爬山虎搜索，
                    // 把网格精确吸附到 90 个交叉点上，消除半格级偏差
                    (bestX, bestY) = RefineGrid(bestX, bestY, screenshot, hsv);

                    calibration.TopLeft = new ScreenPoint(bestX[0], bestY[0]);
                    calibration.TopRight = new ScreenPoint(bestX[8], bestY[0]);
                    calibration.BottomLeft = new ScreenPoint(bestX[0], bestY[9]);
                    calibration.BottomRight = new ScreenPoint(bestX[8], bestY[9]);
                    calibration.IsValid = true;
                    calibration.Timestamp = DateTime.Now;
                    return calibration;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"棋盘自动检测失败: {ex.Message}");
        }

        calibration.IsValid = false;
        return calibration;
    }

    private static List<GridLine> GroupLines(List<GridLine> lines)
    {
        if (lines.Count == 0) return new List<GridLine>();
        var sorted = lines.OrderBy(l => l.Position).ToList();
        var result = new List<GridLine>();
        var cur = new List<GridLine> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            // 与分组起始位置对比，避免相邻线段递增导致的雪崩串联
            if (Math.Abs(sorted[i].Position - cur[0].Position) <= 5.0)
            {
                cur.Add(sorted[i]);
            }
            else
            {
                result.Add(new GridLine(cur.Sum(l => l.Position * l.Length) / cur.Sum(l => l.Length), cur.Sum(l => l.Length)));
                cur = new List<GridLine> { sorted[i] };
            }
        }
        result.Add(new GridLine(cur.Sum(l => l.Position * l.Length) / cur.Sum(l => l.Length), cur.Sum(l => l.Length)));
        return result;
    }

    /// <summary>
    /// 联合搜索 9 列与 10 行的最佳网格，结合棋盘长宽比约束与关键位棋子墨迹验证
    /// </summary>
    private static (double[] bestX, double[] bestY)? FindBestJointGrid(
        List<GridLine> vLines, List<GridLine> hLines,
        Mat screenshot, Mat hsv,
        int width, int height,
        double minSpacing, double maxSpacing)
    {
        var candX = GetCandidateGrids(vLines, 9, width, minSpacing, maxSpacing);
        var candY = GetCandidateGrids(hLines, 10, height, minSpacing, maxSpacing);

        if (candX.Count == 0 || candY.Count == 0)
            return null;

        double bestScore = -1e9;
        (double[], double[])? bestPair = null;

        // 联合评估：象棋网格 8 个横间隔和 9 个纵间隔，单元格接近正方形
        foreach (var gx in candX)
        {
            double spX = (gx[8] - gx[0]) / 8.0;

            foreach (var gy in candY)
            {
                double spY = (gy[9] - gy[0]) / 9.0;

                // 单元格宽高比约束 (dx/dy 应在 0.84 ~ 1.16 之间)
                double ratio = spX / spY;
                if (ratio < 0.84 || ratio > 1.16)
                    continue;

                // 棋盘总宽高比约束 (8*spX / 9*spY 应在 0.76 ~ 1.02 之间)
                double boardW = gx[8] - gx[0];
                double boardH = gy[9] - gy[0];
                double totalRatio = boardW / boardH;
                if (totalRatio < 0.76 || totalRatio > 1.02)
                    continue;

                // 垂直位置约束：棋盘顶部与底部的合理边界
                if (gy[0] < height * 0.08 || gy[9] > height * 0.92)
                    continue;

                // 棋盘必须有足够宽度（至少占窗口宽度的 45%）
                if (boardW < width * 0.45)
                    continue;

                // 综合打分：线段匹配分 + 精确 HSV 墨迹命中分
                double score = EvaluateGridScore(gx, gy, vLines, hLines, screenshot, hsv, spX * 0.4);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPair = (gx, gy);
                }
            }
        }

        return bestPair;
    }

    /// <summary>
    /// 全盘墨迹精修：以初始网格为中心，对水平/垂直原点与间距做局部网格搜索，
    /// 最大化 90 个交叉点上的棋子墨迹命中。彻底消除 Hough 线定位带来的
    /// 半格级偏差（半格偏差 = 整颗棋子丢失或采到邻格导致认错字）。
    /// </summary>
    private static (double[] gx, double[] gy) RefineGrid(double[] gx0, double[] gy0, Mat img, Mat hsv)
    {
        double spX0 = (gx0[8] - gx0[0]) / 8.0;
        double spY0 = (gy0[9] - gy0[0]) / 9.0;
        if (spX0 <= 0 || spY0 <= 0)
            return (gx0, gy0);

        double bestScore = FullGridInkScore(gx0, gy0, img, hsv);
        double bestOx = gx0[0], bestOy = gy0[0];
        double bestSx = spX0, bestSy = spY0;

        // 原点平移最大 ±0.12 格、间距缩放最大 ±3%，覆盖典型 Hough 误差
        double[] shiftFracs = { 0, -0.04, 0.04, -0.08, 0.08, -0.12, 0.12 };
        double[] scaleFracs = { 0, -0.015, 0.015, -0.03, 0.03 };

        foreach (var dSx in scaleFracs)
        {
            foreach (var dSy in scaleFracs)
            {
                double sx = spX0 * (1 + dSx);
                double sy = spY0 * (1 + dSy);

                foreach (var dOx in shiftFracs)
                {
                    double ox = gx0[0] + dOx * spX0;

                    foreach (var dOy in shiftFracs)
                    {
                        double oy = gy0[0] + dOy * spY0;

                        var gx = BuildGrid(ox, sx, 9);
                        var gy = BuildGrid(oy, sy, 10);
                        if (gx[8] >= img.Width || gy[9] >= img.Height || gx[0] < 2 || gy[0] < 2)
                            continue;

                        double score = FullGridInkScore(gx, gy, img, hsv);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestOx = ox; bestOy = oy;
                            bestSx = sx; bestSy = sy;
                        }
                    }
                }
            }
        }

        return (BuildGrid(bestOx, bestSx, 9), BuildGrid(bestOy, bestSy, 10));
    }

    private static double[] BuildGrid(double origin, double spacing, int count)
    {
        var grid = new double[count];
        for (int i = 0; i < count; i++)
            grid[i] = origin + i * spacing;
        return grid;
    }

    /// <summary>
    /// 全部 90 个交叉点的墨迹命中评分：命中数为主、墨迹强度为辅
    /// </summary>
    private static double FullGridInkScore(double[] gx, double[] gy, Mat img, Mat hsv)
    {
        int hits = 0;
        double strength = 0;

        for (int r = 0; r < 10; r++)
        {
            int py = (int)Math.Round(gy[r]);
            if (py < 3 || py >= img.Height - 3)
                continue;

            for (int c = 0; c < 9; c++)
            {
                int px = (int)Math.Round(gx[c]);
                if (px < 3 || px >= img.Width - 3)
                    continue;

                int red = 0, black = 0;
                for (int dy = -3; dy <= 3; dy += 2)
                {
                    int sy = py + dy;
                    for (int dx = -3; dx <= 3; dx += 2)
                    {
                        int sx = px + dx;
                        var vec = img.Get<Vec3b>(sy, sx);
                        byte b = vec.Item0, g = vec.Item1, rVal = vec.Item2;

                        var hsvPix = !hsv.Empty() ? hsv.Get<Vec3b>(sy, sx) : new Vec3b(0, 0, 0);
                        byte hHue = hsvPix.Item0, sSat = hsvPix.Item1, vVal = hsvPix.Item2;

                        bool isRed = (hHue <= 8 || hHue >= 172) && sSat >= 60 && vVal >= 70 && (rVal - Math.Max(g, b) >= 30);
                        bool isBlack = vVal <= 105 && (rVal < 98 && g < 92 && b < 82);

                        if (isRed) red++;
                        else if (isBlack) black++;
                    }
                }

                if (red >= 3 || black >= 3)
                {
                    hits++;
                    strength += Math.Max(red, black);
                }
            }
        }

        return hits * 100.0 + strength;
    }

    private static List<double[]> GetCandidateGrids(List<GridLine> lines, int count, int dimension, double minSpacing, double maxSpacing)
    {
        var candidates = new List<(double[] grid, int matches)>();
        if (lines.Count < 2) return new List<double[]>();

        // 仅保留长度显著的前 25 条线段，过滤杂散碎线
        var significantLines = lines.OrderByDescending(l => l.Length).Take(25).OrderBy(l => l.Position).ToList();
        var seen = new List<(double origin, double spacing)>();

        for (int i = 0; i < significantLines.Count; i++)
        {
            for (int j = i + 1; j < significantLines.Count; j++)
            {
                double dist = significantLines[j].Position - significantLines[i].Position;
                for (int k = 1; k < count; k++)
                {
                    double spacing = dist / k;
                    if (spacing < minSpacing || spacing > maxSpacing) continue;

                    for (int offset = 0; offset <= k; offset++)
                    {
                        double origin = significantLines[i].Position - offset * spacing;
                        double end = origin + (count - 1) * spacing;
                        if (origin < 0 || end > dimension) continue;

                        // 快速去重：如果已有相近原点与间距的候选，直接跳过
                        if (seen.Any(s => Math.Abs(s.origin - origin) <= 3.0 && Math.Abs(s.spacing - spacing) <= 1.0))
                            continue;

                        int matches = 0;
                        var grid = new double[count];
                        for (int idx = 0; idx < count; idx++)
                        {
                            double expected = origin + idx * spacing;
                            grid[idx] = expected;
                            var nearest = lines.MinBy(l => Math.Abs(l.Position - expected));
                            if (Math.Abs(nearest.Position - expected) <= Math.Max(5.0, spacing * 0.15))
                            {
                                matches++;
                            }
                        }

                        if (matches >= (int)Math.Ceiling(count * 0.5))
                        {
                            seen.Add((origin, spacing));
                            candidates.Add((grid, matches));
                        }
                    }
                }
            }
        }

        // 按线条匹配数降序排序，取前 35 组最优质候选
        return candidates
            .OrderByDescending(c => c.matches)
            .Take(35)
            .Select(c => c.grid)
            .ToList();
    }

    /// <summary>
    /// 评估网格优劣：包含线条拟合度与关键落子点的墨迹特征验证
    /// </summary>
    private static double EvaluateGridScore(double[] gx, double[] gy, List<GridLine> vLines, List<GridLine> hLines, Mat img, Mat hsv, double radius)
    {
        double lineScore = 0;
        foreach (var x in gx)
        {
            var near = vLines.MinBy(l => Math.Abs(l.Position - x));
            double err = Math.Abs(near.Position - x);
            if (err <= 6.0) lineScore += 15.0 - err;
        }

        foreach (var y in gy)
        {
            var near = hLines.MinBy(l => Math.Abs(l.Position - y));
            double err = Math.Abs(near.Position - y);
            if (err <= 6.0) lineScore += 15.0 - err;
        }

        // 验证关键交叉点上的棋子墨迹（Row 0, 3, 6, 9）
        int[] checkRows = { 0, 3, 6, 9 };
        int inkHit = 0;

        for (int ri = 0; ri < checkRows.Length; ri++)
        {
            int r = checkRows[ri];
            int py = (int)gy[r];
            if (py < 0 || py >= img.Height) continue;

            for (int c = 0; c < 9; c++)
            {
                int px = (int)gx[c];
                if (px < 0 || px >= img.Width) continue;

                // 快速采样中心 7x7 像素是否有红/黑真实文字墨迹（基于 HSV 排除黄色木纹底盘）
                int red = 0, black = 0;
                for (int dy = -3; dy <= 3; dy += 2)
                {
                    int sy = Math.Clamp(py + dy, 0, img.Height - 1);
                    for (int dx = -3; dx <= 3; dx += 2)
                    {
                        int sx = Math.Clamp(px + dx, 0, img.Width - 1);
                        var vec = img.Get<Vec3b>(sy, sx);
                        byte b = vec.Item0, g = vec.Item1, rVal = vec.Item2;

                        var hsvPix = !hsv.Empty() ? hsv.Get<Vec3b>(sy, sx) : new Vec3b(0, 0, 0);
                        byte hHue = hsvPix.Item0, sSat = hsvPix.Item1, vVal = hsvPix.Item2;

                        // 真实红墨水特征
                        bool isRed = (hHue <= 8 || hHue >= 172) && sSat >= 60 && vVal >= 70 && (rVal - Math.Max(g, b) >= 30);
                        // 真实黑墨水特征
                        bool isBlack = vVal <= 105 && (rVal < 98 && g < 92 && b < 82);

                        if (isRed) red++;
                        else if (isBlack) black++;
                    }
                }
                if (red >= 3 || black >= 3)
                    inkHit++;
            }
        }

        // 墨迹命中给极高加权，确保网格绝不上下错行或左右横移
        return lineScore + inkHit * 25.0;
    }

    public BoardCalibration ManualCalibrate(ScreenPoint tl, ScreenPoint tr, ScreenPoint bl, ScreenPoint br)
    {
        return new BoardCalibration
        {
            TopLeft = tl,
            TopRight = tr,
            BottomLeft = bl,
            BottomRight = br,
            IsValid = true,
            Timestamp = DateTime.Now
        };
    }

    public void Dispose()
    {
        if (!_disposed) _disposed = true;
    }
}
