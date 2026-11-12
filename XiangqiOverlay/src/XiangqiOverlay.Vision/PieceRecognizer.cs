using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using OpenCvSharp;
using XiangqiOverlay.Core;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace XiangqiOverlay.Vision;

/// <summary>
/// 高精度棋子识别器
/// 结合色彩特征、字心吸附、模板匹配与象棋规则先验约束
/// </summary>
public sealed class PieceRecognizer : IDisposable
{
    private bool _disposed;
    private const int TemplateSize = 40;

    // 内置标准字模字典 (key: "R_King", "B_Advisor" 等)
    private readonly Dictionary<string, List<Mat>> _builtinTemplates = new();

    // 当前对局动态自适应字模字典
    private readonly Dictionary<string, Mat> _adaptiveTemplates = new();

    // 游戏实截贴图库：直接从截图裁下的完整棋子外观（灰度 SpriteSize 方图）。
    // 游戏美术字与渲染字模的差异、高亮光晕下的字形失真，都在"贴图 vs 贴图"的
    // 直接比对中消失。开局锚定阶段按位置真值学习，之后只从"原始识别与追踪
    // 结果一致"的高置信格子补充，绝不覆盖（防自我强化污染）。
    private readonly Dictionary<string, List<Mat>> _spriteTemplates = new();
    private const int SpriteSize = 48;
    private const int MaxSpritesPerKey = 4;

    // 最近一帧各格原始分类的学习置信度 / 第一名优势（用于自愈与贴图库补充的资格判定）。
    // NaN = 本局尚未分类过该格（未知）；0 = 分类器自己判定"低置信、无明显优势"——
    // 两者含义完全不同，0 必须拦截，绝不能当"未知"放行。
    private readonly double[,] _lastLearnScores = new double[BoardState.Cols, BoardState.Rows];
    private readonly double[,] _lastTypeMargins = new double[BoardState.Cols, BoardState.Rows];

    // 本帧检测到的高亮格（游戏"上一步落点"标记，如 JJ象棋绿色光环）
    private readonly List<BoardPoint> _highlightedCells = new();

    /// <summary>本帧检测到的高亮格（每次识别刷新）</summary>
    public IReadOnlyList<BoardPoint> LastHighlightedCells => _highlightedCells;

    /// <summary>最近一帧的原始识别盘面（未经锚定/追踪，诊断用）</summary>
    public BoardState LastRawBoard { get; private set; } = new();

    /// <summary>当前实截贴图库样本总数（诊断用）</summary>
    public int SpriteCount
    {
        get
        {
            lock (_spriteTemplates)
                return _spriteTemplates.Values.Sum(l => l.Count);
        }
    }

    // 屏幕方向是否翻转（执黑时游戏翻转棋盘：红上黑下），在检测到完整开局阵列时锁定
    private bool _flipped;

    // 方向探测已放弃（中局启动且始终未见开局阵列时停止无谓的逐帧探测）
    private bool _orientationSearchDone;
    private int _orientationFrames;

    /// <summary>屏幕方向是否翻转（红方在上方）</summary>
    public bool Flipped => _flipped;

    /// <summary>
    /// 重置方向锁与新对局探测状态（换窗口校准或检测到新对局时调用）。
    /// 注意：不清空实截贴图库——同一游戏换局后贴图依然有效，
    /// 只有更换目标窗口/校准时才通过 ResetSprites 重建。
    /// </summary>
    public void ResetOrientation()
    {
        _flipped = false;
        _orientationSearchDone = false;
        _orientationFrames = 0;
        ResetCellEvidence();
    }

    /// <summary>把各格分类证据重置为"未知"（NaN / Empty）</summary>
    private void ResetCellEvidence()
    {
        for (int c = 0; c < BoardState.Cols; c++)
        {
            for (int r = 0; r < BoardState.Rows; r++)
            {
                _lastLearnScores[c, r] = double.NaN;
                _lastTypeMargins[c, r] = double.NaN;
            }
        }
    }

    /// <summary>
    /// 清空实截贴图库（更换目标窗口或重新校准时调用，可能是另一个游戏）
    /// </summary>
    public void ResetSprites()
    {
        lock (_spriteTemplates)
        {
            foreach (var list in _spriteTemplates.Values)
            {
                foreach (var mat in list)
                    mat.Dispose();
            }
            _spriteTemplates.Clear();
        }
    }

    public PieceRecognizer()
    {
        ResetCellEvidence();
        GenerateBuiltinTemplates();
    }

    /// <summary>
    /// 生成标准字体模板库（针对Windows自带楷体、仿宋、黑体）
    /// </summary>
    private void GenerateBuiltinTemplates()
    {
        var redMapping = new Dictionary<PieceType, string[]>
        {
            [PieceType.King] = new[] { "帥", "帅" },
            [PieceType.Advisor] = new[] { "仕" },
            [PieceType.Elephant] = new[] { "相" },
            [PieceType.Horse] = new[] { "傌", "马" },
            [PieceType.Chariot] = new[] { "俥", "车" },
            [PieceType.Cannon] = new[] { "炮", "砲" },
            [PieceType.Pawn] = new[] { "兵" }
        };

        var blackMapping = new Dictionary<PieceType, string[]>
        {
            [PieceType.King] = new[] { "將", "将" },
            [PieceType.Advisor] = new[] { "士" },
            [PieceType.Elephant] = new[] { "象" },
            [PieceType.Horse] = new[] { "馬", "马" },
            [PieceType.Chariot] = new[] { "車", "车" },
            [PieceType.Cannon] = new[] { "砲", "炮" },
            [PieceType.Pawn] = new[] { "卒" }
        };

        string[] fontFamilies = { "KaiTi", "STKaiti", "STXingkai", "Microsoft YaHei", "SimSun", "SimHei", "LiSu" };

        foreach (var (type, chars) in redMapping)
        {
            string key = $"R_{type}";
            _builtinTemplates[key] = new List<Mat>();
            foreach (var ch in chars)
            {
                foreach (var fontName in fontFamilies)
                {
                    var mat = RenderCharToMat(ch, fontName, TemplateSize);
                    if (mat != null)
                        _builtinTemplates[key].Add(mat);
                }
            }
        }

        foreach (var (type, chars) in blackMapping)
        {
            string key = $"B_{type}";
            _builtinTemplates[key] = new List<Mat>();
            foreach (var ch in chars)
            {
                foreach (var fontName in fontFamilies)
                {
                    var mat = RenderCharToMat(ch, fontName, TemplateSize);
                    if (mat != null)
                        _builtinTemplates[key].Add(mat);
                }
            }
        }
    }

