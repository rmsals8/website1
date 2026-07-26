using System.Collections.Concurrent;

namespace backend.Models;

/// <summary>
/// 업로드~편집~내보내기 동안의 작업 세션. 인메모리로 관리되며 TempDir 아래에 원본 파일을 보관한다.
/// 로그인/DB 연동 전까지는 이 세션 저장 방식을 그대로 사용한다.
/// </summary>
public class PdfSession
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>webroot 바깥의 임시 저장 폴더 (세션 전용)</summary>
    public string TempDir { get; init; } = string.Empty;

    /// <summary>fileId -> 실제 파일 경로 (원본 PDF/이미지)</summary>
    public ConcurrentDictionary<string, string> SourceFiles { get; } = new();

    public List<PdfPageState> Pages { get; } = new();

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>동시 편집 충돌 방지를 위한 세션 단위 락</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}
