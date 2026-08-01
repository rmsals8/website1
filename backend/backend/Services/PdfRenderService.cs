using backend.Models;
using Docnet.Core;
using Docnet.Core.Models;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SixLabors.ImageSharp;

namespace backend.Services;

/// <summary>
/// WpfApp1.Services.PdfDocumentService의 웹 버전 포팅.
/// 차이점: 결과를 WPF ImageSource가 아니라 JPEG byte[]로 반환하고,
/// 페이지 컬렉션(Pages)을 세션이 소유한다(이 서비스는 stateless).
/// </summary>
public class PdfRenderService
{
    public const int ThumbnailWidth = 180;
    public const int PreviewWidth = 1200;
    private const int ExportWidth = 2000;
    private const int JpegQuality = 88;

    public List<PdfPageState> LoadPdf(string filePath, string fileId, int maxPages)
    {
        var bytes = File.ReadAllBytes(filePath);
        using var docReader = DocLib.Instance.GetDocReader(bytes, new PageDimensions(ThumbnailWidth, ThumbnailWidth * 2));

        var pageCount = docReader.GetPageCount();
        if (pageCount <= 0)
            throw new InvalidOperationException("PDF에 페이지가 없습니다.");

        if (pageCount > maxPages)
            throw new InvalidOperationException($"업로드 가능한 페이지 수(최대 {maxPages}장)를 초과했습니다.");

        var pages = new List<PdfPageState>();
        for (var i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            pages.Add(new PdfPageState
            {
                DisplayIndex = i + 1,
                SourceType = PageSourceType.PdfPage,
                SourceFileId = fileId,
                SourcePageIndex = i,
                OriginalWidthPt = pageReader.GetPageWidth(),
                OriginalHeightPt = pageReader.GetPageHeight()
            });
        }

        return pages;
    }

    public List<PdfPageState> LoadPagesForInsert(string pdfPath, string fileId, int startDisplayIndex, int remainingCapacity)
    {
        var bytes = File.ReadAllBytes(pdfPath);
        using var docReader = DocLib.Instance.GetDocReader(bytes, new PageDimensions(ThumbnailWidth, ThumbnailWidth * 2));

        var pageCount = docReader.GetPageCount();
        if (pageCount > remainingCapacity)
            throw new InvalidOperationException($"삽입 가능한 남은 페이지 수({remainingCapacity}장)를 초과했습니다.");

        var pages = new List<PdfPageState>();
        for (var i = 0; i < pageCount; i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            pages.Add(new PdfPageState
            {
                DisplayIndex = startDisplayIndex + i,
                SourceType = PageSourceType.InsertedPdfPage,
                SourceFileId = fileId,
                SourcePageIndex = i,
                OriginalWidthPt = pageReader.GetPageWidth(),
                OriginalHeightPt = pageReader.GetPageHeight()
            });
        }

        return pages;
    }

    public PdfPageState CreateFromImage(string imagePath, string fileId, int displayIndex)
    {
        using var image = Image.Load(imagePath);
        return new PdfPageState
        {
            DisplayIndex = displayIndex,
            SourceType = PageSourceType.InsertedImage,
            SourceFileId = fileId,
            OriginalWidthPt = image.Width * 72.0 / 96.0,
            OriginalHeightPt = image.Height * 72.0 / 96.0
        };
    }

    /// <summary>페이지를 지정한 너비로 렌더링해 JPEG 바이트로 반환 (썸네일/미리보기 공용)</summary>
    public byte[] RenderPageJpeg(PdfPageState page, string sourceFilePath, int targetWidth, bool applyCrop = true)
    {
        var crop = applyCrop ? page.Crop : CropRegion.Full;

        var (bgra, w, h) = page.SourceType switch
        {
            PageSourceType.PdfPage or PageSourceType.InsertedPdfPage =>
                RenderPdfPageBgra(sourceFilePath, page.SourcePageIndex, targetWidth, page.Rotation, crop,
                    page.Brightness, page.Contrast, page.Midtones, page.InkStrokes),
            PageSourceType.InsertedImage =>
                RenderImagePageBgra(sourceFilePath, targetWidth, crop, page.Rotation,
                    page.Brightness, page.Contrast, page.Midtones, page.InkStrokes),
            _ => throw new InvalidOperationException("알 수 없는 페이지 소스 타입입니다.")
        };

        return ImageAdjustmentService.EncodeJpeg(bgra, w, h, JpegQuality);
    }

    public void Export(PdfSession session, IReadOnlyList<PdfPageState> pagesToExport, Stream outputStream, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        var output = new PdfDocument();
        var importCache = new Dictionary<string, PdfDocument?>(StringComparer.OrdinalIgnoreCase);
        var total = pagesToExport.Count;
        var done = 0;

        try
        {
            foreach (var page in pagesToExport)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryImportPage(output, page, session, importCache))
                    AppendRasterizedPage(output, page, session);

                done++;
                onProgress?.Invoke(done, total);
            }