    private static Mat? RenderCharToMat(string ch, string fontName, int size)
    {
        try
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using var font = new Font(fontName, size * 0.72f, FontStyle.Bold, GraphicsUnit.Pixel);
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(ch, font, System.Drawing.Brushes.White, new RectangleF(0, 0, size, size), sf);
            }

            // 转换成 OpenCV 灰度单通道二值图
            var bmpData = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                using var bgra = Mat.FromPixelData(size, size, MatType.CV_8UC4, bmpData.Scan0, bmpData.Stride);
                using var gray = new Mat();
                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                using var bin = new Mat();
                Cv2.Threshold(gray, bin, 50, 255, ThresholdTypes.Binary);
                // 紧凑外接矩形居中归一化，彻底消除 GDI+ 字体偏上带来的错位
                return CenterAndNormalize(bin, size);
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将字模前景白色像素的外接矩形居中等比例缩放到目标尺寸画布
    /// </summary>
    public static Mat CenterAndNormalize(Mat input, int targetSize = TemplateSize)
    {
        var pts = new List<Point>();
        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                if (input.Get<byte>(y, x) > 100)
                    pts.Add(new Point(x, y));
            }
        }

        if (pts.Count < 8)
            return input.Clone();

        var rect = Cv2.BoundingRect(pts.ToArray());
        using var roi = new Mat(input, rect);

        double maxDim = Math.Max(rect.Width, rect.Height);
        double scale = (targetSize * 0.78) / maxDim;
        int newW = Math.Max(1, (int)(rect.Width * scale));
        int newH = Math.Max(1, (int)(rect.Height * scale));

        using var resized = new Mat();
        Cv2.Resize(roi, resized, new Size(newW, newH), 0, 0, InterpolationFlags.Area);

        var output = new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.All(0));
        int ox = (targetSize - newW) / 2;
        int oy = (targetSize - newH) / 2;

        using var outRoi = new Mat(output, new Rect(ox, oy, newW, newH));
        resized.CopyTo(outRoi);
        return output;
    }

    /// <summary>
    /// 识别棋盘上所有棋子（支持基于历史盘面追踪继承类型）
    /// </summary>
    public BoardState Recognize(Mat screenshot, BoardCalibration calibration, BoardState? previousBoard = null)
    {
        var raw = RecognizeRaw(screenshot, calibration);
        LastRawBoard = raw.Clone();
        var tracked = TrackBoard(previousBoard, raw, _highlightedCells);
        tracked.CurrentPlayer = previousBoard?.CurrentPlayer ?? tracked.CurrentPlayer;
        return tracked;
    }

    /// <summary>
    /// 原始视觉检测棋盘上所有棋子
    /// </summary>
    public BoardState RecognizeRaw(Mat screenshot, BoardCalibration calibration)
    {
        // 开局阶段自动检测屏幕方向（红上=翻转），锁定后整局使用同一映射
        if (!_flipped && !_orientationSearchDone && calibration.IsValid)
        {
            if (DetectFlippedOpening(screenshot, calibration))
            {
                _flipped = true;
                _orientationSearchDone = true;
                calibration.Flipped = true;
            }
            else if (++_orientationFrames > 40)
            {
                // 长时间未见开局阵列（中局启动），停止逐帧方向探测
                _orientationSearchDone = true;
            }
        }

        var board = new BoardState();

        if (!calibration.IsValid || screenshot.Empty())
            return board;

        double radius = calibration.GetCellRadius();
        if (radius < 8)
            return board;

        // 全帧 HSV 供高亮格检测使用（游戏"上一步落点"的绿色光环）
        _highlightedCells.Clear();
        using var hsvFull = new Mat();
        if (screenshot.Channels() == 4)
        {
            using var bgrFull = new Mat();
            Cv2.CvtColor(screenshot, bgrFull, ColorConversionCodes.BGRA2BGR);
            Cv2.CvtColor(bgrFull, hsvFull, ColorConversionCodes.BGR2HSV);
        }
        else if (screenshot.Channels() == 3)
        {
            Cv2.CvtColor(screenshot, hsvFull, ColorConversionCodes.BGR2HSV);
        }

        for (int c = 0; c < BoardState.Cols; c++)
        {
            for (int r = 0; r < BoardState.Rows; r++)
            {
                var screenPos = calibration.BoardToScreen(c, r);
                var cell = RecognizeCell(screenshot, screenPos, radius, c, r);

                if (cell != null && !cell.IsEmpty)
                    board[c, r] = cell;

                if (!hsvFull.Empty() && IsCellHighlighted(hsvFull, screenPos, radius))
                    _highlightedCells.Add(new BoardPoint(c, r));
            }
        }

        // 象棋走法高亮至多 1~2 个（起点圆点 + 终点光圈）。若超过 3 个，必为全盘草坪背景等环境误报，全部清空
        if (_highlightedCells.Count > 3)
            _highlightedCells.Clear();

        // 如果识别到接近开局阵列，自动将高置信度字模收录到自适应库中
        TryLearnAdaptiveTemplates(screenshot, calibration, board);

        // 原始视觉观察不执行兵种降级或数量整型；无效盘面由提交层拒绝。
        return board;
    }

    /// <summary>
    /// 检测屏幕方向：对比"红方在底部"与"红方在顶部（翻转）"两种假设下，
    /// 初始阵列位置的颜色吻合数。颜色识别远比字形分类可靠，
    /// 即使美术字认错类型也不影响该判定。
    /// 仅在开局阵型较完整（≥20 颗吻合）时才给出方向结论，避免中局误翻转。
    /// </summary>
    private bool DetectFlippedOpening(Mat screenshot, BoardCalibration calibration)
    {
        try
        {
            var initial = BoardState.InitialPosition();
            double radius = calibration.GetCellRadius();
            if (radius < 8)
                return false;

            var normalCal = calibration.WithFlipped(false);
            var flippedCal = calibration.WithFlipped(true);
            int normalMatch = 0;
            int flippedMatch = 0;

            foreach (var (pt, _, pColor) in initial.GetAllPieces())
            {
                var normalCell = DetectCellColorOnly(screenshot, normalCal.BoardToScreen(pt.Col, pt.Row), radius);
                if (normalCell.Color == pColor) normalMatch++;

                var flippedCell = DetectCellColorOnly(screenshot, flippedCal.BoardToScreen(pt.Col, pt.Row), radius);
                if (flippedCell.Color == pColor) flippedMatch++;
            }

            // 翻转阵列明显更完整且优于正常方向，才判定为翻转（红上黑下）
            return flippedMatch >= 20 && flippedMatch > normalMatch;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 只判断某交叉点是否有棋子及其颜色（不分类兵种），用于方向检测
    /// </summary>
    private static BoardCell DetectCellColorOnly(Mat screenshot, ScreenPoint center, double radius)
    {
        try
        {
            int x = (int)Math.Max(0, center.X - radius);
            int y = (int)Math.Max(0, center.Y - radius);
            int w = (int)Math.Min(screenshot.Width - x, radius * 2);
            int h = (int)Math.Min(screenshot.Height - y, radius * 2);
            if (w <= 12 || h <= 12)
                return new BoardCell();

            using var roi = new Mat(screenshot, new OpenCvSharp.Rect(x, y, w, h));
            using var bgr = new Mat();
            if (roi.Channels() == 4) Cv2.CvtColor(roi, bgr, ColorConversionCodes.BGRA2BGR);
            else if (roi.Channels() == 1) Cv2.CvtColor(roi, bgr, ColorConversionCodes.GRAY2BGR);
            else roi.CopyTo(bgr);

            var (color, mask) = DetectPieceColorAndMask(bgr, radius);
            mask?.Dispose();
            return color == PieceColor.Unknown
                ? new BoardCell()
                : new BoardCell { Type = PieceType.Pawn, Color = color };
        }
        catch
        {
            return new BoardCell();
        }
    }

    /// <summary>
    /// 识别单个交叉点的棋子
    /// </summary>
    private BoardCell RecognizeCell(Mat screenshot, ScreenPoint center, double radius, int col, int row)
    {
        try
        {
            // 棋子自适应搜索微调：在交叉点微小邻域内吸附到最明显的墨迹字心
            var adjustedCenter = FindBestPieceCenter(screenshot, center, radius * 0.25);

            int x = (int)Math.Max(0, adjustedCenter.X - radius);
            int y = (int)Math.Max(0, adjustedCenter.Y - radius);
            int w = (int)Math.Min(screenshot.Width - x, radius * 2);
            int h = (int)Math.Min(screenshot.Height - y, radius * 2);

            if (w <= 12 || h <= 12)
                return new BoardCell();

            var rect = new OpenCvSharp.Rect(x, y, w, h);
            using var roi = new Mat(screenshot, rect);
            if (roi.Empty())
                return new BoardCell();

            using var roiBgr = new Mat();
            if (roi.Channels() == 4)
                Cv2.CvtColor(roi, roiBgr, ColorConversionCodes.BGRA2BGR);
            else if (roi.Channels() == 1)
                Cv2.CvtColor(roi, roiBgr, ColorConversionCodes.GRAY2BGR);
            else
                roi.CopyTo(roiBgr);

            // 检测是否有棋子以及阵营颜色
            var (color, mask) = DetectPieceColorAndMask(roiBgr, radius);
            if (color == PieceColor.Unknown || mask == null || mask.Empty())
            {
                mask?.Dispose();
                return new BoardCell { Type = PieceType.Empty, Color = PieceColor.Unknown };
            }

            // 使用模板匹配与规则约束分类具体棋子类型
            using (mask)
            {
                var (type, learnScore, typeMargin) = ClassifyPieceWithTemplates(mask, roiBgr, color, col, row);
                _lastLearnScores[col, row] = learnScore;
                _lastTypeMargins[col, row] = typeMargin;

                return new BoardCell { Type = type, Color = color };
            }
        }
        catch
        {
            return new BoardCell { Type = PieceType.Empty, Color = PieceColor.Unknown };
        }
    }

    /// <summary>
    /// 微调寻找棋子真实中心
    /// </summary>
    private static ScreenPoint FindBestPieceCenter(Mat screenshot, ScreenPoint nominal, double searchDist)
    {
        int sx = (int)nominal.X;
        int sy = (int)nominal.Y;
        int range = (int)Math.Max(2, searchDist);

        int bestX = sx, bestY = sy;
        double maxInk = -1;

        int step = Math.Max(1, range / 3);
        using var hsv = new Mat();
        Cv2.CvtColor(screenshot, hsv, ColorConversionCodes.BGR2HSV);

        for (int dy = -range; dy <= range; dy += step)
        {
            for (int dx = -range; dx <= range; dx += step)
            {
                int px = sx + dx;
                int py = sy + dy;
                if (px < 5 || py < 5 || px >= screenshot.Width - 5 || py >= screenshot.Height - 5)
                    continue;

                double inkScore = 0;
                for (int cy = -2; cy <= 2; cy++)
                {
                    for (int cx = -2; cx <= 2; cx++)
                    {
                        var vec = screenshot.Get<Vec3b>(py + cy, px + cx);
                        byte b = vec.Item0, g = vec.Item1, r = vec.Item2;
                        var hsvPix = hsv.Get<Vec3b>(py + cy, px + cx);
                        byte h = hsvPix.Item0, s = hsvPix.Item1, v = hsvPix.Item2;

                        // 真实红字特征（色相必须集中在正红 0~8 或 172~180）
                        if ((h <= 8 || h >= 172) && s >= 65 && v >= 75 && (r - Math.Max(g, b) >= 30))
                            inkScore += (r - Math.Max(g, b));
                        // 真实深色墨迹特征（低明度）
                        else if (v <= 100 && (r < 95 && g < 90 && b < 80))
                            inkScore += (100 - v);
                    }
                }

                if (inkScore > maxInk)
                {
                    maxInk = inkScore;
                    bestX = px;
                    bestY = py;
                }
            }
        }

        return new ScreenPoint(bestX, bestY);
    }

    /// <summary>
    /// 检测棋子颜色并提取文字二值化掩膜（基于精准 HSV 色相与明度）
    /// </summary>
    private static (PieceColor color, Mat? charMask) DetectPieceColorAndMask(Mat bgr, double radius)
    {
        int w = bgr.Width;
        int h = bgr.Height;
        int cx = w / 2;
        int cy = h / 2;
        int innerRadius = (int)(radius * 0.58);
        int innerRadiusSq = innerRadius * innerRadius;

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        using var redMask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        using var blackMask = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));

        int redCount = 0;
        int blackCount = 0;
        int totalInner = 0;
        double redSumX = 0, redSumY = 0, blackSumX = 0, blackSumY = 0;

        for (int y = Math.Max(0, cy - innerRadius); y <= Math.Min(h - 1, cy + innerRadius); y++)
        {
            for (int x = Math.Max(0, cx - innerRadius); x <= Math.Min(w - 1, cx + innerRadius); x++)
            {
                int dx = x - cx;
                int dy = y - cy;
                if (dx * dx + dy * dy > innerRadiusSq)
                    continue;

                totalInner++;
                var vec = bgr.Get<Vec3b>(y, x);
                byte b = vec.Item0, g = vec.Item1, r = vec.Item2;

                var hsvPix = hsv.Get<Vec3b>(y, x);
                byte hHue = hsvPix.Item0, sSat = hsvPix.Item1, vVal = hsvPix.Item2;

                // 红色文字特征：H 色相在正红区间 [0, 8] 或 [172, 180]，高饱和度且 R 显著高于 G 和 B
                bool isRed = (hHue <= 8 || hHue >= 172) && sSat >= 60 && vVal >= 70 && (r - Math.Max(g, b) >= 30);

                // 黑色文字特征：暗色墨迹（排除黄色木纹，木纹的 V 通常在 150 以上且饱和度较高）
                bool isBlack = vVal <= 105 && (r < 98 && g < 92 && b < 82);

                if (isRed)
                {
                    redCount++;
                    redSumX += x;
                    redSumY += y;
                    redMask.Set(y, x, (byte)255);
                }
                else if (isBlack)
                {
                    blackCount++;
                    blackSumX += x;
                    blackSumY += y;
                    blackMask.Set(y, x, (byte)255);
                }
            }
        }

        if (totalInner < 16)
            return (PieceColor.Unknown, null);

        double redRatio = (double)redCount / totalInner;
        double blackRatio = (double)blackCount / totalInner;

        // 真实棋子文字墨迹在内圈通常占据 30%~65% 的面积
        // 此处要求墨迹点数至少 25 像素且占比 >= 14%，避免棋盘标线数字（如“八”字仅 18 像素/7.1%）或网格交界线阴影误报为棋子
        if (redCount >= 25 && redRatio >= 0.14 && redRatio > blackRatio * 1.5)
        {
            // 墨迹质心必须靠近格心：相邻棋子的圆盘边缘侵入本格 ROI 时，
            // 质心会明显偏离格心——这是"幽灵子"的主要来源，直接判空
            if (!InkCentroidNearCenter(redSumX, redSumY, redCount, cx, cy, radius))
                return (PieceColor.Unknown, null);

            var normalized = CropAndNormalizeMask(redMask, cx, cy, innerRadius);
            return (PieceColor.Red, normalized);
        }
        else if (blackCount >= 25 && blackRatio >= 0.14 && blackRatio > redRatio * 1.5)
        {
            if (!InkCentroidNearCenter(blackSumX, blackSumY, blackCount, cx, cy, radius))
                return (PieceColor.Unknown, null);

            var normalized = CropAndNormalizeMask(blackMask, cx, cy, innerRadius);
            return (PieceColor.Black, normalized);
        }

        return (PieceColor.Unknown, null);
    }

    /// <summary>
    /// 墨迹质心校验：棋子字心必然在格心附近（ROI 已由字心吸附对齐），
    /// 质心偏离超过 0.35 格半径说明检测到的是邻格棋子的边缘侵入或远处干扰
    /// </summary>
    private static bool InkCentroidNearCenter(double sumX, double sumY, int count, int cx, int cy, double radius)
    {
        if (count <= 0)
            return false;
        double mx = sumX / count - cx;
        double my = sumY / count - cy;
        double maxDist = radius * 0.35;
        return mx * mx + my * my <= maxDist * maxDist;
    }

    /// <summary>
    /// 将提取出的文字掩膜裁剪居中并归一化为 TemplateSize x TemplateSize
    /// </summary>
    private static Mat CropAndNormalizeMask(Mat rawMask, int cx, int cy, int innerRadius)
    {
        int x = Math.Max(0, cx - innerRadius);
        int y = Math.Max(0, cy - innerRadius);
        int w = Math.Min(innerRadius * 2, rawMask.Width - x);
        int h = Math.Min(innerRadius * 2, rawMask.Height - y);

        using var roi = new Mat(rawMask, new OpenCvSharp.Rect(x, y, w, h));
        using var resized = new Mat();
        Cv2.Resize(roi, resized, new Size(TemplateSize, TemplateSize), 0, 0, InterpolationFlags.Area);

        using var bin = new Mat();
        Cv2.Threshold(resized, bin, 80, 255, ThresholdTypes.Binary);
        // 使用紧凑外接矩形居中等比缩放，确保与模板字模完全对齐
        return CenterAndNormalize(bin, TemplateSize);
    }

    /// <summary>
    /// 结合象棋规则约束、渲染字模与游戏实截贴图进行分类。
    /// 返回 (棋子类型, 学习置信度)：学习置信度取获胜类型在自适应库/内置库/贴图库中
    /// 的最高原始匹配分（不含加权），用于筛选可反哺模板库的高置信样本。
    /// </summary>
    private (PieceType type, double learnScore, double typeMargin) ClassifyPieceWithTemplates(Mat charMask, Mat roiBgr, PieceColor color, int col, int row)
    {
        var candidateTypes = GetLegalPieceTypesForPosition(color, col, row);
        if (candidateTypes.Count == 1)
            return (candidateTypes[0], 1.0, 1.0);

        string prefix = color == PieceColor.Red ? "R" : "B";

        // ① 渲染字模分（内置库 + 开局锚定学习的自适应库）
        var fontBest = new Dictionary<PieceType, double>();
        foreach (var type in candidateTypes)
        {
            string key = $"{prefix}_{type}";
            double best = 0;

            Mat? adaptTpl = null;
            lock (_adaptiveTemplates)
            {
                _adaptiveTemplates.TryGetValue(key, out adaptTpl);
            }
            if (adaptTpl != null)
                best = Math.Max(best, MatchSimilarity(charMask, adaptTpl) * 1.25);

            if (_builtinTemplates.TryGetValue(key, out var tplList))
            {
                foreach (var tpl in tplList)
                    best = Math.Max(best, MatchSimilarity(charMask, tpl));
            }

            fontBest[type] = best;
        }

        // ② 游戏实截贴图分（同一游戏内外观恒定，是美术字的权威样本）
        var spriteBest = new Dictionary<PieceType, double>();
        bool hasSprite = false;
        using Mat? graySprite = ToGraySprite(roiBgr);
        if (graySprite != null)
        {
            foreach (var type in candidateTypes)
            {
                double best = 0;
                Mat[] snapshots;
                lock (_spriteTemplates)
                {
                    if (!_spriteTemplates.TryGetValue($"{prefix}_{type}", out var list))
                        continue;
                    snapshots = list.ToArray();
                }
                foreach (var tpl in snapshots)
                    best = Math.Max(best, GrayNcc(graySprite, tpl));
                if (snapshots.Length > 0)
                {
                    spriteBest[type] = best;
                    hasSprite = true;
                }
            }
        }

        // ③ 综合评分：有实截贴图的类型以贴图为主、字模为辅；没有的退回纯字模
        PieceType bestType = candidateTypes[0];
        double bestScore = -1.0;
        double secondBestScore = -1.0;
        double bestLearnScore = 0;
        foreach (var type in candidateTypes)
        {
            double font = fontBest.GetValueOrDefault(type);
            double combined;
            double learnRaw;
            if (hasSprite && spriteBest.TryGetValue(type, out var sprite) && sprite >= 0.65)
            {
                combined = sprite * 0.6 + font * 0.4;
                learnRaw = Math.Max(sprite, font);
            }
            else
            {
                combined = font;
                learnRaw = font;
            }

            if (combined > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = combined;
                bestType = type;
                bestLearnScore = learnRaw;
            }
            else if (combined > secondBestScore)
            {
                secondBestScore = combined;
            }
        }

        // 低分或无明显优势时仍保留占用和颜色：空格与低置信棋子不能混为一谈，
        // 否则完整开局无法触发颜色锚定。learnScore/margin 置为 0，
        // 追踪层会保留已确认类型，也不会用这个样本反哺模板或自愈类型。
        double margin = bestScore - Math.Max(0, secondBestScore);
        if (bestScore < 0.36 || (bestScore < 0.62 && margin < 0.08))
            return (bestType, 0, 0);

        return (bestType, bestLearnScore, margin);
    }

    /// <summary>把 ROI 转为 SpriteSize 方形灰度图，用于与实截贴图比对</summary>
    private static Mat? ToGraySprite(Mat roiBgr)
    {
        try
        {
            if (roiBgr.Empty())
                return null;
            using var gray = new Mat();
            if (roiBgr.Channels() == 4)
                Cv2.CvtColor(roiBgr, gray, ColorConversionCodes.BGRA2GRAY);
            else if (roiBgr.Channels() == 3)
                Cv2.CvtColor(roiBgr, gray, ColorConversionCodes.BGR2GRAY);
            else
                roiBgr.CopyTo(gray);

            var resized = new Mat();
            Cv2.Resize(gray, resized, new Size(SpriteSize, SpriteSize), 0, 0, InterpolationFlags.Area);
            return resized;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>两张同尺寸灰度图的归一化互相关（0~1）</summary>
    private static double GrayNcc(Mat a, Mat b)
    {
        if (a.Empty() || b.Empty() || a.Size() != b.Size())
            return 0;
        try
        {
            using var fa = new Mat();
            using var fb = new Mat();
            a.ConvertTo(fa, MatType.CV_32F, 1.0 / 255.0);
            b.ConvertTo(fb, MatType.CV_32F, 1.0 / 255.0);
            using var res = new Mat();
            Cv2.MatchTemplate(fa, fb, res, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(res, out _, out double max, out _, out _);
            return Math.Max(0.0, max);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>从截图裁下某交叉点的完整棋子外观（灰度 SpriteSize 方图），失败返回 null</summary>
    private static Mat? CaptureSprite(Mat screenshot, BoardCalibration calibration, int col, int row)
    {
        try
        {
            double radius = calibration.GetCellRadius();
            if (radius < 8)
                return null;

            var screenPos = calibration.BoardToScreen(col, row);
            var center = FindBestPieceCenter(screenshot, screenPos, radius * 0.25);
            int x = (int)Math.Max(0, center.X - radius);
            int y = (int)Math.Max(0, center.Y - radius);
            int w = (int)Math.Min(screenshot.Width - x, radius * 2);
            int h = (int)Math.Min(screenshot.Height - y, radius * 2);
            if (w <= 12 || h <= 12)
                return null;

            using var roi = new Mat(screenshot, new OpenCvSharp.Rect(x, y, w, h));
            using var gray = new Mat();
            if (roi.Channels() == 4)
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGRA2GRAY);
            else if (roi.Channels() == 3)
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
            else
                roi.CopyTo(gray);

            var resized = new Mat();
            Cv2.Resize(gray, resized, new Size(SpriteSize, SpriteSize), 0, 0, InterpolationFlags.Area);
            return resized;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>向实截贴图库添加样本（接管传入 Mat 的所有权）：只补缺、近似去重、封顶</summary>
    private void AddSprite(string key, Mat sprite)
    {
        if (sprite == null || sprite.Empty())
        {
            sprite?.Dispose();
            return;
        }

        lock (_spriteTemplates)
        {
            if (!_spriteTemplates.TryGetValue(key, out var list))
            {
                list = new List<Mat>();
                _spriteTemplates[key] = list;
            }

            if (list.Count >= MaxSpritesPerKey)
            {
                sprite.Dispose();
                return;
            }

            foreach (var existing in list)
            {
                if (GrayNcc(existing, sprite) >= 0.93)
                {
                    sprite.Dispose();
                    return;
                }
            }

            list.Add(sprite);
        }
    }

    private bool SpriteHasCapacity(string key)
    {
        lock (_spriteTemplates)
            return !_spriteTemplates.TryGetValue(key, out var list) || list.Count < MaxSpritesPerKey;
    }

    /// <summary>
    /// 检测某交叉点是否带“上一步落点”高亮光环（JJ象棋可能呈绿色或白色）。
    /// 标记常落在棋子外圈，不能按固定棋子半径采样；用 0.9~1.35 倍半径
    /// 的环带寻找连续的白色/绿色弧段，并保留白点标记兜底。
    /// </summary>
    private static bool IsCellHighlighted(Mat hsv, ScreenPoint center, double radius)
    {
        try
        {
            int cx = (int)center.X;
            int cy = (int)center.Y;
            int greenHits = 0;
            int total = 0;
            var ringLightHits = new int[3];
            var ringTotals = new int[3];
            var ringValueSums = new int[3];
            var centerPixel = hsv.Get<Vec3b>(Math.Clamp(cy, 0, hsv.Height - 1),
                                             Math.Clamp(cx, 0, hsv.Width - 1));
            bool centerWhite = centerPixel.Item1 <= 45 && centerPixel.Item2 >= 220;

            var ringFractions = new[] { 0.90, 1.10, 1.30 };
            for (int ringIndex = 0; ringIndex < ringFractions.Length; ringIndex++)
            {
                int rr = Math.Max(3, (int)(radius * ringFractions[ringIndex]));
                for (int i = 0; i < 16; i++)
                {
                    double ang = i * Math.PI / 8.0;
                    int px = cx + (int)Math.Round(rr * Math.Cos(ang));
                    int py = cy + (int)Math.Round(rr * Math.Sin(ang));
                    if (px < 1 || py < 1 || px >= hsv.Width - 1 || py >= hsv.Height - 1)
                        continue;
                    total++;
                    ringTotals[ringIndex]++;

                    var p = hsv.Get<Vec3b>(py, px);
                    byte h = p.Item0, s = p.Item1, v = p.Item2;
                    // 绿色光环：放宽色相区间、饱和度与明度，适应各种透明度和画质下的绿色光晕
                    if (h >= 30 && h <= 95 && s >= 40 && v >= 85)
                        greenHits++;
                    // 部分棋局/缩放下光环会被渲染成白色或低饱和亮环。
                    // 限制为低饱和高亮，并要求中间环带比内外环带更亮，
                    // 避免把普通米色棋盘背景算作落点。
                    if (s <= 65 && v >= 175)
                        ringLightHits[ringIndex]++;
                    ringValueSums[ringIndex] += v;
                }
            }

            bool greenRing = total >= 20 && (greenHits * 5 >= total || greenHits >= 5);
            bool whiteRing = ringTotals[1] >= 12 &&
                             ringLightHits[1] * 2 >= ringTotals[1] &&
                             ringValueSums[1] / ringTotals[1] >=
                             Math.Max(ringValueSums[0] / Math.Max(1, ringTotals[0]),
                                      ringValueSums[2] / Math.Max(1, ringTotals[2])) + 12;
            bool whiteDot = centerWhite && ringTotals[2] >= 12 &&
                            centerPixel.Item2 >= ringValueSums[2] / ringTotals[2] + 25;
            return greenRing || whiteRing || whiteDot;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算两个二值字模的匹配相似度。
    /// 综合 IoU（像素重叠比）与归一化互相关（CCoeffNormed）两项指标，
    /// 对不同美术字体风格的鲁棒性优于单纯 IoU，能更好区分外形相近的棋子（如 俥/傌/砲）。
    /// </summary>
    private static double MatchSimilarity(Mat mask, Mat tpl)
    {
        if (mask.Size() != tpl.Size())
            return 0;

        // IoU 分：像素级重叠率
        using var intersection = new Mat();
        Cv2.BitwiseAnd(mask, tpl, intersection);
        using var unionMat = new Mat();
        Cv2.BitwiseOr(mask, tpl, unionMat);
        int interCount = Cv2.CountNonZero(intersection);
        int unionCount = Cv2.CountNonZero(unionMat);
        double iouScore = unionCount == 0 ? 0.0 : (double)interCount / unionCount;

        // 归一化互相关分（Pearson 相关系数），对字体笔画粗细与局部风格容差更优
        try
        {
            using var maskF = new Mat();
            using var tplF = new Mat();
            mask.ConvertTo(maskF, MatType.CV_32F, 1.0 / 255.0);
            tpl.ConvertTo(tplF, MatType.CV_32F, 1.0 / 255.0);
            using var ccResult = new Mat();
            Cv2.MatchTemplate(maskF, tplF, ccResult, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(ccResult, out _, out double ccScore, out _, out _);
            double normCc = Math.Max(0.0, ccScore);
            return iouScore * 0.4 + normCc * 0.6;
        }
        catch
        {
            return iouScore;
        }
    }

    /// <summary>
    /// 依据象棋规则，过滤指定格子上绝不可能出现的棋子类型
    /// </summary>
    private static List<PieceType> GetLegalPieceTypesForPosition(PieceColor color, int col, int row)
    {
        var list = new List<PieceType>();
        bool isRed = color == PieceColor.Red;

        bool inPalace = (col >= 3 && col <= 5) && (isRed ? (row >= 7 && row <= 9) : (row >= 0 && row <= 2));

        if (inPalace)
        {
            list.Add(PieceType.King);
            bool isAdvisorPos = (col == 4 && (row == 1 || row == 8)) ||
                                ((col == 3 || col == 5) && (row == 0 || row == 2 || row == 7 || row == 9));
            if (isAdvisorPos)
                list.Add(PieceType.Advisor);
        }

        bool isElephantPos = isRed
            ? (row >= 5 && ((row == 9 && (col == 2 || col == 6)) || (row == 7 && (col == 0 || col == 4 || col == 8)) || (row == 5 && (col == 2 || col == 6))))
            : (row <= 4 && ((row == 0 && (col == 2 || col == 6)) || (row == 2 && (col == 0 || col == 4 || col == 8)) || (row == 4 && (col == 2 || col == 6))));

        if (isElephantPos)
            list.Add(PieceType.Elephant);

        bool isPawnPos = isRed ? (row <= 6) : (row >= 3);
        if (isPawnPos)
            list.Add(PieceType.Pawn);

        list.Add(PieceType.Chariot);
        list.Add(PieceType.Horse);
        list.Add(PieceType.Cannon);

        return list;
    }

    /// <summary>
    /// 当检测到开局或标准阵型时，动态学习提取当前游戏的美术字模
    /// </summary>
    private void TryLearnAdaptiveTemplates(Mat screenshot, BoardCalibration calibration, BoardState board)
    {
        int colorMatch = 0;
        var initial = BoardState.InitialPosition();

        for (int r = 0; r < BoardState.Rows; r++)
        {
            for (int c = 0; c < BoardState.Cols; c++)
            {
                var initCell = initial[c, r];
                var cell = board[c, r];
                if (!initCell.IsEmpty && !cell.IsEmpty && initCell.Color == cell.Color)
                    colorMatch++;
            }
        }

        // 只要初始位置有 >= 24 颗棋子颜色吻合，说明包含完整的开局棋子样本
        if (colorMatch >= 24)
        {
            double radius = calibration.GetCellRadius();
            foreach (var (pt, pType, pColor) in initial.GetAllPieces())
            {
                string key = $"{(pColor == PieceColor.Red ? "R" : "B")}_{pType}";
                if (_adaptiveTemplates.ContainsKey(key))
                    continue;

                var cur = board[pt];
                if (!cur.IsEmpty && cur.Color == pColor)
                {
                    var screenPos = calibration.BoardToScreen(pt.Col, pt.Row);
                    var center = FindBestPieceCenter(screenshot, screenPos, radius * 0.25);
                    int x = (int)Math.Max(0, center.X - radius);
                    int y = (int)Math.Max(0, center.Y - radius);
                    int w = (int)Math.Min(screenshot.Width - x, radius * 2);
                    int h = (int)Math.Min(screenshot.Height - y, radius * 2);

                    if (w > 12 && h > 12)
                    {
                        using var roi = new Mat(screenshot, new OpenCvSharp.Rect(x, y, w, h));
                        using var bgr = new Mat();
                        if (roi.Channels() == 4) Cv2.CvtColor(roi, bgr, ColorConversionCodes.BGRA2BGR);
                        else roi.CopyTo(bgr);

                        var (_, mask) = DetectPieceColorAndMask(bgr, radius);
                        if (mask != null && !mask.Empty())
                        {
                            _adaptiveTemplates[key] = mask;
                        }

                        // 同步收录该棋子的完整实截贴图：
                        // 位置+颜色双重真值确定的样本，是游戏美术字外观的权威来源
                        if (SpriteHasCapacity(key))
                        {
                            var sprite = CaptureSprite(screenshot, calibration, pt.Col, pt.Row);
                            if (sprite != null)
                                AddSprite(key, sprite);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 走子追踪与状态继承
    /// <para>
    /// 核心策略：
    /// 1. 首帧/开局锚定：凡初始阵列位置颜色吻合 ≥20 颗，直接采用标准开局盘面。
    ///    开局棋子类型由定义唯一确定，不依赖字形分类，彻底杜绝美术字误识。
    /// 2. 颜色未变位置继承上一帧类型；每色每帧只处理"最优一步"（最近邻 old→new 对）。
    /// 3. 未配对的旧棋子原位恢复（防噪点漏失），未配对的新位置忽略（防弱斑幽灵棋）。
    /// </para>
    /// </summary>
    public static BoardState TrackBoard(BoardState? previousBoard, BoardState detectedBoard, IReadOnlyList<BoardPoint>? highlightedCells = null)
    {
        // ── 开局锚定 ──
        // 初始阵列位置上 ≥30 颗棋子颜色与标准开局一致（即最多走了 2 步），
        // 直接使用标准初始盘面：颜色是视觉强特征，类型在开局是常量，
        // 两者结合不受美术字体误识影响。
        var stdInitial = BoardState.InitialPosition();
        int alignColor = CountInitialColorMatches(detectedBoard);

        if (previousBoard == null)
        {
            if (alignColor >= 32)
                return stdInitial;

            if (alignColor >= 28)
            {
                // 开局阶段（双方已走 1~2 步）：
                // 未移动棋子（仍坐落在初始位且颜色一致）直接按开局定义赋予确切类型（消除美术字误识）
                // 移动的棋子继承变空初始位的类型，保留走子，不抹平已走局面！
                return AnchorOpeningMoves(detectedBoard, stdInitial);
            }

            return detectedBoard;
        }

        // 新对局检测：上一帧盘面不在开局形态，而当前帧几乎完整呈现初始阵列。
        // 真实走子只会让初始位吻合数单调递减（棋子离开初始位），
        // 只有"再来一局"才会让满阵列重现，因此用 31/31 门槛即可与普通走子严格区分。
        int prevAlignColor = CountInitialColorMatches(previousBoard);
        if (alignColor >= 31 && prevAlignColor < 31)
            return stdInitial; // 对手点了"再来一局"，强制重置，绝不走走子继承

        // 第一遍：找出未变格（颜色一致）直接继承已有确切类型，其余按阵营分类
        var result = new BoardState { CurrentPlayer = previousBoard.CurrentPlayer };
        var remainRed   = new List<(BoardPoint pt, BoardCell cell)>();
        var remainBlack = new List<(BoardPoint pt, BoardCell cell)>();
        var newRed   = new List<BoardPoint>();
        var newBlack = new List<BoardPoint>();

        for (int r = 0; r < BoardState.Rows; r++)
        {
            for (int c = 0; c < BoardState.Cols; c++)
            {
                var oldCell = previousBoard[c, r];
                var newCell = detectedBoard[c, r];
                var pt = new BoardPoint(c, r);

                if (!newCell.IsEmpty && !oldCell.IsEmpty && oldCell.Color == newCell.Color)
                {
                    // 颜色未变：严格继承已知真实类型（防止走子光晕干扰识别）
                    result[c, r] = new BoardCell { Color = newCell.Color, Type = oldCell.Type };
                }
                else
                {
                    if (!newCell.IsEmpty)
                    {
                        if (newCell.Color == PieceColor.Red)        newRed.Add(pt);
                        else if (newCell.Color == PieceColor.Black) newBlack.Add(pt);
                    }
                    if (!oldCell.IsEmpty)
                    {
                        if (oldCell.Color == PieceColor.Red)        remainRed.Add((pt, oldCell));
                        else if (oldCell.Color == PieceColor.Black) remainBlack.Add((pt, oldCell));
                    }
                }
            }
        }

        // 第二遍：每方最多处理"最优一步"（距离最近的 old→new 对）。
        // 以走子前盘面做合法性校验，优先采用合法走法对，
        // 避免吃子/高亮光晕同帧出现时"最近邻"错配把类型瞬移到非法位置。
        ApplyBestSingleMove(result, remainRed,   newRed,   PieceColor.Red, previousBoard, highlightedCells);
        ApplyBestSingleMove(result, remainBlack, newBlack, PieceColor.Black, previousBoard, highlightedCells);

        // 走子追踪只继承和转移已有棋子，不执行兵种降级，也不凭空补将帅。
        return result;
    }

    /// <summary>
    /// 开局阶段（双方已走 1~2 步）首帧锚定：
    /// 未移动棋子赋予标准开局真值类型；已移动棋子根据变空的初始格继承真实兵种，
    /// 既彻底杜绝美术字误识，又完美保留已走盘面，防止中局/开局走步后软件启动时盘面被错误重置。
    /// </summary>
    private static BoardState AnchorOpeningMoves(BoardState detected, BoardState stdInitial)
    {
        var result = detected.Clone();
        var movedRedOrig = new List<(BoardPoint pt, PieceType type)>();
        var movedBlackOrig = new List<(BoardPoint pt, PieceType type)>();
        var newRedPositions = new List<BoardPoint>();
        var newBlackPositions = new List<BoardPoint>();

        for (int r = 0; r < BoardState.Rows; r++)
        {
            for (int c = 0; c < BoardState.Cols; c++)
            {
                var pt = new BoardPoint(c, r);
                var initCell = stdInitial[c, r];
                var curCell = detected[c, r];

                if (!initCell.IsEmpty)
                {
                    if (!curCell.IsEmpty && curCell.Color == initCell.Color)
                    {
                        // 依然在初始格且颜色吻合：兵种 100% 确定为开局真值类型
                        result[c, r] = new BoardCell { Color = initCell.Color, Type = initCell.Type };
                    }
                    else
                    {
                        // 初始格变空：说明该棋子已离开
                        if (initCell.Color == PieceColor.Red)
                            movedRedOrig.Add((pt, initCell.Type));
                        else
                            movedBlackOrig.Add((pt, initCell.Type));
                    }
                }
                else if (!curCell.IsEmpty)
                {
                    // 非初始格有子：说明是某颗离开初始位的棋子
                    if (curCell.Color == PieceColor.Red)
                        newRedPositions.Add(pt);
                    else if (curCell.Color == PieceColor.Black)
                        newBlackPositions.Add(pt);
                }
            }
        }

        AssignMovedPieces(result, stdInitial, movedRedOrig, newRedPositions, PieceColor.Red);
        AssignMovedPieces(result, stdInitial, movedBlackOrig, newBlackPositions, PieceColor.Black);

        return result;
    }

    private static void AssignMovedPieces(
        BoardState result,
        BoardState stdInitial,
        List<(BoardPoint pt, PieceType type)> origPieces,
        List<BoardPoint> newPositions,
        PieceColor color)
    {
        foreach (var orig in origPieces)
        {
            // 必须在象棋规则下能从 orig.pt 合法走到 np
            var legalTarget = newPositions.FirstOrDefault(np => XiangqiMoveRules.IsLegalMove(stdInitial, orig.pt, np, color));
            if (legalTarget != default)
            {
                result[legalTarget.Col, legalTarget.Row] = new BoardCell { Color = color, Type = orig.type };
                newPositions.Remove(legalTarget);
            }
            else
            {
                // 无合法落点：说明该棋子并未真实移动（新格只是噪点，原位只是微弱漏检），原位恢复开局真值
                result[orig.pt.Col, orig.pt.Row] = new BoardCell { Color = color, Type = orig.type };
            }
        }

        // 开局阶段棋子数守恒，未配对的非法新位置视为视觉弱斑噪点，清空
        foreach (var np in newPositions)
        {
            result[np.Col, np.Row] = new BoardCell();
        }
    }

    /// <summary>
    /// 统计 board 与标准开局阵列在位置+颜色上吻合的棋子数（≥30 视为开局形态）
    /// </summary>
    public static int CountInitialColorMatches(BoardState board)
    {
        var initial = BoardState.InitialPosition();
        int match = 0;
        for (int r = 0; r < BoardState.Rows; r++)
            for (int c = 0; c < BoardState.Cols; c++)
            {
                var i = initial[c, r]; var d = board[c, r];
                if (!i.IsEmpty && !d.IsEmpty && i.Color == d.Color) match++;
            }
        return match;
    }

    /// <summary>
    /// 对某一阵营执行"最优单步"匹配：
    /// 1. 若有 old 和 new 位置，找距离最近的 (old, new) 对（优先符合象棋规则的
    ///    合法走法对），将 old 的真实类型转移到 new。
    /// 2. 若落点被光环冲淡导致 newPositions 漏识，优先吸附到合法的高亮落点格。
    /// 3. 其余未配对的旧棋子原位恢复（防止噪点导致棋子消失）。
    /// </summary>
    private static void ApplyBestSingleMove(
        BoardState result,
        List<(BoardPoint pt, BoardCell cell)> remainOld,
        List<BoardPoint> newPositions,
        PieceColor side,
        BoardState? boardBeforeMove,
        IReadOnlyList<BoardPoint>? highlightedCells = null)
    {
        if (newPositions.Count > 0 && remainOld.Count > 0)
        {
            (BoardPoint pt, BoardCell cell) bestOld = remainOld[0];
            BoardPoint bestNew = newPositions[0];
            int bestDistSq = int.MaxValue;

            // 先在合法走法对里找最近的一对；找不到再退回纯最近邻
            if (boardBeforeMove != null)
            {
                var fromPts = remainOld.Select(o => o.pt).ToList();
                var legal = XiangqiMoveRules.NearestLegalPair(boardBeforeMove, fromPts, newPositions);
                if (legal != null)
                {
                    var (lf, ln, _) = legal.Value;
                    bestOld = remainOld.First(o => o.pt.Col == lf.Col && o.pt.Row == lf.Row);
                    bestNew = ln;
                    bestDistSq = (lf.Col - ln.Col) * (lf.Col - ln.Col) + (lf.Row - ln.Row) * (lf.Row - ln.Row);
                }
            }

            if (bestDistSq == int.MaxValue && boardBeforeMove == null)
            {
                foreach (var o in remainOld)
                {
                    foreach (var n in newPositions)
                    {
                        int dSq = (o.pt.Col - n.Col) * (o.pt.Col - n.Col)
                                + (o.pt.Row - n.Row) * (o.pt.Row - n.Row);
                        if (dSq < bestDistSq) { bestDistSq = dSq; bestOld = o; bestNew = n; }
                    }
                }
            }

            if (bestDistSq != int.MaxValue)
            {
                result[bestNew.Col, bestNew.Row] = new BoardCell { Color = side, Type = bestOld.cell.Type };
                remainOld.Remove(bestOld);
            }
        }

        // 高亮格落点吸附：落点被高亮光晕冲淡导致墨迹视觉为空时，
        // 游戏高亮标记是真实落点真值！将能合法走到该高亮格的旧棋子转移过去，
        // 杜绝棋子离开后被误原位复活导致走步被完全吞噬的恶性 bug
        if (highlightedCells != null && remainOld.Count > 0 && boardBeforeMove != null)
        {
            foreach (var hl in highlightedCells)
            {
                if (result[hl.Col, hl.Row].IsEmpty)
                {
                    (BoardPoint pt, BoardCell cell)? bestHlOld = null;
                    int bestHlDistSq = int.MaxValue;
                    foreach (var o in remainOld)
                    {
                        if (XiangqiMoveRules.IsLegalMove(boardBeforeMove, o.pt, hl, side))
                        {
                            int dSq = (o.pt.Col - hl.Col) * (o.pt.Col - hl.Col) + (o.pt.Row - hl.Row) * (o.pt.Row - hl.Row);
                            if (dSq < bestHlDistSq)
                            {
                                bestHlDistSq = dSq;
                                bestHlOld = o;
                            }
                        }
                    }

                    if (bestHlOld != null)
                    {
                        result[hl.Col, hl.Row] = new BoardCell { Color = side, Type = bestHlOld.Value.cell.Type };
                        remainOld.Remove(bestHlOld.Value);
                    }
                }
            }
        }

        // 所有未配对旧棋子原位恢复（避免噪点导致的短暂消失）
        foreach (var (pt, cell) in remainOld)
        {
            if (result[pt.Col, pt.Row].IsEmpty)
                result[pt.Col, pt.Row] = cell;
        }
    }

    public static List<(BoardPoint point, BoardCell old, BoardCell now)> DiffBoards(
        BoardState old, BoardState now)
    {
        var diffs = new List<(BoardPoint, BoardCell, BoardCell)>();

        for (int c = 0; c < BoardState.Cols; c++)
        {
            for (int r = 0; r < BoardState.Rows; r++)
            {
                var pt = new BoardPoint(c, r);
                var oldCell = old[pt];
                var nowCell = now[pt];

                if (oldCell.Type != nowCell.Type || oldCell.Color != nowCell.Color)
                {
                    diffs.Add((pt, oldCell, nowCell));
                }
            }
        }

        return diffs;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var list in _builtinTemplates.Values)
            {
                foreach (var mat in list)
                    mat.Dispose();
            }
            _builtinTemplates.Clear();

            foreach (var mat in _adaptiveTemplates.Values)
                mat.Dispose();
            _adaptiveTemplates.Clear();

            ResetSprites();

            _disposed = true;
        }
    }
}
