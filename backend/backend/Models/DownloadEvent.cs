namespace backend.Models;

/// <summary>
/// 다운로드 버튼 클릭 1회당 1개 로우. 총 다운로드 수는 COUNT(*)로 집계한다.
/// (단일 카운터 컬럼 대신 이벤트 로그 방식을 쓰면 동시성 문제 없이 안전하게 누적된다.)
/// </summary>
public class DownloadEvent
{
    public int Id { get; set; }

    /// <summary>예: "desktop-windows". 추후 플랫폼(맥/모바일 등)이 늘어나도 구분 가능하도록 문자열로 둔다.</summary>
    public string Platform { get; set; } = "desktop-windows";

    public DateTime DownloadedAtUtc { get; set; } = DateTime.UtcNow;
}
