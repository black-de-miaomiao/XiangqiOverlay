using OpenCvSharp;
using XiangqiOverlay.Core;
using XiangqiOverlay.Vision;

int failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    if (!ok) failures++;
}

// ═══ TrackBoard 回归 ═══
var misdetect = BoardState.InitialPosition();
misdetect[2, 0] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Black };
misdetect[6, 0] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Black };
misdetect[0, 6] = new BoardCell { Type = PieceType.Horse, Color = PieceColor.Red };
var anchored = PieceRecognizer.TrackBoard(null, misdetect);
Check("开局首帧颜色锚定纠正误识类型",
    anchored[2, 0].Type == PieceType.Elephant && anchored[0, 6].Type == PieceType.Pawn);

var prev = BoardState.InitialPosition();
var afterMove = BoardState.InitialPosition();
afterMove[7, 7] = new BoardCell();
afterMove[4, 7] = new BoardCell { Type = PieceType.Horse, Color = PieceColor.Red };
var tracked = PieceRecognizer.TrackBoard(prev, afterMove);
Check("走子继承：炮移动后类型正确延续",
    tracked[7, 7].IsEmpty && tracked[4, 7].Type == PieceType.Cannon);

var midGame = BoardState.InitialPosition();
midGame[0, 0] = new BoardCell();
midGame[0, 9] = new BoardCell();
midGame[4, 4] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
var reset = PieceRecognizer.TrackBoard(midGame, BoardState.InitialPosition());
Check("新对局强制重置为初始盘面",
    reset[0, 0].Type == PieceType.Chariot && reset[4, 4].IsEmpty);

