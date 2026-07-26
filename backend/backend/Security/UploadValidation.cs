using Microsoft.AspNetCore.Http;

namespace backend.Security;

public static class UploadValidation
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"
    };

    /// <summary>
    /// PDF 매직 바이트("%PDF-") 확인. 확장자만으로는 파일 타입을 신뢰하지 않는다.
    /// </summary>
    public static bool IsValidPdf(IFormFile file)
    {
        if (file.Length <= 0 || file.Length > DocumentLimits.MaxPdfFileBytes)
            return false;

        Span<byte> header = stackalloc byte[5];
        using var stream = file.OpenReadStream();
        var read = stream.Read(header);
        if (read < 5)
            return false;

        return header[0] == (byte)'%' && header[1] == (byte)'P' && header[2] == (byte)'D' &&
               header[3] == (byte)'F' && header[4] == (byte)'-';
    }

    public static bool IsValidInsertFile(IFormFile file, out bool isPdf)
    {
        isPdf = false;
        var ext = Path.GetExtension(file.FileName);

        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            isPdf = true;
            return IsValidPdf(file);
        }

        if (!ImageExtensions.Contains(ext))
            return false;

        return file.Length > 0 && file.Length <= DocumentLimits.MaxImageFileBytes;
    }

    /// <summary>
    /// 업로드 파일을 GUID 파일명으로 재생성해 세션 임시 폴더(webroot 바깥)에 저장하고 경로를 반환한다.
    /// </summary>
    public static async Task<string> SaveWithGeneratedNameAsync(IFormFile file, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        var ext = Path.GetExtension(file.FileName);
        var safeExt = ext.Length is > 0 and <= 6 ? ext : string.Empty;
        var fileName = $"{Guid.NewGuid():N}{safeExt}";
        var fullPath = Path.Combine(destinationDir, fileName);

        await using var target = File.Create(fullPath);
        await file.CopyToAsync(target);
        return fullPath;
    }
}
