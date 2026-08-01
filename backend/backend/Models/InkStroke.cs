namespace backend.Models;

/// <summary>
/// 서명/그리기 한 획. WpfApp1의 InkCanvas Stroke에 대응.
/// Points는 "회전 적용, 크롭 미적용" 상태의 페이지 기준 0~1 정규화 좌표(CropRegion과 동일 좌표계).
/// ThicknessRatio는 페이지 너비 대비 굵기 비율로 저장해, 썸네일/미리보기/최종 내보내기처럼
/// 렌더링 해상도가 달라져도 항상 동일한 상대 굵기로 보이게 한다.
/// </summary>
public class InkStroke
{
    public List<InkPoint> Points { get; set; } = new();

    /// <summary>#RRGGBB 또는 #AARRGGBB 형식</summary>
    public string Color { get; set; } = "#FF000000";

    /// <summary>페이지 너비 대비 굵기 비율 (예: 0.004 = 너비의 0.4%)</summary>
    public double ThicknessRatio { get; set; } = 0.004;
}

public struct InkPoint
{
    public double X { get; set; }
    public double Y { get; set; }
}
