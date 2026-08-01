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

    /// <summary>
    /// 잉크 획(서명/그리기)을 BGRA 버퍼에 직접 합성한다(회전/크롭과 동일하게 ImageSharp.Drawing 대신
    /// 순수 픽셀 연산을 쓴다 — PdfSharpCore가 ImageSharp의 구버전 API에 의존해 ImageSharp.Drawing이 요구하는
    /// 신귝버전으로 올리면 런타임 MissingMethodException이 난다). 좌표는 "회전 적용, 크롭 미적용" 상태의
    /// 0~1 정규화 좌표(CropRegion과 동일 좌표계)이므로, 회전 이후 크롭 이전 시점에 호출해야
    /// WpfApp1 데스크톱 버전과 동일한 위치에 그려진다.
    /// </summary>
    public static byte[] CompositeInkStrokes(byte[] bgraBytes, int width, int height, IReadOnlyList<InkStroke> strokes)
    {
        if (strokes.Count == 0)
            return bgraBytes;

        var result = (byte[])bgraBytes.Clone();

        foreach (var stroke in strokes)
        {
            if (stroke.Points.Count == 0)
                continue;

            var (r, g, b, a) = ParseInkColorBytes(stroke.Color);
            var radius = Math.Max(0.5, stroke.ThicknessRatio * width / 2.0);

            if (stroke.Points.Count == 1)
            {
                var p = stroke.Points[0];
                StampCircle(result, width, height, p.X * width, p.Y * height, radius, r, g, b, a);
                continue;
            }

            for (var i = 0; i < stroke.Points.Count - 1; i++)
            {
                var x0 = stroke.Points[i].X * width;
                var y0 = stroke.Points[i].Y * height;
                var x1 = stroke.Points[i + 1].X * width;
                var y1 = stroke.Points[i + 1].Y * height;

                var dist = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                var steps = Math.Max(1, (int)Math.Ceiling(dist)); // 1px 간격으로 원을 뚝어 선을 이은다(이동 응)
                for (var s = 0; s <= steps; s++)
                {
                    var t = (double)s / steps;
                    var x = x0 + (x1 - x0) * t;
                    var y = y0 + (y1 - y0) * t;
                    StampCircle(result, width, height, x, y, radius, r, g, b, a);
                }
            }
        }

        return result;
    }

    /// <summary>지정한 중심점에 반지름(radius)크기의 원을 알파 블렌딩해서 직접 찍는다(펀 굵기의 획 끝/이음새 표현용)</summary>
    private static void StampCircle(byte[] buffer, int width, int height, double cx, double cy, double radius, byte r, byte g, byte b, byte a)
    {
        var minX = Math.Max(0, (int)Math.Floor(cx - radius));
        var maxX = Math.Min(width - 1, (int)Math.Ceiling(cx + radius));
        var minY = Math.Max(0, (int)Math.Floor(cy - radius));
        var maxY = Math.Min(height - 1, (int)Math.Ceiling(cy + radius));
        var r2 = radius * radius;
        var alpha = a / 255.0;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x + 0.5 - cx;
                var dy = y + 0.5 - cy;
                if (dx * dx + dy * dy > r2)
                    continue;

                var idx = (y * width + x) * 4;
                // BGRA 순서: B, G, R, A
                buffer[idx] = (byte)Math.Round(b * alpha + buffer[idx] * (1 - alpha));
                buffer[idx + 1] = (byte)Math.Round(g * alpha + buffer[idx + 1] * (1 - alpha));
                buffer[idx + 2] = (byte)Math.Round(r * alpha + buffer[idx + 2] * (1 - alpha));
                buffer[idx + 3] = Math.Max(buffer[idx + 3], a);
            }
        }
    }

    /// <summary>#RRGGBB 또는 #AARRGGBB 형식의 색상 문자열을 (R,G,B,A) 바이트로 변환</summary>
    private static (byte R, byte G, byte B, byte A) ParseInkColorBytes(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6)
            h = "FF" + h; // 알파 생략 시 불투명으로 간주

        var a = Convert.ToByte(h[..2], 16);
        var r = Convert.ToByte(h.Substring(2, 2), 16);
        var g = Convert.ToByte(h.Substring(4, 2), 16);
        var b = Convert.ToByte(h.Substring(6, 2), 16);
        return (r, g, b, a);
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