            output.Save(outputStream);
        }
        finally
        {
            foreach (var doc in importCache.Values)
                doc?.Dispose();
        }
    }

    /// <summary>
    /// 원본 PDF를 벡터 그대로 복사(import)해본다. PdfSharpCore의 파서가 처리하지 못하는
    /// 특수한 PDF 문법(예: 일부 문자열/스트림 인코딩)을 만나면 예외를 던지는데, 이 경우
    /// Export 전체를 실패시키지 않고 false를 반환해 래스터화(이미지 변환) 경로로 자동 전환한다.
    /// </summary>
    private static bool TryImportPage(PdfDocument output, PdfPageState page, PdfSession session, Dictionary<string, PdfDocument?> importCache)
    {
        if (page.RequiresRasterExport)
            return false;

        if (page.SourceType is not (PageSourceType.PdfPage or PageSourceType.InsertedPdfPage))
            return false;

        if (!session.SourceFiles.TryGetValue(page.SourceFileId, out var path) || !File.Exists(path))
            return false;

        if (!importCache.TryGetValue(path, out var importDoc))
        {
            try
            {
                importDoc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
            }
            catch (Exception)
            {
                // PdfSharpCore가 이 PDF를 파싱하지 못함 - 벡터 복사 포기, 래스터화로 대체
                importDoc = null;
            }
            importCache[path] = importDoc;
        }

        if (importDoc is null)
            return false;

        if (page.SourcePageIndex < 0 || page.SourcePageIndex >= importDoc.PageCount)
            return false;

        output.AddPage(importDoc.Pages[page.SourcePageIndex]);
        return true;
    }

    private void AppendRasterizedPage(PdfDocument output, PdfPageState page, PdfSession session)
    {
        if (!session.SourceFiles.TryGetValue(page.SourceFileId, out var path))
            return;

        var (bgra, w, h) = page.SourceType == PageSourceType.InsertedImage
            ? RenderImagePageBgra(path, ExportWidth, page.Crop, page.Rotation, page.Brightness, page.Contrast, page.Midtones, page.InkStrokes)
            : RenderPdfPageBgra(path, page.SourcePageIndex, ExportWidth, page.Rotation, page.Crop, page.Brightness, page.Contrast, page.Midtones, page.InkStrokes);

        var imageBytes = ImageAdjustmentService.EncodeJpeg(bgra, w, h, JpegQuality);
        var xImage = XImage.FromStream(() => new MemoryStream(imageBytes));

        var pdfPage = output.AddPage();
        pdfPage.Width = XUnit.FromPoint(xImage.PixelWidth * 72.0 / 96.0);
        pdfPage.Height = XUnit.FromPoint(xImage.PixelHeight * 72.0 / 96.0);

        using var gfx = XGraphics.FromPdfPage(pdfPage);
        gfx.DrawImage(xImage, 0, 0, pdfPage.Width, pdfPage.Height);
    }

    private static (byte[] Bgra, int Width, int Height) RenderPdfPageBgra(
        string filePath, int pageIndex, int targetWidth, int rotation, CropRegion crop,
        double brightness, double contrast, double midtones, IReadOnlyList<InkStroke>? inkStrokes = null)
    {
        using var docReader = DocLib.Instance.GetDocReader(File.ReadAllBytes(filePath), new PageDimensions(targetWidth, targetWidth * 2));
        using var pageReader = docReader.GetPageReader(pageIndex);

        var renderWidth = pageReader.GetPageWidth();
        var renderHeight = pageReader.GetPageHeight();
        var rawBytes = pageReader.GetImage();

        var adjusted = ImageAdjustmentService.ApplyAdjustments(rawBytes, renderWidth, renderHeight, brightness, contrast, midtones);
        var (rotated, rotW, rotH) = ImageAdjustmentService.RotateBgra(adjusted, renderWidth, renderHeight, rotation);
        // 잉크(서명/그리기)는 회전 이후·크롭 이전 좌표계에 저장되어 있으므로 이 시점에 합성한다.
        var inked = ImageAdjustmentService.CompositeInkStrokes(rotated, rotW, rotH, inkStrokes ?? Array.Empty<InkStroke>());
        return ImageAdjustmentService.CropBgra(inked, rotW, rotH, crop);
    }

    private static (byte[] Bgra, int Width, int Height) RenderImagePageBgra(
        string imagePath, int targetWidth, CropRegion crop, int rotation,
        double brightness, double contrast, double midtones, IReadOnlyList<InkStroke>? inkStrokes = null)
    {
        var bgra = ImageAdjustmentService.LoadAndResizeImageToBgra(imagePath, targetWidth, out var width, out var height);
        var adjusted = ImageAdjustmentService.ApplyAdjustments(bgra, width, height, brightness, contrast, midtones);
        var (rotated, rotW, rotH) = ImageAdjustmentService.RotateBgra(adjusted, width, height, rotation);
        var inked = ImageAdjustmentService.CompositeInkStrokes(rotated, rotW, rotH, inkStrokes ?? Array.Empty<InkStroke>());
        return ImageAdjustmentService.CropBgra(inked, rotW, rotH, crop);
    }
}