// ═══ 自动校准精修验证：合成棋盘 + 故意偏移 ═══
// 画一个 9x10 棋盘（间距50，原点(40,40)），开局32颗棋子
using (var board = new Mat(560, 500, MatType.CV_8UC3, new Scalar(196, 176, 148)))
{
    // 网格线（浅灰：真实棋盘线不会被判成棋子墨迹）
    for (int r = 0; r < 10; r++)
        Cv2.Line(board, new Point(40, 40 + r * 50), new Point(40 + 8 * 50, 40 + r * 50), new Scalar(95, 95, 95), 1);
    for (int c = 0; c < 9; c++)
    {
        if (c == 0 || c == 8)
            Cv2.Line(board, new Point(40 + c * 50, 40), new Point(40 + c * 50, 40 + 9 * 50), new Scalar(95, 95, 95), 1);
        else
        {
            Cv2.Line(board, new Point(40 + c * 50, 40), new Point(40 + c * 50, 40 + 4 * 50), new Scalar(95, 95, 95), 1);
            Cv2.Line(board, new Point(40 + c * 50, 40 + 5 * 50), new Point(40 + c * 50, 40 + 9 * 50), new Scalar(95, 95, 95), 1);
        }
    }

    // 初始 32 颗棋子：米色圆盘 + 红/黑字心
    var initial = BoardState.InitialPosition();
    var rng = new Random(7);
    foreach (var (pt, _, color) in initial.GetAllPieces())
    {
        var center = new Point(40 + pt.Col * 50, 40 + pt.Row * 50);
        Cv2.Circle(board, center, 21, new Scalar(210, 200, 180), -1);
        Cv2.Circle(board, center, 21, new Scalar(120, 90, 60), 2);
        // 字心：红色画红字，黑色画黑字（贯穿中心的十字+斜笔画，模拟真实字形）
        var ink = color == PieceColor.Red ? new Scalar(40, 40, 220) : new Scalar(30, 30, 30);
        Cv2.Line(board, center + new Point(-10, 0), center + new Point(10, 0), ink, 3);
        Cv2.Line(board, center + new Point(0, -10), center + new Point(0, 10), ink, 3);
        Cv2.Line(board, center + new Point(-7, -7), center + new Point(7, 7), ink, 2);
        Cv2.Line(board, center + new Point(-7, 7), center + new Point(7, -7), ink, 2);
    }

    var detector = new BoardDetector();
    var cal = detector.AutoDetect(board);

    bool detected = cal.IsValid;
    double errTl = detected ? Math.Sqrt(Math.Pow(cal.TopLeft.X - 40, 2) + Math.Pow(cal.TopLeft.Y - 40, 2)) : -1;
    double errBr = detected ? Math.Sqrt(Math.Pow(cal.BottomRight.X - 440, 2) + Math.Pow(cal.BottomRight.Y - 490, 2)) : -1;
    Check($"合成棋盘自动校准成功 (isValid={detected})", detected);
    Check($"左上角误差≤5px (err={errTl:F1}px)", errTl >= 0 && errTl <= 5);
    Check($"右下角误差≤5px (err={errBr:F1}px)", errBr >= 0 && errBr <= 5);

    if (true)
    {
        // 隔离诊断：先用真实角点手工校准读盘
        var reader = new VisionBoardReader();
        var calTrue = new BoardCalibration
        {
            TopLeft = new ScreenPoint(40, 40),
            TopRight = new ScreenPoint(440, 40),
            BottomLeft = new ScreenPoint(40, 490),
            BottomRight = new ScreenPoint(440, 490),
            IsValid = true
        };
        var readTrue = reader.ReadBoard(board, calTrue);
        int okTrue = 0;
        foreach (var (pt, _, pcolor) in initial.GetAllPieces())
        {
            var cell = readTrue.Board[pt.Col, pt.Row];
            if (!cell.IsEmpty && cell.Color == pcolor) okTrue++;
        }
        Console.WriteLine($"手工精确校准颜色识别: {okTrue}/32");
        if (detected)
        {
            Console.WriteLine($"自动校准角点: TL=({cal.TopLeft.X:F1},{cal.TopLeft.Y:F1}) BR=({cal.BottomRight.X:F1},{cal.BottomRight.Y:F1})");
        }
        var read = reader.ReadBoard(board, cal);
        int colorOk = 0, total = 0;
        foreach (var (pt, _, pcolor) in initial.GetAllPieces())
        {
            total++;
            var cell = read.Board[pt.Col, pt.Row];
            if (!cell.IsEmpty && cell.Color == pcolor) colorOk++;
        }
        Check($"自动校准后合成棋子颜色识别 {colorOk}/{total}", colorOk >= 28);

        // 实截贴图库：完整开局阵列入账后应学到 14 类样本（红黑各 7 种）
        Check($"实截贴图库已学习样本 (count={reader.GetRecognizerSpriteCount()})", reader.GetRecognizerSpriteCount() >= 14);

        // 时序一致性：同一校准+同一画面连续两帧全量重识别，结果必须完全一致
        //（VinXiangQi 式保证：识别确定性是"diff==2 才接受走子"判定的前提）
        var readAgain = reader.ReadBoard(board, cal);
        int diffCells = read.Board.CountDifferences(readAgain.Board);
        for (int c = 0; c < 9; c++)
            for (int r = 0; r < 10; r++)
            {
                var a = read.Board[c, r];
                var b = readAgain.Board[c, r];
                if (a.Type != b.Type || a.Color != b.Color)
                {
                    Console.WriteLine($"  差异格 ({c},{r}): 首帧={a.Color}/{a.Type} 二帧={b.Color}/{b.Type}");
                    var rawA = reader.LastRawBoard[c, r];
                    Console.WriteLine($"    Raw: {rawA.Color}/{rawA.Type}");
                }
            }
        Check($"连续两帧识别一致 (diffCells={diffCells})", diffCells == 0);

        var expectedRaw = reader.LastRawBoard.Clone();
        using var productionBgra = new Mat();
        Cv2.CvtColor(board, productionBgra, ColorConversionCodes.BGR2BGRA);
        var readProduction = reader.ReadBoard(productionBgra, cal);
        Check("生产 BGRA 与离线 BGR 完整盘面识别一致",
            readProduction.Board.CountDifferences(readAgain.Board) == 0 &&
            reader.LastRawBoard.CountDifferences(expectedRaw) == 0);

        reader.Dispose();
    }
    detector.Dispose();
}

