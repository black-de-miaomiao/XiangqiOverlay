using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using XiangqiOverlay.Core;
using XiangqiOverlay.Platform;

namespace XiangqiOverlay.App;

/// <summary>
/// MainWindow.xaml 的交互逻辑
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainController _controller;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private BoardState? _lastBoard;
    private AnalysisResult? _lastAnalysis;

    public MainWindow(MainController controller, AppSettings settings, string settingsPath)
    {
        _controller = controller;
        _settings = settings;
        _settingsPath = settingsPath;
        InitializeComponent();

        PreviewCanvas.SizeChanged += (_, _) => DrawBoardPreview();

        // 绑定事件
        _controller.StatusChanged += OnStatusChanged;
        _controller.AnalysisReceived += OnAnalysisReceived;
        _controller.BoardRead += OnBoardRead;
        _controller.ErrorOccurred += OnErrorOccurred;

        // 初始化引擎设置 UI
        EnginePathBox.Text = settings.PikafishExecutable;
        EvalFileBox.Text = settings.PikafishEvalFile;
        ThreadsCombo.SelectedIndex = settings.Threads switch
        {
            1 => 0, 2 => 1, 3 => 2, 4 => 3, 6 => 4, 8 => 5, _ => 3
        };
        DepthCombo.SelectedIndex = settings.MaxDepth switch
        {
            12 => 0, 16 => 1, 24 => 2, 30 => 3, 0 => 4, _ => 2
        };
        ThinkTimeCombo.SelectedIndex = settings.MaxTimeMs switch
        {
            1000 => 0, 2000 => 1, 6000 => 3, _ => 2
        };
        TopMovesCombo.SelectedIndex = settings.TopMoves switch
        {
            1 => 0, 2 => 1, 3 => 2, 4 => 3, 5 => 4, _ => 4
        };
        ExcludeOverlayCheck.IsChecked = settings.ExcludeOverlayFromCapture;

        // 执子设置
        ColorCombo.SelectedIndex = settings.OwnColor == "Black" ? 1 : 0;
        PlayerCombo.SelectedIndex = settings.CurrentPlayer == "Black" ? 1 : 0;
        OnlyOwnMovesCheck.IsChecked = settings.OnlyRecommendOwnMoves;
        OnlyOwnMovesCheck.Checked += OnOnlyOwnMovesChanged;
        OnlyOwnMovesCheck.Unchecked += OnOnlyOwnMovesChanged;
        ReadModeCombo.SelectedIndex = settings.BoardReadMode switch
        {
            "VisionOnly" => 1, _ => 0
        };

        _controller.CurrentPlayerChanged += OnCurrentPlayerChanged;
        ColorCombo.SelectionChanged += OnColorSelectionChanged;
        PlayerCombo.SelectionChanged += OnPlayerSelectionChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 初始化引擎（异步，不阻塞 UI）
        StatusText.Text = "正在初始化 Pikafish 引擎...";
        Task.Run(() => _controller.Initialize()).ContinueWith(t =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (t.Result)
                    StatusText.Text = "Pikafish 引擎就绪，请选择目标窗口";
                else
                    StatusText.Text = "引擎初始化失败，请检查 Pikafish 路径";
            });
        });

        // 刷新窗口列表
        RefreshWindows();

        // 绘制初始棋盘预览
        DrawBoardPreview();

        // 验证渲染输出
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Max(1, ActualWidth), (int)Math.Max(1, ActualHeight),
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                string debugDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XiangqiOverlay");
                System.IO.Directory.CreateDirectory(debugDir);
                string path = System.IO.Path.Combine(debugDir, "main_window_rendered.png");
                using var fs = System.IO.File.Create(path);
                encoder.Save(fs);
            }
            catch {}
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _controller.Dispose();
    }

    /// <summary>
    /// 刷新窗口列表
    /// </summary>
    private void RefreshWindows()
    {
        WindowCombo.Items.Clear();
        var windows = _controller.GetWindows();
        foreach (var w in windows)
        {
            WindowCombo.Items.Add(new TargetWindowItem(w));
        }

        // 自动尝试查找真实 象棋 / JJ / 微信小程序 窗口（排除自身）
        TargetWindowItem? bestMatch = null;
        foreach (TargetWindowItem item in WindowCombo.Items)
        {
            if (item.Window.Title.Contains("XiangqiOverlay", StringComparison.OrdinalIgnoreCase))
                continue;

            if (item.Window.ProcessName.Contains("WeChatAppEx", StringComparison.OrdinalIgnoreCase) ||
                item.Window.Title.Contains("象棋", StringComparison.OrdinalIgnoreCase) ||
                item.Window.Title.Contains("JJ", StringComparison.OrdinalIgnoreCase))
            {
                bestMatch = item;
                break;
            }
            if (bestMatch == null && item.Window.ProcessName.Contains("WeChat", StringComparison.OrdinalIgnoreCase))
            {
                bestMatch = item;
            }
        }

        if (bestMatch != null)
        {
            WindowCombo.SelectedItem = bestMatch;
        }
        else if (WindowCombo.Items.Count > 0)
        {
            // 如果只有系统窗口如设置，不要强选系统窗口误导用户，若选中则提示
            WindowCombo.SelectedIndex = 0;
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshWindows();

    private void OnWindowSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (WindowCombo.SelectedItem is TargetWindowItem item)
        {
            _controller.SetTargetWindow(item.Window);
        }
    }

    private void OnAutoCalibrateClick(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            bool ok = _controller.AutoCalibrate();
            if (!ok)
                System.Windows.MessageBox.Show("自动校准失败，请确保棋盘完整显示后重试，或点击【手动框选棋盘】拉取范围。",
                    "校准提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnManualCalibrateClick(object sender, RoutedEventArgs e)
    {
        var target = _controller.TargetWindow;
        if (target == null)
        {
            System.Windows.MessageBox.Show("请先在上方下拉框选择目标游戏窗口！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Win32.TryGetClientBounds(target.Handle, out var clientBounds))
        {
            System.Windows.MessageBox.Show("无法获取目标窗口位置，请确认目标窗口未最小化。", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        uint dpi = Win32.GetDpi(target.Handle);
        var selector = new SelectionOverlayWindow(clientBounds, dpi, this);
        selector.Confirmed += (_, cal) =>
        {
            _controller.SetCalibration(cal);
            StatusText.Text = "手动框选完成，棋盘范围已锁定！";
        };
        selector.ShowDialog();
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        _controller.Start();
        // Start 内部校准失败时不会进入运行态，此时恢复按钮可用，避免卡死在"已开始"
        bool running = _controller.IsRunning;
        StartBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _controller.Stop();
        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        _lastBoard = null;
        _lastAnalysis = null;
        DrawBoardPreview();
    }

    private void OnRequestAnalysisClick(object sender, RoutedEventArgs e)
    {
        _controller.RequestAnalysis();
    }

    private void OnSaveSettingsClick(object sender, RoutedEventArgs e)
    {
        _settings.PikafishExecutable = EnginePathBox.Text;
        _settings.PikafishEvalFile = EvalFileBox.Text;
        _settings.Threads = int.Parse(((System.Windows.Controls.ComboBoxItem)ThreadsCombo.SelectedItem).Content.ToString()!);
        _settings.MaxDepth = ((System.Windows.Controls.ComboBoxItem)DepthCombo.SelectedItem).Content.ToString() == "0（无限）"
            ? 0 : int.Parse(((System.Windows.Controls.ComboBoxItem)DepthCombo.SelectedItem).Content.ToString()!);
        _settings.MaxTimeMs = long.Parse(((System.Windows.Controls.ComboBoxItem)ThinkTimeCombo.SelectedItem).Tag.ToString()!);
        _settings.TopMoves = int.Parse(((System.Windows.Controls.ComboBoxItem)TopMovesCombo.SelectedItem).Content.ToString()!);
        _settings.ExcludeOverlayFromCapture = ExcludeOverlayCheck.IsChecked ?? true;
        _settings.OwnColor = ((System.Windows.Controls.ComboBoxItem)ColorCombo.SelectedItem).Tag.ToString()!;
        _settings.CurrentPlayer = ((System.Windows.Controls.ComboBoxItem)PlayerCombo.SelectedItem).Tag.ToString()!;
        _settings.OnlyRecommendOwnMoves = OnlyOwnMovesCheck.IsChecked ?? true;
        _settings.BoardReadMode = ((System.Windows.Controls.ComboBoxItem)ReadModeCombo.SelectedItem).Tag.ToString()!;

        _settings.Save(_settingsPath);
        System.Windows.MessageBox.Show("配置已保存", "保存", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOnlyOwnMovesChanged(object sender, RoutedEventArgs e)
    {
        if (_settings == null || _controller == null || OnlyOwnMovesCheck == null)
            return;
        bool onlyOwn = OnlyOwnMovesCheck.IsChecked ?? true;
        _settings.OnlyRecommendOwnMoves = onlyOwn;
        _controller.SetOnlyRecommendOwnMoves(onlyOwn);
        UpdateRecommendationPrompt();
        DrawBoardPreview();
    }

    // 事件处理
    private void OnStatusChanged(string message)
    {
        Dispatcher.BeginInvoke(() => StatusText.Text = message);
    }

    private bool _isInternalComboUpdate;

    private void OnCurrentPlayerChanged(PlayerSide side)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _isInternalComboUpdate = true;
            try
            {
                PlayerCombo.SelectedIndex = side == PlayerSide.Black ? 1 : 0;
            }
            finally
            {
                _isInternalComboUpdate = false;
            }
        });
    }

    private void OnColorSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ColorCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        {
            string color = item.Tag?.ToString() ?? "Red";
            _settings.OwnColor = color;
            _controller.SetOwnColor(color);
            DrawBoardPreview();
        }
    }

    private void OnPlayerSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isInternalComboUpdate) return;
        if (PlayerCombo.SelectedItem is System.Windows.Controls.ComboBoxItem item)
        {
            string player = item.Tag?.ToString() ?? "Red";
            var side = player == "Black" ? PlayerSide.Black : PlayerSide.Red;
            _settings.CurrentPlayer = player;
            _controller.SetCurrentPlayer(side);
            if (_lastBoard != null)
            {
                _lastBoard.CurrentPlayer = side;
                _lastAnalysis = null;
                UpdateRecommendationPrompt();
            }
            DrawBoardPreview();
        }
    }

    private void OnAnalysisReceived(AnalysisResult result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_lastBoard == null || result.Fen != _lastBoard.ToFen())
                return;

            _lastAnalysis = result;
            UpdateRecommendationPrompt();
            DrawBoardPreview();
        });
    }

    private void UpdateRecommendationPrompt()
    {
        if (_lastBoard == null)
        {
            BestMoveText.Text = "—";
            ScoreText.Text = "评分: —";
            WinRateText.Text = "胜率: —";
            DepthText.Text = "深度: —";
            PvText.Text = "";
            return;
        }

        var mySide = _settings.OwnColor == "Black" ? PlayerSide.Black : PlayerSide.Red;
        string mySideStr = mySide == PlayerSide.Black ? "黑方" : "红方";
        string playerStr = _lastBoard.CurrentPlayer == PlayerSide.Black ? "黑方" : "红方";
        var kind = RecommendationPromptPolicy.Resolve(
            _lastBoard, mySide, _settings.OnlyRecommendOwnMoves, _lastAnalysis);
        var current = _lastAnalysis != null && _lastAnalysis.Fen == _lastBoard.ToFen()
            ? _lastAnalysis : null;

        if (kind == RecommendationPromptKind.WaitingForOpponent)
        {
            BestMoveText.Text = $"[等待对手走棋...] 我方执子: {mySideStr}";
            PvText.Text = "当前为对手思考，轮到我方落子时将自动给出最佳招法";
            SetAnalysisMetrics(null);
            return;
        }

        if (kind == RecommendationPromptKind.Thinking || current?.BestMove == null)
        {
            BestMoveText.Text = mySide == _lastBoard.CurrentPlayer
                ? $"[正在思考...] 我方执子: {mySideStr}"
                : $"[正在分析{playerStr}走法...]";
            PvText.Text = "正在分析最新盘面，旧结果不会用于当前棋盘";
            SetAnalysisMetrics(null);
            return;
        }

        var best = current!.BestMove!;
        string pieceName = "";
        try
        {
            var move = ChessMove.FromUcci(best.Move);
            string text = GetPieceText(_lastBoard[move.From].Type, _lastBoard[move.From].Color);
            if (!string.IsNullOrEmpty(text))
                pieceName = $"[{text}] ";
        }
        catch { }

        BestMoveText.Text = kind == RecommendationPromptKind.OwnRecommendation
            ? $"[{playerStr}推荐] {pieceName}{best.Move}"
            : $"[预测{playerStr}走法] {pieceName}{best.Move}";
        PvText.Text = string.IsNullOrEmpty(best.PvString)
            ? "" : $"变着: {best.PvString}";
        SetAnalysisMetrics(current);
    }

    private void SetAnalysisMetrics(AnalysisResult? result)
    {
        ScoreText.Text = result == null ? "评分: —" : $"评分: {result.Score}";
        WinRateText.Text = result == null ? "胜率: —" : $"胜率: {result.WinRate * 100:F1}%";
        DepthText.Text = result == null ? "深度: —" : $"深度: {result.Depth}";
    }

    private void OnBoardRead(BoardReadResult result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!result.Success)
            {
                StatusText.Text = $"读取失败: {result.Error}";
            }
            else
            {
                _lastBoard = result.Board;
                if (_lastAnalysis?.Fen != _lastBoard.ToFen())
                    _lastAnalysis = null;
                UpdateRecommendationPrompt();
                int redCount = 0, blackCount = 0;
                foreach (var (_, _, color) in _lastBoard.GetAllPieces())
                {
                    if (color == PieceColor.Red) redCount++;
                    else if (color == PieceColor.Black) blackCount++;
                }
                string lastMoveText = result.LastMove is { } lastMove
                    ? $"，走子 {lastMove.From}->{lastMove.To}" : "";
                string nextSideText = _lastBoard.CurrentPlayer == PlayerSide.Red ? "红方" : "黑方";
                StatusText.Text = $"盘面已同步 (红方 {redCount} 子, 黑方 {blackCount} 子){lastMoveText}，轮到{nextSideText}";
                DrawBoardPreview();
            }
        });
    }

    private void DrawBoardPreview()
    {
        if (PreviewCanvas == null)
            return;

        PreviewCanvas.Children.Clear();
        double w = PreviewCanvas.ActualWidth;
        double h = PreviewCanvas.ActualHeight;
        if (w <= 60 || h <= 60)
            return;

        double pad = 24.0;
        double cell = Math.Min((w - pad * 2) / 8.0, (h - pad * 2) / 9.0);
        double boardW = cell * 8.0;
        double boardH = cell * 9.0;
        double ox = (w - boardW) / 2.0;
        double oy = (h - boardH) / 2.0;

        // 棋盘背景底板
        var bg = new System.Windows.Shapes.Rectangle
        {
            Width = boardW + 24,
            Height = boardH + 24,
            Fill = new SolidColorBrush(Color.FromRgb(32, 32, 46)),
            Stroke = new SolidColorBrush(Color.FromRgb(60, 60, 90)),
            StrokeThickness = 2,
            RadiusX = 8,
            RadiusY = 8
        };
        System.Windows.Controls.Canvas.SetLeft(bg, ox - 12);
        System.Windows.Controls.Canvas.SetTop(bg, oy - 12);
        PreviewCanvas.Children.Add(bg);

        var gridBrush = new SolidColorBrush(Color.FromRgb(95, 95, 125));

        // 10 条横线
        for (int r = 0; r < 10; r++)
        {
            var line = new System.Windows.Shapes.Line
            {
                X1 = ox, Y1 = oy + r * cell,
                X2 = ox + boardW, Y2 = oy + r * cell,
                Stroke = gridBrush,
                StrokeThickness = 1
            };
            PreviewCanvas.Children.Add(line);
        }

        // 9 条竖线
        for (int c = 0; c < 9; c++)
        {
            if (c == 0 || c == 8)
            {
                var line = new System.Windows.Shapes.Line
                {
                    X1 = ox + c * cell, Y1 = oy,
                    X2 = ox + c * cell, Y2 = oy + boardH,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                PreviewCanvas.Children.Add(line);
            }
            else
            {
                var line1 = new System.Windows.Shapes.Line
                {
                    X1 = ox + c * cell, Y1 = oy,
                    X2 = ox + c * cell, Y2 = oy + 4 * cell,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                PreviewCanvas.Children.Add(line1);

                var line2 = new System.Windows.Shapes.Line
                {
                    X1 = ox + c * cell, Y1 = oy + 5 * cell,
                    X2 = ox + c * cell, Y2 = oy + boardH,
                    Stroke = gridBrush,
                    StrokeThickness = 1
                };
                PreviewCanvas.Children.Add(line2);
            }
        }

        // 九宫斜线
        AddDiagLine(ox + 3 * cell, oy, ox + 5 * cell, oy + 2 * cell, gridBrush);
        AddDiagLine(ox + 5 * cell, oy, ox + 3 * cell, oy + 2 * cell, gridBrush);
        AddDiagLine(ox + 3 * cell, oy + 7 * cell, ox + 5 * cell, oy + 9 * cell, gridBrush);
        AddDiagLine(ox + 5 * cell, oy + 7 * cell, ox + 3 * cell, oy + 9 * cell, gridBrush);

        // 楚河汉界文字
        var riverText = new System.Windows.Controls.TextBlock
        {
            Text = "楚 河           汉 界",
            Foreground = new SolidColorBrush(Color.FromArgb(120, 150, 150, 180)),
            FontSize = Math.Max(11, cell * 0.38),
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            Width = boardW
        };
        System.Windows.Controls.Canvas.SetLeft(riverText, ox);
        System.Windows.Controls.Canvas.SetTop(riverText, oy + 4.35 * cell);
        PreviewCanvas.Children.Add(riverText);

        if (_lastBoard == null)
        {
            var tip = new System.Windows.Controls.TextBlock
            {
                Text = "选择目标窗口后点击【开始】进行棋盘分析",
                Foreground = new SolidColorBrush(Color.FromArgb(180, 170, 170, 200)),
                FontSize = 13,
                TextAlignment = TextAlignment.Center,
                Width = boardW
            };
            System.Windows.Controls.Canvas.SetLeft(tip, ox);
            System.Windows.Controls.Canvas.SetTop(tip, oy + 2.2 * cell);
            PreviewCanvas.Children.Add(tip);
            return;
        }

        // 绘制棋子
        double pr = cell * 0.42;
        foreach (var (pt, type, color) in _lastBoard.GetAllPieces())
        {
            double cx = ox + pt.Col * cell;
            double cy = oy + pt.Row * cell;

            bool isRed = color == PieceColor.Red;
            var circle = new System.Windows.Shapes.Ellipse
            {
                Width = pr * 2,
                Height = pr * 2,
                Fill = isRed ? new SolidColorBrush(Color.FromRgb(245, 235, 220)) : new SolidColorBrush(Color.FromRgb(225, 220, 210)),
                Stroke = isRed ? new SolidColorBrush(Color.FromRgb(200, 50, 50)) : new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                StrokeThickness = 2
            };
            System.Windows.Controls.Canvas.SetLeft(circle, cx - pr);
            System.Windows.Controls.Canvas.SetTop(circle, cy - pr);
            PreviewCanvas.Children.Add(circle);

            string text = GetPieceText(type, color);
            var textBlock = new System.Windows.Controls.TextBlock
            {
                Text = text,
                Foreground = isRed ? new SolidColorBrush(Color.FromRgb(200, 40, 40)) : new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                FontSize = pr * 1.1,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Microsoft YaHei"),
                Width = pr * 2,
                TextAlignment = TextAlignment.Center
            };
            System.Windows.Controls.Canvas.SetLeft(textBlock, cx - pr);
            System.Windows.Controls.Canvas.SetTop(textBlock, cy - pr * 0.82);
            PreviewCanvas.Children.Add(textBlock);
        }

        // 推荐走法
        var curSide = _lastBoard?.CurrentPlayer ?? PlayerSide.Red;
        var mySidePreview = _settings.OwnColor == "Black" ? PlayerSide.Black : PlayerSide.Red;
        bool isMyTurnPreview = (curSide == mySidePreview);
        bool shouldDrawPreviewMove = !(_settings.OnlyRecommendOwnMoves && !isMyTurnPreview);
        bool hasCurrentAnalysis = _lastAnalysis?.BestMove != null &&
            _lastBoard != null && _lastAnalysis.Fen == _lastBoard.ToFen();

        if (shouldDrawPreviewMove && hasCurrentAnalysis)
        {
            try
            {
                var mv = ChessMove.FromUcci(_lastAnalysis!.BestMove!.Move);
                double fx = ox + mv.From.Col * cell;
                double fy = oy + mv.From.Row * cell;
                double tx = ox + mv.To.Col * cell;
                double ty = oy + mv.To.Row * cell;

                var strokeColor = isMyTurnPreview
                    ? Color.FromArgb(240, 255, 60, 60)
                    : Color.FromArgb(230, 255, 170, 0);

                var bestLine = new System.Windows.Shapes.Line
                {
                    X1 = fx, Y1 = fy, X2 = tx, Y2 = ty,
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = 3
                };
                if (!isMyTurnPreview)
                {
                    bestLine.StrokeDashArray = new DoubleCollection { 3, 2 };
                }
                PreviewCanvas.Children.Add(bestLine);

                var bestCircle = new System.Windows.Shapes.Ellipse
                {
                    Width = pr * 2.2, Height = pr * 2.2,
                    Stroke = new SolidColorBrush(strokeColor),
                    StrokeThickness = 3
                };
                System.Windows.Controls.Canvas.SetLeft(bestCircle, tx - pr * 1.1);
                System.Windows.Controls.Canvas.SetTop(bestCircle, ty - pr * 1.1);
                PreviewCanvas.Children.Add(bestCircle);
            }
            catch {}
        }
    }

    private void AddDiagLine(double x1, double y1, double x2, double y2, System.Windows.Media.Brush brush)
    {
        PreviewCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush, StrokeThickness = 1
        });
    }

    private static string GetPieceText(PieceType type, PieceColor color) => (type, color) switch
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

    private void OnErrorOccurred(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = $"错误: {message}";
            System.Diagnostics.Debug.WriteLine($"错误: {message}");
        });
    }
}

/// <summary>
/// ComboBox 中显示的目标窗口项
/// </summary>
internal sealed class TargetWindowItem
{
    public TargetWindow Window { get; }
    public override string ToString() => Window.ToString();

    public TargetWindowItem(TargetWindow window) => Window = window;
}
