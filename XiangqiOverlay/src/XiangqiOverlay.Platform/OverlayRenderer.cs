using System.Windows;
using Application = System.Windows.Application;
using XiangqiOverlay.Core;

namespace XiangqiOverlay.Platform;

/// <summary>
/// 悬浮层管理器：在目标窗口上方渲染分析结果
/// 内部委托给真正的 WPF OverlayWindow，彻底消除黑屏
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private OverlayWindow? _overlayWindow;
    private bool _disposed;
    private TargetWindow? _currentTarget;
    private int _topMoves = 5;
    private bool _excludeFromCapture = true;

    private string _ownColor = "Red";
    private bool _onlyRecommendOwnMoves = true;

    public string OwnColor
    {
        get => _ownColor;
        set
        {
            _ownColor = value;
            if (_overlayWindow != null)
                _overlayWindow.OwnColor = value;
        }
    }

    public bool OnlyRecommendOwnMoves
    {
        get => _onlyRecommendOwnMoves;
        set
        {
            _onlyRecommendOwnMoves = value;
            if (_overlayWindow != null)
                _overlayWindow.OnlyRecommendOwnMoves = value;
        }
    }

    public int TopMoves
    {
        get => _topMoves;
        set
        {
            _topMoves = value;
            if (_overlayWindow != null)
                _overlayWindow.TopMoves = value;
        }
    }

    public bool ExcludeFromCapture
    {
        get => _excludeFromCapture;
        set
        {
            _excludeFromCapture = value;
            if (_overlayWindow != null)
                _overlayWindow.ExcludeFromCapture = value;
        }
    }

    public OverlayRenderer()
    {
        EnsureWindowCreated();
    }

    private void EnsureWindowCreated()
    {
        if (_overlayWindow != null || _disposed)
            return;

        var app = Application.Current;
        if (app != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(EnsureWindowCreated);
            return;
        }

        _overlayWindow = new OverlayWindow
        {
            TopMoves = _topMoves,
            ExcludeFromCapture = _excludeFromCapture,
            OwnColor = _ownColor,
            OnlyRecommendOwnMoves = _onlyRecommendOwnMoves
        };
    }

    public void FollowTarget(TargetWindow target)
    {
        _currentTarget = target;
        EnsureWindowCreated();
        _overlayWindow?.FollowTarget(target);
    }

    public void UpdateAnalysis(AnalysisResult? result)
    {
        EnsureWindowCreated();
        _overlayWindow?.UpdateAnalysis(result);
    }

    public void UpdateCalibration(BoardCalibration? calibration)
    {
        EnsureWindowCreated();
        _overlayWindow?.UpdateCalibration(calibration);
    }

    public void UpdateBoardReadResult(BoardReadResult? readResult)
    {
        EnsureWindowCreated();
        _overlayWindow?.UpdateBoardReadResult(readResult);
    }

    public void UpdatePosition(int x, int y, int width, int height)
    {
        if (_currentTarget != null)
        {
            FollowTarget(_currentTarget);
        }
    }

    public void Show()
    {
        EnsureWindowCreated();
        if (_overlayWindow != null)
        {
            var app = Application.Current;
            Action act = () =>
            {
                if (_currentTarget != null)
                    _overlayWindow.FollowTarget(_currentTarget);
                _overlayWindow.Show();
            };

            if (app != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(act);
            else
                act();
        }
    }

    public void Hide()
    {
        if (_overlayWindow != null)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(() => _overlayWindow.Hide());
            else
                _overlayWindow.Hide();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() =>
                {
                    _overlayWindow?.Close();
                    _overlayWindow = null;
                });
            }
            else
            {
                _overlayWindow?.Close();
                _overlayWindow = null;
            }
            _disposed = true;
        }
    }
}
