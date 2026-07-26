namespace backend.Models;

/// <summary>
/// 세션 내에서 관리되는 페이지 상태. WpfApp1의 PdfPageItem(ObservableObject)에 대응하되
/// 서버는 상태만 들고 있고 렌더링은 요청 시점에 수행한다 (썸네일/이미지를 세션에 들고 있지 않음).
/// </summary>
public class PdfPageState
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public int DisplayIndex { get; set; }

    public PageSourceType SourceType { get; set; } = PageSourceType.PdfPage;

    /// <summary>세션의 SourceFiles 딕셔너리를 가리키는 키 (실제 파일 경로는 세션이 관리)</summary>
    public string SourceFileId { get; set; } = string.Empty;

    public int SourcePageIndex { get; set; }

    public double OriginalWidthPt { get; set; }

    public double OriginalHeightPt { get; set; }

    public int Rotation { get; set; }

    public CropRegion Crop { get; set; } = CropRegion.Full;

    public double Brightness { get; set; }

    public double Contrast { get; set; }

    public double Midtones { get; set; }

    public bool AutoExposure { get; set; }

    public double PageWidthPt => Rotation is 90 or 270 ? OriginalHeightPt : OriginalWidthPt;

    public double PageHeightPt => Rotation is 90 or 270 ? OriginalWidthPt : OriginalHeightPt;

    /// <summary>회전·자르기·보정이 없고 원본이 PDF 페이지라면 무손실로 그대로 복사 가능</summary>
    public bool RequiresRasterExport =>
        SourceType == PageSourceType.InsertedImage ||
        Rotation != 0 ||
        !Crop.IsFull ||
        Brightness != 0 ||
        Contrast != 0 ||
        Midtones != 0;
}