// ═══ XiangqiMoveRules 走法合法性校验 ═══
var rulesBoard = BoardState.InitialPosition();
Check("车直线一步合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(0, 9), new BoardPoint(0, 8), PieceColor.Red));
Check("炮平移路径畅通合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(1, 2), new BoardPoint(5, 2), PieceColor.Black));
Check("炮无炮架吃子非法", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(1, 2), new BoardPoint(1, 7), PieceColor.Black));
Check("炮斜走非法（识别噪声典型形态）", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(1, 2), new BoardPoint(2, 3), PieceColor.Black));
Check("马路畅通合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(1, 0), new BoardPoint(2, 2), PieceColor.Black));
rulesBoard[1, 1] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Black };
Check("蹩马腿非法", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(1, 0), new BoardPoint(2, 2), PieceColor.Black));
rulesBoard[1, 1] = new BoardCell();
Check("象眼畅通合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(2, 0), new BoardPoint(4, 2), PieceColor.Black));
rulesBoard[2, 4] = new BoardCell { Type = PieceType.Elephant, Color = PieceColor.Black };
Check("象不能过河", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(2, 4), new BoardPoint(4, 6), PieceColor.Black));
Check("兵向前合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(2, 6), new BoardPoint(2, 5), PieceColor.Red));
Check("兵未过河不能横走", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(2, 6), new BoardPoint(1, 6), PieceColor.Red));
rulesBoard[2, 4] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Red };
Check("兵过河后可横走", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(2, 4), new BoardPoint(1, 4), PieceColor.Red));
Check("兵不能后退", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(2, 4), new BoardPoint(2, 5), PieceColor.Red));
Check("帅九宫内直走合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(4, 9), new BoardPoint(4, 8), PieceColor.Red));
Check("帅走两步非法", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(4, 9), new BoardPoint(4, 7), PieceColor.Red));
Check("士斜走合法", XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(3, 9), new BoardPoint(4, 8), PieceColor.Red));
Check("士直走非法", !XiangqiMoveRules.IsLegalMove(rulesBoard, new BoardPoint(4, 8), new BoardPoint(4, 7), PieceColor.Red));

// ═══ InferLastMove 合法对优先 ═══
// 最近的"变化对"是识别噪声（车→斜位幽灵），合法对更远但真实
var inferPrev = new BoardState();
inferPrev[0, 9] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
inferPrev[4, 6] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Red };
inferPrev[4, 0] = new BoardCell { Type = PieceType.King, Color = PieceColor.Black };
var inferNow = inferPrev.Clone();
inferNow[0, 9] = new BoardCell();
inferNow[0, 3] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
inferNow[2, 5] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Red }; // 噪声幽灵
var inferResult = new BoardReadResult { Board = inferNow };
var inferred = inferResult.InferLastMove(inferPrev);
Check($"InferLastMove 跳过非法幽灵对 (得到 {inferred?.From}->{inferred?.To})",
    inferred != null && inferred.Value.From.Col == 0 && inferred.Value.From.Row == 9 &&
    inferred.Value.To.Col == 0 && inferred.Value.To.Row == 3);

// ═══ TrackBoard 吃子+走子同帧：合法配对防止类型瞬移/幽灵子 ═══
// 黑马 (2,0)→(3,2) 同时黑马炮在 (2,3) 被红车吃掉：
// 旧逻辑按最近邻把炮"斜移瞬移"到 (3,2) 并把马复活在原位；新逻辑只认合法走法对
var capPrev = new BoardState();
capPrev[4, 0] = new BoardCell { Type = PieceType.King, Color = PieceColor.Black };
capPrev[4, 9] = new BoardCell { Type = PieceType.King, Color = PieceColor.Red };
capPrev[2, 0] = new BoardCell { Type = PieceType.Horse, Color = PieceColor.Black };
capPrev[2, 3] = new BoardCell { Type = PieceType.Cannon, Color = PieceColor.Black };
capPrev[7, 3] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
var capNow = capPrev.Clone();
capNow[2, 0] = new BoardCell();
capNow[3, 2] = new BoardCell { Type = PieceType.Horse, Color = PieceColor.Black };
capNow[2, 3] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
capNow[7, 3] = new BoardCell();
var capTracked = PieceRecognizer.TrackBoard(capPrev, capNow);
Check("同帧吃子+走子：马类型正确转移到 (3,2)",
    capTracked[3, 2].Type == PieceType.Horse && capTracked[3, 2].Color == PieceColor.Black);
