using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using XiangqiOverlay.Core;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Cursors = System.Windows.Input.Cursors;

namespace XiangqiOverlay.Platform;

/// <summary>
/// 手动框选棋盘的半透明交互窗口
/// </summary>
public sealed class SelectionOverlayWindow : Window
{
    private readonly Canvas _canvas = new Canvas();
    private readonly Polygon _polygon = new Polygon();
    private readonly Ellipse[] _handles = new Ellipse[4];
    private readonly TextBlock _tipText = new TextBlock();
    private readonly uint _dpi;
    private System.Windows.Point? _dragStart;
    private int _activeHandle = -1;
    private System.Windows.Point[]? _points;

    public event EventHandler<BoardCalibration>? Confirmed;

    public SelectionOverlayWindow(System.Drawing.Rectangle clientBounds, uint dpi, Window? owner = null)
    {
        _dpi = dpi > 0 ? dpi : 96u;
        base.Owner = owner;
        base.WindowStyle = WindowStyle.None;
        base.ResizeMode = ResizeMode.NoResize;
        base.AllowsTransparency = true;
        base.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 0, 0, 0));
        base.ShowInTaskbar = false;
        base.Topmost = true;
        base.ShowActivated = true;

        double scale = 96.0 / _dpi;
        base.Width = clientBounds.Width * scale;
        base.Height = clientBounds.Height * scale;
        base.Left = clientBounds.Left * scale;
        base.Top = clientBounds.Top * scale;
        base.Content = _canvas;

        _canvas.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 0, 0, 0));
        _canvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseLeftButtonUp;

        _polygon.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(80, 235, 170));
        _polygon.StrokeThickness = 2.0;
        _polygon.Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(20, 80, 235, 170));
        _canvas.Children.Add(_polygon);

        // 创建9列内部纵线与10行内部横线
        for (int i = 0; i < 9 + 10; i++)
        {
            var line = new Line
            {
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(160, 80, 235, 170)),
                StrokeThickness = 1.2,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
            _gridLines.Add(line);
            _canvas.Children.Add(line);
        }

        // 创建四角交叉点大圆环
        for (int i = 0; i < _handles.Length; i++)
        {
            var ellipse = new Ellipse
            {
                Width = 22.0,
                Height = 22.0,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 80, 235, 170)),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 2.5,
                Visibility = Visibility.Collapsed,
                Cursor = Cursors.SizeAll
            };
            ellipse.Tag = i;
            _canvas.Children.Add(ellipse);
            _handles[i] = ellipse;
        }

        // 提示文字
        _tipText.Text = "【手动对齐棋盘】在游戏界面拖拽矩形，并微调 4 个绿色圆环\n确保内部网格与游戏棋盘交叉线完全贴合（4个角对齐4个角的车/俥交叉点）\n按【Enter】锁定棋盘并立即开始识别，按【Esc】取消";
        _tipText.Foreground = System.Windows.Media.Brushes.White;
        _tipText.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(210, 20, 20, 35));
        _tipText.Padding = new Thickness(14, 10, 14, 10);
        _tipText.FontSize = 14;
        _tipText.FontWeight = FontWeights.SemiBold;
        Canvas.SetLeft(_tipText, 20);
        Canvas.SetTop(_tipText, 20);
        _canvas.Children.Add(_tipText);

        base.KeyDown += OnKeyDown;
    }

    private readonly List<Line> _gridLines = new();

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (_points != null)
        {
            _activeHandle = FindHandle(pos);
            if (_activeHandle >= 0)
            {
                _canvas.CaptureMouse();
                e.Handled = true;
                return;
            }
        }
        _dragStart = pos;
        _points = null;
        RenderSelection();
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (_activeHandle >= 0 && _points != null)
        {
            _points[_activeHandle] = ClampToCanvas(pos);
            RenderSelection();
            return;
        }

        if (_dragStart.HasValue)
        {
            var start = _dragStart.Value;
            var cur = ClampToCanvas(pos);
            _points = new System.Windows.Point[4]
            {
                new System.Windows.Point(Math.Min(start.X, cur.X), Math.Min(start.Y, cur.Y)), // TL
                new System.Windows.Point(Math.Max(start.X, cur.X), Math.Min(start.Y, cur.Y)), // TR
                new System.Windows.Point(Math.Max(start.X, cur.X), Math.Max(start.Y, cur.Y)), // BR
                new System.Windows.Point(Math.Min(start.X, cur.X), Math.Max(start.Y, cur.Y))  // BL
            };
            RenderSelection();
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _activeHandle = -1;
        _canvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
        else if (e.Key == Key.Return)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private void ConfirmSelection()
    {
        if (_points != null && _points.Length == 4)
        {
            double scale = (double)_dpi / 96.0;
            // 物理像素坐标
            var p0 = new ScreenPoint(_points[0].X * scale, _points[0].Y * scale); // TL
            var p1 = new ScreenPoint(_points[1].X * scale, _points[1].Y * scale); // TR
            var p2 = new ScreenPoint(_points[2].X * scale, _points[2].Y * scale); // BR
            var p3 = new ScreenPoint(_points[3].X * scale, _points[3].Y * scale); // BL

            double w = (p1 - p0).Length;
            double h = (p3 - p0).Length;
            if (w >= 80 && h >= 80)
            {
                var cal = new BoardCalibration
                {
                    TopLeft = p0,
                    TopRight = p1,
                    BottomRight = p2,
                    BottomLeft = p3,
                    IsValid = true,
                    Timestamp = DateTime.Now
                };
                Confirmed?.Invoke(this, cal);
                DialogResult = true;
                Close();
            }
        }
    }

    private int FindHandle(System.Windows.Point pt)
    {
        for (int i = 0; i < _handles.Length; i++)
        {
            if (_points != null && (_points[i] - pt).Length <= 18.0)
                return i;
        }
        return -1;
    }

    private System.Windows.Point ClampToCanvas(System.Windows.Point pt) =>
        new System.Windows.Point(Math.Clamp(pt.X, 0.0, ActualWidth), Math.Clamp(pt.Y, 0.0, ActualHeight));

    private void RenderSelection()
    {
        bool has = _points != null && _points.Length == 4;
        _polygon.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _handles.Length; i++)
            _handles[i].Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (has && _points != null)
        {
            _polygon.Points = new PointCollection(_points);
            for (int j = 0; j < _handles.Length; j++)
            {
                Canvas.SetLeft(_handles[j], _points[j].X - _handles[j].Width / 2.0);
                Canvas.SetTop(_handles[j], _points[j].Y - _handles[j].Height / 2.0);
            }

            var p0 = _points[0]; // TL
            var p1 = _points[1]; // TR
            var p2 = _points[2]; // BR
            var p3 = _points[3]; // BL

            int lineIdx = 0;
            // 9 条纵线 (col 0..8)
            for (int c = 0; c < 9 && lineIdx < _gridLines.Count; c++)
            {
                double tx = c / 8.0;
                double topX = (1 - tx) * p0.X + tx * p1.X;
                double topY = (1 - tx) * p0.Y + tx * p1.Y;
                double botX = (1 - tx) * p3.X + tx * p2.X;
                double botY = (1 - tx) * p3.Y + tx * p2.Y;

                var line = _gridLines[lineIdx++];
                line.X1 = topX; line.Y1 = topY;
                line.X2 = botX; line.Y2 = botY;
                line.Visibility = Visibility.Visible;
            }

            // 10 条横线 (row 0..9)
            for (int r = 0; r < 10 && lineIdx < _gridLines.Count; r++)
            {
                double ty = r / 9.0;
                double leftX = (1 - ty) * p0.X + ty * p3.X;
                double leftY = (1 - ty) * p0.Y + ty * p3.Y;
                double rightX = (1 - ty) * p1.X + ty * p2.X;
                double rightY = (1 - ty) * p1.Y + ty * p2.Y;

                var line = _gridLines[lineIdx++];
                line.X1 = leftX; line.Y1 = leftY;
                line.X2 = rightX; line.Y2 = rightY;
                line.Visibility = Visibility.Visible;
            }
        }
        else
        {
            foreach (var line in _gridLines)
                line.Visibility = Visibility.Collapsed;
        }
    }
}
