using System.Collections.Concurrent;

namespace backend.Services;

public enum ExportStatus
{
    Running,
    Done,
    Error
}

public class ExportJob
{
    public int Total;
    public int Completed;
    public volatile ExportStatus Status = ExportStatus.Running;
    public string? Error;
    public byte[]? Result;
}

/// <summary>
/// 내보내기(Export)는 백그라운드 Task로 실행되고, 프론트는 이 서비스를 폴링해서
/// 진행률(완료 페이지 수 / 전체 페이지 수)을 프로그레스 바로 표시한다.
/// 세션당 한 번에 하나의 내보내기만 진행된다고 가정(session.Lock으로 동시 실행 방지).
/// </summary>
public class ExportProgressService
{
    private readonly ConcurrentDictionary<string, ExportJob> _jobs = new();

    public ExportJob Start(string sessionId, int total)
    {
        var job = new ExportJob { Total = total };
        _jobs[sessionId] = job;
        return job;
    }

    public bool TryGet(string sessionId, out ExportJob job) => _jobs.TryGetValue(sessionId, out job!);

    public void Remove(string sessionId) => _jobs.TryRemove(sessionId, out _);
}