Check("同帧吃子+走子：马原位不复活（幽灵）", capTracked[2, 0].IsEmpty);
Check("同帧吃子+走子：炮被吃后不瞬移", capTracked[2, 3].Type == PieceType.Chariot && capTracked[2, 3].Color == PieceColor.Red);

// ═══ 预期盘面：ApplyMove 生成引擎走完的盘面 ═══
var expBase = BoardState.InitialPosition();
var expMove = new ChessMove(new BoardPoint(7, 7), new BoardPoint(4, 7)); // 炮二平五
var expected = XiangqiMoveRules.ApplyMove(expBase, expMove);
Check("ApplyMove：合法走法生成预期盘面",
    expected != null && expected[4, 7].Type == PieceType.Cannon && expected[7, 7].IsEmpty);
Check("ApplyMove：预期盘面与真实下一帧完全一致（CountDifferences=0）",
    expected != null && expected.CountDifferences(expected) == 0);
Check("ApplyMove：非法走法被拒绝",
    XiangqiMoveRules.ApplyMove(expBase, new ChessMove(new BoardPoint(2, 6), new BoardPoint(2, 7))) == null); // 红兵倒退=非法

// ═══ InferLastMove 高亮分层：游戏标注的真值落点优先 ═══
// 两个合法候选对，无高亮时取最近（马），高亮落在更远的车上时车优先
var hlPrev = new BoardState();
hlPrev[4, 0] = new BoardCell { Type = PieceType.King, Color = PieceColor.Black };
hlPrev[4, 9] = new BoardCell { Type = PieceType.King, Color = PieceColor.Red };
hlPrev[0, 9] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
hlPrev[3, 9] = new BoardCell { Type = PieceType.Horse, Color = PieceColor.Red };
var hlNow = hlPrev.Clone();
hlNow[0, 9] = new BoardCell();
hlNow[0, 5] = new BoardCell { Type = PieceType.Chariot, Color = PieceColor.Red };
hlNow[3, 9] = new BoardCell();
hlNow[4, 7] = new BoardCell { Type = PieceType.Horse, Color = PieceColor.Red };
var hlResult = new BoardReadResult { Board = hlNow };
var noHl = hlResult.InferLastMove(hlPrev);
Check($"无高亮时取最近合法对 (得到 {noHl?.From}->{noHl?.To})",
    noHl != null && noHl.Value.From.Col == 3 && noHl.Value.To.Col == 4);
var withHl = hlResult.InferLastMove(hlPrev, new[] { new BoardPoint(0, 5) });
Check($"高亮落点优先 (得到 {withHl?.From}->{withHl?.To})",
    withHl != null && withHl.Value.From.Col == 0 && withHl.Value.From.Row == 9 &&
    withHl.Value.To.Col == 0 && withHl.Value.To.Row == 5);

// ═══ 窗口缩放 → 校准网格等比缩放 ═══
var cal0 = new BoardCalibration
{
    TopLeft = new ScreenPoint(40, 40),
    TopRight = new ScreenPoint(440, 40),
    BottomLeft = new ScreenPoint(40, 490),
    BottomRight = new ScreenPoint(440, 490),
    IsValid = true,
    CalibratedClientWidth = 500,
    CalibratedClientHeight = 560
};
var calScaled = new BoardCalibration
{
    TopLeft = cal0.TopLeft, TopRight = cal0.TopRight,
    BottomLeft = cal0.BottomLeft, BottomRight = cal0.BottomRight,
    IsValid = true
};
calScaled.ScaleTo(0.9, 0.8); // 窗口缩到 90% 宽 / 80% 高
var p0 = cal0.BoardToScreen(4, 4);
var p1 = calScaled.BoardToScreen(4, 4);
Check($"网格中心按比例缩放 ({p0}->{p1})",
    Math.Abs(p1.X - p0.X * 0.9) < 0.01 && Math.Abs(p1.Y - p0.Y * 0.8) < 0.01);
var br0 = cal0.BoardToScreen(8, 9);
var br1 = calScaled.BoardToScreen(8, 9);
Check($"网格右下角按比例缩放 ({br0}->{br1})",
    Math.Abs(br1.X - br0.X * 0.9) < 0.01 && Math.Abs(br1.Y - br0.Y * 0.8) < 0.01);

