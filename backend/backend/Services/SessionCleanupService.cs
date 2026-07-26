namespace backend.Services;

/// <summary>주기적으로 유휴 세션(임시 파일 포함)을 정리하는 백그라운드 서비스</summary>
public class SessionCleanupService : BackgroundService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private readonly PdfSessionStore _store;

    public SessionCleanupService(PdfSessionStore store)
    {
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var expired in _store.GetExpired(IdleTimeout))
                _store.Remove(expired.Id);

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // shutting down
            }
        }
    }
}
