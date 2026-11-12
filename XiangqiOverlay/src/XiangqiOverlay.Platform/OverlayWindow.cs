using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using XiangqiOverlay.Core;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DoubleCollection = System.Windows.Media.DoubleCollection;
using FontFamily = System.Windows.Media.FontFamily;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace XiangqiOverlay.Platform;

/// <summary>
/// 悬浮层窗口：在目标窗口上方以硬件加速高透明度显示分析结果与推荐着法
/// 采用标准 WPF 架构，支持真正的 32 位透明度和鼠标完全穿透，不黑屏
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Border _infoBorder = new();
    private readonly TextBlock _infoText = new();

    private double _pixelsToDip = 1.0;
    private bool _sourceInitialized;
    private bool _excludeFromCapture = true;

    private AnalysisResult? _currentResult;
    private BoardCalibration? _calibration;
    private BoardReadResult? _lastReadResult;
    private int _topMoves = 5;
    private string _ownColor = "Red";
    private bool _onlyRecommendOwnMoves = true;

    public IntPtr TargetHandle { get; private set; }

    public string OwnColor
    {
        get => _ownColor;
        set { _ownColor = value; Render(); }
    }

    public bool OnlyRecommendOwnMoves
    {
        get => _onlyRecommendOwnMoves;
        set { _onlyRecommendOwnMoves = value; Render(); }
    }

    public int TopMoves
    {
        get => _topMoves;
        set { _topMoves = value; Render(); }
    }

    public bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set
        {
            _excludeFromCapture = value;
            if (_sourceInitialized)
            {
                ApplyCapturePolicy(new WindowInteropHelper(this).Handle);
            }
        }
    }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        IsHitTestVisible = false;

        Content = _canvas;
        _canvas.Background = Brushes.Transparent;
        _canvas.IsHitTestVisible = false;

        // 初始化信息面板
        _infoBorder.Background = new SolidColorBrush(Color.FromArgb(210, 24, 24, 38));
        _infoBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(200, 100, 150, 255));
        _infoBorder.BorderThickness = new Thickness(1.0);
        _infoBorder.CornerRadius = new CornerRadius(6.0);
        _infoBorder.Padding = new Thickness(10.0, 6.0, 10.0, 6.0);
        _infoBorder.MinWidth = 220.0;
        _infoBorder.IsHitTestVisible = false;

        _infoText.Foreground = Brushes.White;
        _infoText.FontFamily = new FontFamily("Microsoft YaHei");
        _infoText.FontSize = 12.0;
        _infoText.TextWrapping = TextWrapping.NoWrap;
        _infoBorder.Child = _infoText;

        _canvas.Children.Add(_infoBorder);

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _sourceInitialized = true;
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        long exStyle = Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE).ToInt64();
        // WS_EX_TRANSPARENT (0x20) | WS_EX_TOOLWINDOW (0x80) | WS_EX_LAYERED (0x80000) | WS_EX_NOACTIVATE (0x08000000)
        exStyle |= 0x80800A0;
        Win32.SetWindowLongPtr(handle, Win32.GWL_EXSTYLE, new IntPtr(exStyle));
        ApplyCapturePolicy(handle);
    }

    private void ApplyCapturePolicy(IntPtr handle)
    {
        if (!_excludeFromCapture)
        {
            Win32.SetWindowDisplayAffinity(handle, Win32.WDA_NONE);
        }
        else if (!Win32.SetWindowDisplayAffinity(handle, Win32.WDA_EXCLUDEFROMCAPTURE))
        {
            Win32.SetWindowDisplayAffinity(handle, Win32.WDA_MONITOR);
        }
    }

    /// <summary>
    /// 跟随目标窗口的物理位置和大小，精准转换为 DIP 并显示
    /// </summary>
    public void FollowTarget(TargetWindow target)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => FollowTarget(target));
            return;
        }

        if (!Win32.TryGetClientBounds(target.Handle, out var bounds))
        {
            Hide();
            return;
        }

        TargetHandle = target.Handle;
        uint dpi = Win32.GetDpi(target.Handle);
        _pixelsToDip = 96.0 / dpi;

        Left = bounds.Left * _pixelsToDip;
        Top = bounds.Top * _pixelsToDip;
        Width = Math.Max(1.0, bounds.Width * _pixelsToDip);
        Height = Math.Max(1.0, bounds.Height * _pixelsToDip);

        if (!IsVisible)
        {
            Show();
        }

        if (_sourceInitialized)
        {
            ApplyClickThrough();
        }
    }

    /// <summary>
    /// 更新分析结果
    /// </summary>
    public void UpdateAnalysis(AnalysisResult? result)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateAnalysis(result));
            return;
        }
        _currentResult = result;
        Render();
    }

    /// <summary>
    /// 更新棋盘校准
    /// </summary>
    public void UpdateCalibration(BoardCalibration? calibration)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateCalibration(calibration));
            return;
        }
        _calibration = calibration;
        Render();
    }

    /// <summary>
    /// 更新盘面读取结果
    /// </summary>
    public void UpdateBoardReadResult(BoardReadResult? readResult)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateBoardReadResult(readResult));
            return;
        }
        _lastReadResult = readResult;
        Render();
    }

    /// <summary>
    /// 渲染悬浮层内容
    /// </summary>
    public void Render()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Render);
            return;
        }

        ClearMarkers();

        if (!IsVisible || _calibration == null || !_calibration.IsValid)
            return;

        // 1. 绘制棋子识别点（精细绿色/淡蓝色小圆点）
        if (_lastReadResult != null)
        {
            foreach (var (point, type, color) in _lastReadResult.Board.GetAllPieces())
            {
                var screenPos = _calibration.BoardToScreen(point.Col, point.Row);
                double localX = screenPos.X * _pixelsToDip;
                double localY = screenPos.Y * _pixelsToDip;

                var dot = new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = color == PieceColor.Red
                        ? new SolidColorBrush(Color.FromArgb(160, 255, 70, 70))
                        : new SolidColorBrush(Color.FromArgb(160, 60, 60, 60)),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(dot, localX - 3);
                Canvas.SetTop(dot, localY - 3);
                _canvas.Children.Add(dot);
            }
        }

        // 2. 绘制最后一手标记（黄色十字/方框）
        if (_lastReadResult?.LastMove != null)
        {
            var move = _lastReadResult.LastMove.Value;
            var pos = _calibration.BoardToScreen(move.To.Col, move.To.Row);
            double localX = pos.X * _pixelsToDip;
            double localY = pos.Y * _pixelsToDip;

            var crossH = new Line
            {
                X1 = localX - 10, Y1 = localY,
                X2 = localX + 10, Y2 = localY,
                Stroke = Brushes.Yellow,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            var crossV = new Line
            {
                X1 = localX, Y1 = localY - 10,
                X2 = localX, Y2 = localY + 10,
                Stroke = Brushes.Yellow,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(crossH);
            _canvas.Children.Add(crossV);
        }

        // 3. 绘制分析着法标记（箭头与圆形）
        bool isMyTurn = true;
        PlayerSide currentSide = _lastReadResult?.Board.CurrentPlayer ?? PlayerSide.Red;
        PlayerSide mySide = _ownColor == "Black" ? PlayerSide.Black : PlayerSide.Red;
        isMyTurn = (currentSide == mySide);

        // 当开启“仅推荐我方走法”时，若当前为对手回合，绝对不绘制走法箭头，杜绝“给对面指路”
        bool shouldDrawMoves = !(_onlyRecommendOwnMoves && !isMyTurn);

        if (shouldDrawMoves && _currentResult != null && _currentResult.Moves.Count > 0)
        {
            var moves = _currentResult.Moves.Take(_topMoves).ToList();
            for (int i = 0; i < moves.Count; i++)
            {
                var m = moves[i];
                try
                {
                    var chessMove = ChessMove.FromUcci(m.Move);
                    var fromPos = _calibration.BoardToScreen(chessMove.From.Col, chessMove.From.Row);
                    var toPos = _calibration.BoardToScreen(chessMove.To.Col, chessMove.To.Row);

                    double fx = fromPos.X * _pixelsToDip;
                    double fy = fromPos.Y * _pixelsToDip;
                    double tx = toPos.X * _pixelsToDip;
                    double ty = toPos.Y * _pixelsToDip;

                    Color circleColor = !isMyTurn
                        ? (i == 0 ? Color.FromRgb(255, 170, 0) : Color.FromRgb(180, 140, 0)) // 对手预测走法：警戒橙黄
                        : (i == 0 ? Color.FromRgb(255, 60, 60) : Color.FromRgb(40, 150, 255)); // 我方走法：红/蓝

                    double radius = i == 0 ? 18 : 14;

                    // 终点高亮圈
                    var circle = new Ellipse
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Stroke = new SolidColorBrush(circleColor),
                        StrokeThickness = i == 0 ? 3 : 2,
                        Fill = new SolidColorBrush(Color.FromArgb(50, circleColor.R, circleColor.G, circleColor.B)),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(circle, tx - radius);
                    Canvas.SetTop(circle, ty - radius);
                    _canvas.Children.Add(circle);

                    // 起点到终点的移动线
                    var line = new Line
                    {
                        X1 = fx, Y1 = fy,
                        X2 = tx, Y2 = ty,
                        Stroke = new SolidColorBrush(circleColor),
                        StrokeThickness = i == 0 ? 3 : 2,
                        IsHitTestVisible = false
                    };
                    if (i > 0 || !isMyTurn)
                    {
                        line.StrokeDashArray = new DoubleCollection { 3, 2 };
                    }
                    _canvas.Children.Add(line);

                    // 着法序号标记
                    var numText = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        Foreground = Brushes.White,
                        FontWeight = FontWeights.Bold,
                        FontSize = i == 0 ? 12 : 10,
                        TextAlignment = TextAlignment.Center,
                        Width = radius * 2,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(numText, tx - radius);
                    Canvas.SetTop(numText, ty - 7);
                    _canvas.Children.Add(numText);
                }
                catch
                {
                }
            }
        }

        // 4. 更新并定位右上角信息卡片
        UpdateInfoPanel();
    }

    private void UpdateInfoPanel()
    {
        if (_currentResult == null && _lastReadResult == null)
        {
            _infoBorder.Visibility = Visibility.Collapsed;
            return;
        }

        _infoBorder.Visibility = Visibility.Visible;

        PlayerSide currentSide = _lastReadResult?.Board.CurrentPlayer ?? PlayerSide.Red;
        PlayerSide mySide = _ownColor == "Black" ? PlayerSide.Black : PlayerSide.Red;
        bool isMyTurn = (currentSide == mySide);

        if (_currentResult != null)
        {
            double winPct = _currentResult.WinRate * 100;
            string best = _currentResult.BestMove?.Move ?? "—";
            string mySideStr = mySide == PlayerSide.Black ? "黑方" : "红方";
            string sideStr = currentSide == PlayerSide.Black ? "黑方" : "红方";

            if (_onlyRecommendOwnMoves && !isMyTurn)
            {
                _infoText.Text = $"{_currentResult.EngineName}  ·  [等待对手走棋...]  ·  我方执子: {mySideStr}\n" +
                                 $"当前状态: 对手思考中，轮到我方落子时将自动给出最佳招法";
            }
            else
            {
                string prefix = isMyTurn ? $"[{sideStr}推荐]" : $"[预测{sideStr}]";

                string pieceDesc = "";
                if (_currentResult.BestMove != null && _lastReadResult != null)
                {
                    try
                    {
                        var cm = ChessMove.FromUcci(_currentResult.BestMove.Move);
                        var p = _lastReadResult.Board[cm.From];
                        string pName = GetPieceName(p.Type, p.Color);
                        if (!string.IsNullOrEmpty(pName))
                            pieceDesc = $"[{pName}] ";
                    }
                    catch {}
                }

                _infoText.Text = $"{_currentResult.EngineName}  ·  {prefix}  ·  深度: {_currentResult.Depth}\n" +
                                 $"推荐: {pieceDesc}{best}   胜率: {winPct:F1}%   评分: {_currentResult.Score}";
            }
        }

        // 右上角定位
        Canvas.SetRight(_infoBorder, 12);
        Canvas.SetTop(_infoBorder, 12);
    }

    private static string GetPieceName(PieceType type, PieceColor color) => (type, color) switch
    {
        (PieceType.King, PieceColor.Red) => "帅",
        (PieceType.King, PieceColor.Black) => "将",
        (PieceType.Advisor, PieceColor.Red) => "仕",
        (PieceType.Advisor, PieceColor.Black) => "士",
        (PieceType.Elephant, PieceColor.Red) => "相",
        (PieceType.Elephant, PieceColor.Black) => "象",
        (PieceType.Horse, PieceColor.Red) => "傌",
        (PieceType.Horse, PieceColor.Black) => "马",
        (PieceType.Chariot, PieceColor.Red) => "俥",
        (PieceType.Chariot, PieceColor.Black) => "车",
        (PieceType.Cannon, PieceColor.Red) => "炮",
        (PieceType.Cannon, PieceColor.Black) => "砲",
        (PieceType.Pawn, PieceColor.Red) => "兵",
        (PieceType.Pawn, PieceColor.Black) => "卒",
        _ => ""
    };

    private void ClearMarkers()
    {
        var elementsToRemove = _canvas.Children
            .Cast<UIElement>()
            .Where(el => el != _infoBorder)
            .ToList();

        foreach (var el in elementsToRemove)
        {
            _canvas.Children.Remove(el);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        TargetHandle = IntPtr.Zero;
    }
}