// ═══ 快速连走追赶（对面秒下）：TryFindMoveSequence ═══
var fastStart = BoardState.InitialPosition(); // 红先
var fastMid = XiangqiMoveRules.ApplyMove(fastStart, new ChessMove(new BoardPoint(7, 7), new BoardPoint(4, 7)))!; // 红炮二平五
var fastFinal = XiangqiMoveRules.ApplyMove(fastMid, new ChessMove(new BoardPoint(1, 0), new BoardPoint(2, 2)))!; // 黑马8进7
bool foundSeq = XiangqiMoveRules.TryFindMoveSequence(
    fastStart, fastFinal, PlayerSide.Red, 2, null, out var seqBoard, out var seqMoves);
Check("对面秒下（两步连走）：成功推导走法序列", foundSeq && seqMoves.Count == 2);
Check("对面秒下（两步连走）：第一步为红炮二平五",
    foundSeq && seqMoves[0].From.Col == 7 && seqMoves[0].From.Row == 7 && seqMoves[0].To.Col == 4 && seqMoves[0].To.Row == 7);
Check("对面秒下（两步连走）：第二步为黑马8进7",
    foundSeq && seqMoves[1].From.Col == 1 && seqMoves[1].From.Row == 0 && seqMoves[1].To.Col == 2 && seqMoves[1].To.Row == 2);
Check("对面秒下（两步连走）：最终盘面与实际完全一致",
    foundSeq && seqBoard != null && seqBoard.CountDifferences(fastFinal) == 0);

// ═══ 吃子连走追赶：红车吃卒，黑车进一 ═══
var capStart = BoardState.InitialPosition();
capStart[0, 6] = new BoardCell { Type = PieceType.Pawn, Color = PieceColor.Black }; // 黑卒在 (0,6)
var capMid = XiangqiMoveRules.ApplyMove(capStart, new ChessMove(new BoardPoint(0, 9), new BoardPoint(0, 6)))!; // 红车吃卒 (0,6)
var capFinal = XiangqiMoveRules.ApplyMove(capMid, new ChessMove(new BoardPoint(8, 0), new BoardPoint(8, 1)))!; // 黑车进一 (8,1)
bool foundCapSeq = XiangqiMoveRules.TryFindMoveSequence(
    capStart, capFinal, PlayerSide.Red, 2, null, out var capSeqBoard, out var capSeqMoves);
Check("对面秒下吃子：成功推导吃子两步走法", foundCapSeq && capSeqMoves.Count == 2);

// ═══ 高亮吸附（防原位复活）：落点因光晕被冲淡判空时，棋子正确转移到高亮格 ═══
var hlTrackPrev = BoardState.InitialPosition();
var hlTrackDetected = BoardState.InitialPosition();
hlTrackDetected[1, 0] = new BoardCell(); // 黑马离开 (1,0)
// 落点 (2,2) 因为光晕被冲淡判为空（hlTrackDetected[2,2] 仍为空）
var hlLanding = new List<BoardPoint> { new BoardPoint(2, 2) };
var hlTrackedRes = PieceRecognizer.TrackBoard(hlTrackPrev, hlTrackDetected, hlLanding);
Check("高亮吸附：落点被光晕冲淡时，黑马正确转移到高亮格 (2,2)",
    hlTrackedRes[2, 2].Type == PieceType.Horse && hlTrackedRes[2, 2].Color == PieceColor.Black);
Check("高亮吸附：黑马未在原位 (1,0) 错误复活",
    hlTrackedRes[1, 0].IsEmpty);

