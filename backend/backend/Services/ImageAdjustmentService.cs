using backend.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace backend.Services;

/// <summary>
/// WpfApp1.Services.ImageAdjustmentService의 순수 픽셀 연산 로직을 그대로 포팅.
/// 밝기/대비/중간톤 계산식과 회전/크롭 알고리즘은 원본과 동일하게 유지해
/// 결과물이 데스크톱 버전과 동일하게 나오도록 한다.
/// 단, WPF BitmapImage 의존 부분(이미지 로드/인코딩)은 ImageSharp로 대체했다.
/// </summary>
public static class ImageAdjustmentService
{
    public static byte[] ApplyAdjustments(byte[] bgraBytes, int width, int height, double brightness, double contrast, double midtones)
    {
        if (brightness == 0 && contrast == 0 && midtones == 0)
            return bgraBytes;

        var result = new byte[bgraBytes.Length];
        var brightnessOffset = brightness * 2.55;
        var contrastFactor = (100.0 + contrast) / 100.0;
        var gamma = midtones == 0 ? 1.0 : Math.Pow(2, -midtones / 50.0);
        var gammaInv = 1.0 / gamma;

        for (var i = 0; i < bgraBytes.Length; i += 4)
        {
            result[i] = AdjustChannel(bgraBytes[i], brightnessOffset, contrastFactor, gammaInv);
            result[i + 1] = AdjustChannel(bgraBytes[i + 1], brightnessOffset, contrastFactor, gammaInv);
            result[i + 2] = AdjustChannel(bgraBytes[i + 2], brightnessOffset, contrastFactor, gammaInv);
            result[i + 3] = bgraBytes[i + 3];
        }

        return result;
    }

    private static byte AdjustChannel(byte channel, double brightnessOffset, double contrastFactor, double gammaInv)
    {
        var value = (channel + brightnessOffset - 128) * contrastFactor + 128;
        value = Math.Clamp(value, 0, 255);
        value = Math.Pow(value / 255.0, gammaInv) * 255.0;
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    public static (byte[] Bytes, int Width, int Height) RotateBgra(byte[] bgraBytes, int width, int height, int rotation)
    {
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation == 0)
            return (bgraBytes, width, height);

        return rotation switch
        {
            90 => Rotate90(bgraBytes, width, height),
            180 => Rotate180(bgraBytes, width, height),
            270 => Rotate270(bgraBytes, width, height),
            _ => (bgraBytes, width, height)
        };
    }

    public static (byte[] Bytes, int Width, int Height) CropBgra(byte[] bgraBytes, int width, int height, CropRegion crop)
    {
        if (crop.IsFull)
            return (bgraBytes, width, height);

        var x = (int)Math.Round(crop.X * width);
        var y = (int)Math.Round(crop.Y * height);
        var w = (int)Math.Round(crop.Width * width);
        var h = (int)Math.Round(crop.Height * height);

        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        w = Math.Clamp(w, 1, width - x);
        h = Math.Clamp(h, 1, height - y);

        var result = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            var srcOffset = ((y + row) * width + x) * 4;
            var dstOffset = row * w * 4;
            Buffer.BlockCopy(bgraBytes, srcOffset, result, dstOffset, w * 4);
        }

        return (result, w, h);
    }

    /// <summary>파일 경로에서 이미지를 로드해 targetWidth로 비율 유지 리사이즈 후 BGRA 픽셀 배열로 변환</summary>
    public static byte[] LoadAndResizeImageToBgra(string path, int targetWidth, out int width, out int height)
    {
        using var image = Image.Load<Bgra32>(path);
        return ResizeAndExtractBgra(image, targetWidth, out width, out height);
    }

    public static byte[] LoadAndResizeImageBytesToBgra(byte[] fileBytes, int targetWidth, out int width, out int height)
    {
        using var image = Image.Load<Bgra32>(fileBytes);
        return ResizeAndExtractBgra(image, targetWidth, out width, out height);
    }

    private static byte[] ResizeAndExtractBgra(Image<Bgra32> image, int targetWidth, out int width, out int height)
    {
        if (targetWidth > 0 && image.Width > targetWidth)
        {
            var ratio = (double)targetWidth / image.Width;
            var targetHeight = Math.Max(1, (int)Math.Round(image.Height * ratio));
            image.Mutate(ctx => ctx.Resize(targetWidth, targetHeight));
        }

        width = image.Width;
        height = image.Height;

        var pixels = new byte[width * height * 4];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    /// <summary>BGRA 픽셀 배열을 JPEG 바이트로 인코딩 (미리보기/썸네일/래스터 export용)</summary>
    public static byte[] EncodeJpeg(byte[] bgraBytes, int width, int height, int quality = 88)
    {
        var flattened = FlattenAlphaToWhite(bgraBytes);
        using var image = Image.LoadPixelData<Bgra32>(flattened, width, height);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    /// <summary>
    /// PDFium(Docnet.Core)이 반환하는 페이지 배경은 대부분 투명(alpha=0, RGB=0)이다.
    /// JPEG는 알파 채널을 지원하지 않으므로, 인코딩 전에 흰 배경 위에 합성해서
    /// 투명 부분이 검은색으로 나오지 않고 흰 종이처럼 보이도록 한다.
    /// </summary>
    public static byte[] FlattenAlphaToWhite(byte[] bgraBytes)
    {
        var result = new byte[bgraBytes.Length];
        for (var i = 0; i < bgraBytes.Length; i += 4)
        {
            var a = bgraBytes[i + 3] / 255.0;
            result[i] = (byte)Math.Round(bgraBytes[i] * a + 255 * (1 - a));         // B
            result[i + 1] = (byte)Math.Round(bgraBytes[i + 1] * a + 255 * (1 - a)); // G
            result[i + 2] = (byte)Math.Round(bgraBytes[i + 2] * a + 255 * (1 - a)); // R
            result[i + 3] = 255;
        }
        return result;
    }

    private static (byte[] Bytes, int Width, int Height) Rotate90(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        var newWidth = height;
        var newHeight = width;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var srcIndex = (y * width + x) * 4;
            var newX = height - 1 - y;
            var newY = x;
            var dstIndex = (newY * newWidth + newX) * 4;
            dst[dstIndex] = src[srcIndex];
            dst[dstIndex + 1] = src[srcIndex + 1];
            dst[dstIndex + 2] = src[srcIndex + 2];
            dst[dstIndex + 3] = src[srcIndex + 3];
        }

        return (dst, newWidth, newHeight);
    }

    private static (byte[] Bytes, int Width, int Height) Rotate180(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var srcIndex = (y * width + x) * 4;
            var dstIndex = ((height - 1 - y) * width + (width - 1 - x)) * 4;
            dst[dstIndex] = src[srcIndex];
            dst[dstIndex + 1] = src[srcIndex + 1];
            dst[dstIndex + 2] = src[srcIndex + 2];
            dst[dstIndex + 3] = src[srcIndex + 3];
        }

        return (dst, width, height);
    }

    private static (byte[] Bytes, int Width, int Height) Rotate270(byte[] src, int width, int height)
    {
        var dst = new byte[src.Length];
        var newWidth = height;
        var newHeight = width;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var srcIndex = (y * width + x) * 4;
            var newX = y;
            var newY = width - 1 - x;
            var dstIndex = (newY * newWidth + newX) * 4;
            dst[dstIndex] = src[srcIndex];
            dst[dstIndex + 1] = src[srcIndex + 1];
            dst[dstIndex + 2] = src[srcIndex + 2];
            dst[dstIndex + 3] = src[srcIndex + 3];
        }

        return (dst, newWidth, newHeight);
    }
}
