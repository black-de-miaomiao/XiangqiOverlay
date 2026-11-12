using OpenCvSharp;
using XiangqiOverlay.Vision;

namespace LogicTest;

public static class ScreenshotNormalizationTests
{
    public static void Run(Action<string, bool> check)
    {
        using var bgr = new Mat(1, 1, MatType.CV_8UC3);
        bgr.Set(0, 0, new Vec3b(10, 20, 30));

        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        using var normalized = ScreenshotNormalizer.ToBgr(bgra);
        var pixel = normalized.Get<Vec3b>(0, 0);
        check("BGRA 生产截图转换为相同 BGR 像素",
            normalized.Channels() == 3 &&
            pixel.Item0 == 10 && pixel.Item1 == 20 && pixel.Item2 == 30);

        using var clone = ScreenshotNormalizer.ToBgr(bgr);
        var clonedPixel = clone.Get<Vec3b>(0, 0);
        check("BGR 离线图片返回独立副本且像素不变",
            !ReferenceEquals(bgr, clone) && clone.Channels() == 3 &&
            clonedPixel.Item0 == 10 && clonedPixel.Item1 == 20 && clonedPixel.Item2 == 30);
    }
}