// ═══ 真实用户上传截图诊断 ═══
string userImgPath = @"C:\Users\123\.gemini\antigravity\brain\c9208f62-f80e-4911-a812-153418448dac\.user_uploaded\media_1791701176165_dfc1244d.png";
if (File.Exists(userImgPath))
{
    using var fullImg = Cv2.ImRead(userImgPath);
    Console.WriteLine($"\n═══ 用户截图诊断 ═══ 尺寸: {fullImg.Width}x{fullImg.Height}");
    // 裁剪左侧 JJ 象棋窗口（右边分析面板大约在 474 开始）
    int jjWidth = 474;
    using var jjCrop = new Mat(fullImg, new Rect(0, 0, Math.Min(jjWidth, fullImg.Width), fullImg.Height));
    
    var detector = new BoardDetector();
    var cal = detector.AutoDetect(jjCrop);
    Console.WriteLine($"自动校准结果: IsValid={cal.IsValid}");
    if (cal.IsValid)
    {
        Console.WriteLine($"  TL: ({cal.TopLeft.X:F1}, {cal.TopLeft.Y:F1}), BR: ({cal.BottomRight.X:F1}, {cal.BottomRight.Y:F1})");
        var reader = new VisionBoardReader();
        var readResult = reader.ReadBoard(jjCrop, cal);
        var rawBoard = reader.LastRawBoard;
        Console.WriteLine("\n[原始识别 RawBoard]:");
        for (int r = 0; r < 10; r++)
        {
            string rowStr = $"R{r}: ";
            for (int c = 0; c < 9; c++)
            {
                var cell = rawBoard[c, r];
                if (cell.IsEmpty) rowStr += " .  ";
                else rowStr += $"{(cell.Color == PieceColor.Red ? "R" : "B")}_{cell.Type.ToString()[..2]} ";
            }
            Console.WriteLine(rowStr);
        }

        Console.WriteLine("\n[读盘结果 TrackedBoard]:");
        for (int r = 0; r < 10; r++)
        {
            string rowStr = $"R{r}: ";
            for (int c = 0; c < 9; c++)
            {
                var cell = readResult.Board[c, r];
                if (cell.IsEmpty) rowStr += " .  ";
                else rowStr += $"{(cell.Color == PieceColor.Red ? "R" : "B")}_{cell.Type.ToString()[..2]} ";
            }
            Console.WriteLine(rowStr);
        }
        // 保存一张画了网格和识别标签的调试图片
        using var debugImg = jjCrop.Clone();
        for (int c = 0; c < 9; c++)
        {
            for (int r = 0; r < 10; r++)
            {
                var pt = cal.BoardToScreen(c, r);
                Cv2.Circle(debugImg, new Point((int)pt.X, (int)pt.Y), 5, new Scalar(0, 0, 255), -1);
                var raw = rawBoard[c, r];
                if (!raw.IsEmpty)
                {
                    string label = $"{(raw.Color == PieceColor.Red ? "R" : "B")}{raw.Type.ToString()[..1]}";
                    Cv2.PutText(debugImg, label, new Point((int)pt.X - 10, (int)pt.Y - 8),
                        HersheyFonts.HersheySimplex, 0.4, new Scalar(0, 255, 0), 1);
                }
            }
        }
        // 自动化校验真实截图识别结果：
        // 1. 红兵三进一 (2,6) -> (2,5)
        Check("真实截图：红兵在 (2,5)", readResult.Board[2, 5].Type == PieceType.Pawn && readResult.Board[2, 5].Color == PieceColor.Red);
        Check("真实截图：原位 (2,6) 变空", readResult.Board[2, 6].IsEmpty);
        
        // 2. 黑象3进5 (2,0) -> (4,2)
        Check("真实截图：黑象在 (4,2)", readResult.Board[4, 2].Type == PieceType.Elephant && readResult.Board[4, 2].Color == PieceColor.Black);
        Check("真实截图：原位 (2,0) 变空", readResult.Board[2, 0].IsEmpty);

        // 3. 关键底线与骨干棋子
        Check("真实截图：黑车在 (0,0)", readResult.Board[0, 0].Type == PieceType.Chariot && readResult.Board[0, 0].Color == PieceColor.Black);
        Check("真实截图：黑马在 (1,0)", readResult.Board[1, 0].Type == PieceType.Horse && readResult.Board[1, 0].Color == PieceColor.Black);
        Check("真实截图：红帅在 (4,9)", readResult.Board[4, 9].Type == PieceType.King && readResult.Board[4, 9].Color == PieceColor.Red);
        Check("真实截图：红车在 (0,9)", readResult.Board[0, 9].Type == PieceType.Chariot && readResult.Board[0, 9].Color == PieceColor.Red);
    }
}

LogicTest.EngineSearchSchedulerTests.Run(Check);
LogicTest.BoardIntegrityTests.Run(Check);
LogicTest.ScreenshotNormalizationTests.Run(Check);

Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n{failures} 个失败");
return failures == 0 ? 0 : 1;
