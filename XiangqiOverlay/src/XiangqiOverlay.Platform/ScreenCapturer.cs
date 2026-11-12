using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;
using XiangqiOverlay.Core;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace XiangqiOverlay.Platform;

/// <summary>
/// 屏幕截图器：截取目标窗口的画面
/// 支持 PrintWindow 并自带全黑检测回退（防止硬件加速黑屏）
/// </summary>
public sealed class ScreenCapturer : IDisposable
{
    private bool _disposed;

    /// <summary>
    /// 截取指定窗口的客户区
    /// </summary>
    public Mat CaptureWindow(IntPtr hwnd)
    {
        if (!Win32.TryGetClientBounds(hwnd, out var bounds))
        {
            if (!Win32.GetWindowRect(hwnd, out var rect))
                return new Mat();
            bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return new Mat();

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            bool printOk = false;
            var hdc = g.GetHdc();
            try
            {
                // 3u: PW_CLIENTONLY (1) | PW_RENDERFULLCONTENT (2)
                printOk = Win32.PrintWindow(hwnd, hdc, 3u);
            }
            catch
            {
                printOk = false;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }

            // 若 PrintWindow 失败或截出来是全黑图片，自动回退到屏幕截屏
            if (!printOk || !HasVisiblePixels(bmp))
            {
                try
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }
                catch
                {
                }
            }
        }

        return BitmapToMat(bmp);
    }

    /// <summary>
    /// 检测截取的位图是否有有效可见像素（防止黑屏）
    /// </summary>
    private static bool HasVisiblePixels(Bitmap bitmap)
    {
        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            int minVal = 255;
            int maxVal = 0;
            int nonDarkCount = 0;
            int stepX = Math.Max(1, bitmap.Width / 24);
            int stepY = Math.Max(1, bitmap.Height / 24);

            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    int offset = y * bmpData.Stride + x * 4;
                    byte b = Marshal.ReadByte(bmpData.Scan0, offset);
                    byte g = Marshal.ReadByte(bmpData.Scan0, offset + 1);
                    byte r = Marshal.ReadByte(bmpData.Scan0, offset + 2);
                    int brightness = (r + g + b) / 3;

                    minVal = Math.Min(minVal, brightness);
                    maxVal = Math.Max(maxVal, brightness);
                    if (brightness > 8)
                        nonDarkCount++;
                }
            }

            return nonDarkCount > 5 && (maxVal - minVal) > 16;
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }
    }

    /// <summary>
    /// 截取屏幕区域
    /// </summary>
    public Mat CaptureScreen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return new Mat();

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
        }

        return BitmapToMat(bmp);
    }

    /// <summary>
    /// 截取全屏
    /// </summary>
    public Mat CaptureFullScreen()
    {
        var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return CaptureScreen(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// 将 System.Drawing.Bitmap 转换为 OpenCvSharp.Mat
    /// </summary>
    private static Mat BitmapToMat(Bitmap bmp)
    {
        var bmpData = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var mat = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC4, bmpData.Scan0, bmpData.Stride);
            return mat.Clone();
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
