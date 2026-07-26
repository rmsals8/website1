namespace backend.Security;

public static class DocumentLimits
{
    public const long MaxPdfFileBytes = 500L * 1024 * 1024;
    public const long MaxImageFileBytes = 100L * 1024 * 1024;

    /// <summary>
    /// 비로그인 사용자 기준 업로드 페이지 수 제한(정책: 총 30페이지).
    /// 로그인 사용자 "무제한" 정책은 인증 붙인 뒤 GetMaxPages(User)로 분기 예정.
    /// 지금은 인증이 없으므로 모든 사용자에게 동일하게 적용한다.
    /// </summary>
    public const int MaxPagesPerDocumentAnonymous = 30;

    /// <summary>비로그인 사용자 Export(최종 결과물) 허용 횟수</summary>
    public const int MaxTrialExportsAnonymous = 1;

    public static int GetMaxPages(bool isAuthenticated) =>
        isAuthenticated ? int.MaxValue : MaxPagesPerDocumentAnonymous;
}
