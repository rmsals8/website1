using System.Collections.Concurrent;
using backend.Models;

namespace backend.Services;

/// <summary>
/// 세션 인메모리 저장소. DB/로그인 붙이기 전까지는 세션 = 편집 작업 단위이며,
/// 일정 시간 미사용 시 임시 파일과 함께 정리된다(IHostedService에서 주기 정리).
/// </summary>
public class PdfSessionStore
{
    private readonly ConcurrentDictionary<string, PdfSession> _sessions = new();
    private readonly string _rootTempDir;

    public PdfSessionStore(IWebHostEnvironment env)
    {
        // webroot(wwwroot) 바깥, ContentRoot 기준 App_Data/sessions 에 저장 (직접 다운로드 불가한 경로)
        _rootTempDir = Path.Combine(env.ContentRootPath, "App_Data", "sessions");
        Directory.CreateDirectory(_rootTempDir);
    }

    public PdfSession CreateSession()
    {
        var session = new PdfSession
        {
            TempDir = Path.Combine(_rootTempDir, Guid.NewGuid().ToString("N"))
        };
        Directory.CreateDirectory(session.TempDir);
        _sessions[session.Id] = session;
        return session;
    }

    public bool TryGet(string sessionId, out PdfSession session)
    {
        if (_sessions.TryGetValue(sessionId, out var found))
        {
            found.LastAccessedAt = DateTime.UtcNow;
            session = found;
            return true;
        }

        session = null!;
        return false;
    }

    public void Remove(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            TryDeleteDirectory(session.TempDir);
        }
    }

    public IEnumerable<PdfSession> GetExpired(TimeSpan idleTimeout)
    {
        var cutoff = DateTime.UtcNow - idleTimeout;
        return _sessions.Values.Where(s => s.LastAccessedAt < cutoff).ToList();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 정리 실패는 치명적이지 않음 (다음 정리 주기나 재시작 시 재시도)
        }
    }
}
