using System.Collections.Concurrent;
using backend.Security;

namespace backend.Services;

/// <summary>
/// 체험판 정책: 비로그인 사용자는 Export 1회까지만 허용.
/// 클라이언트는 localStorage로도 1차 체크하지만, 이 서버 측 카운트가 최소 방어선이다.
/// 지금은 로그인 기능이 없으므로 모든 요청을 "비로그인"으로 간주한다.
/// 추후 인증이 붙으면 인증된 사용자는 이 체크를 건너뛰도록 컨트롤러에서 분기하면 된다.
/// </summary>
public class TrialUsageService
{
    // trialId(쿠키 값) -> export 횟수. 프로세스 재시작 시 초기화됨(향후 DB/Redis로 교체 권장).
    private readonly ConcurrentDictionary<string, int> _exportCounts = new();

    public const string CookieName = "trial_id";

    public bool CanExport(string trialId) =>
        _exportCounts.GetOrAdd(trialId, 0) < DocumentLimits.MaxTrialExportsAnonymous;

    public void RecordExport(string trialId) =>
        _exportCounts.AddOrUpdate(trialId, 1, (_, count) => count + 1);
}
