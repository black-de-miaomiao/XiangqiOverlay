using OpenCvSharp;

namespace XiangqiOverlay.Vision;

public static class ScreenshotNormalizer
{
    public static Mat ToBgr(Mat screenshot)
    {
        if (screenshot.Empty())
            return new Mat();

        switch (screenshot.Channels())
        {
            case 3:
                return screenshot.Clone();
            case 4:
            {
                var bgr = new Mat();
                Cv2.CvtColor(screenshot, bgr, ColorConversionCodes.BGRA2BGR);
                return bgr;
            }
            case 1:
            {
                var bgr = new Mat();
                Cv2.CvtColor(screenshot, bgr, ColorConversionCodes.GRAY2BGR);
                return bgr;
            }
            default:
                throw new ArgumentException($"不支持的截图通道数: {screenshot.Channels()}", nameof(screenshot));
        }
    }
}
