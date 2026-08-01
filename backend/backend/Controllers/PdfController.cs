using backend.Models;
using backend.Models.Dtos;
using backend.Security;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/pdf")]
public class PdfController : ControllerBase
{
    private readonly PdfSessionStore _sessionStore;
    private readonly PdfRenderService _renderService;
    private readonly TrialUsageService _trialUsage;
    private readonly ExportProgressService _exportProgress;
    private readonly ILogger<PdfController> _logger;

    public PdfController(
        PdfSessionStore sessionStore,
        PdfRenderService renderService,
        TrialUsageService trialUsage,
        ExportProgressService exportProgress,
        ILogger<PdfController> logger)
    {
        _sessionStore = sessionStore;
        _renderService = renderService;
        _trialUsage = trialUsage;
        _exportProgress = exportProgress;
        _logger = logger;
    }

    // ---- 세션 생성 (PDF 업로드) ----
    [HttpPost("sessions")]
    [RequestSizeLimit(DocumentLimits.MaxPdfFileBytes)]
    public async Task<ActionResult<SessionDto>> CreateSession(IFormFile file)
    {
        if (file is null || !UploadValidation.IsValidPdf(file))
            return BadRequest(new ErrorResponse("유효한 PDF 파일이 아닙니다."));

        var session = _sessionStore.CreateSession();
        var maxPages = DocumentLimits.GetMaxPages(isAuthenticated: false);

        try
        {
            var savedPath = await UploadValidation.SaveWithGeneratedNameAsync(file, session.TempDir);
            var fileId = Guid.NewGuid().ToString("N");
            session.SourceFiles[fileId] = savedPath;

            var pages = _renderService.LoadPdf(savedPath, fileId, maxPages);
            session.Pages.AddRange(pages);

            return Ok(ToSessionDto(session, maxPages));
        }
        catch (InvalidOperationException ex)
        {
            _sessionStore.Remove(session.Id);
            return BadRequest(new ErrorResponse(ex.Message));
        }
    }

    // ---- 페이지/PDF 삽입 ----
    [HttpPost("sessions/{sessionId}/insert")]
    [RequestSizeLimit(DocumentLimits.MaxPdfFileBytes)]
    public async Task<ActionResult<SessionDto>> InsertFile(string sessionId, IFormFile file, [FromQuery] int? afterDisplayIndex)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        if (file is null || !UploadValidation.IsValidInsertFile(file, out var isPdf))
            return BadRequest(new ErrorResponse("삽입 가능한 파일 형식이 아닙니다(PDF, PNG, JPG, BMP, TIFF)."));

        var maxPages = DocumentLimits.GetMaxPages(isAuthenticated: false);

        await session.Lock.WaitAsync();
        try
        {
            var remaining = maxPages - session.Pages.Count;
            if (remaining <= 0)
                return BadRequest(new ErrorResponse($"페이지 수 제한(최대 {maxPages}장)에 도달했습니다."));

            var savedPath = await UploadValidation.SaveWithGeneratedNameAsync(file, session.TempDir);
            var fileId = Guid.NewGuid().ToString("N");
            session.SourceFiles[fileId] = savedPath;

            var insertAt = afterDisplayIndex.HasValue
                ? Math.Clamp(afterDisplayIndex.Value, 0, session.Pages.Count)
                : session.Pages.Count;

            List<PdfPageState> newPages;
            try
            {
                newPages = isPdf
                    ? _renderService.LoadPagesForInsert(savedPath, fileId, insertAt + 1, remaining)
                    : new List<PdfPageState> { _renderService.CreateFromImage(savedPath, fileId, insertAt + 1) };
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new ErrorResponse(ex.Message));
            }

            session.Pages.InsertRange(insertAt, newPages);
            RenumberDisplayIndexes(session);

            return Ok(ToSessionDto(session, maxPages));
        }
        finally
        {
            session.Lock.Release();
        }
    }

    // ---- 페이지 속성 수정 (회전/크롭/밝기/대비/중간톤) ----
    [HttpPatch("sessions/{sessionId}/pages/{pageId}")]
    public async Task<ActionResult<PageDto>> UpdatePage(string sessionId, string pageId, [FromBody] UpdatePageRequest request)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        await session.Lock.WaitAsync();
        try
        {
            var page = session.Pages.FirstOrDefault(p => p.Id == pageId);
            if (page is null)
                return NotFound(new ErrorResponse("페이지를 찾을 수 없습니다."));

            if (request.RotateBy is { } delta)
            {
                page.Rotation = ((page.Rotation + delta) % 360 + 360) % 360;
                // 회전하면 좌표계가 바뀌므로, 기존 잉크(서명/그리기)는 초기화한다 (WpfApp1과 동일 정책)
                page.InkStrokes.Clear();
            }
            else if (request.Rotation is { } rot)
            {
                page.Rotation = ((rot % 360) + 360) % 360;
                page.InkStrokes.Clear();
            }

            if (request.InkStrokes is { } inkStrokes)
            {
                page.InkStrokes = inkStrokes.Select(s => new InkStroke
                {
                    Color = s.Color,
                    ThicknessRatio = s.ThicknessRatio,
                    Points = s.Points.Select(p => new InkPoint { X = p.X, Y = p.Y }).ToList()
                }).ToList();
            }

            if (request.CropX is { } cx && request.CropY is { } cy && request.CropWidth is { } cw && request.CropHeight is { } ch)
                page.Crop = new CropRegion { X = cx, Y = cy, Width = cw, Height = ch };

            if (request.AutoExposure is { } auto)
            {
                page.AutoExposure = auto;
                if (auto)
                {
                    page.Brightness = 8;
                    page.Contrast = 0;
                    page.Midtones = 0;
                }
            }
            else
            {
                if (request.Brightness is { } b) page.Brightness = b;
                if (request.Contrast is { } c) page.Contrast = c;
                if (request.Midtones is { } m) page.Midtones = m;
            }

            return Ok(ToPageDto(page));
        }
        finally
        {
            session.Lock.Release();
        }
    }

    // ---- 페이지 삭제 ----
    [HttpDelete("sessions/{sessionId}/pages/{pageId}")]
    public async Task<ActionResult<SessionDto>> DeletePage(string sessionId, string pageId)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        await session.Lock.WaitAsync();
        try
        {
            var page = session.Pages.FirstOrDefault(p => p.Id == pageId);
            if (page is null)
                return NotFound(new ErrorResponse("페이지를 찾을 수 없습니다."));

            session.Pages.Remove(page);
            RenumberDisplayIndexes(session);

            return Ok(ToSessionDto(session, DocumentLimits.GetMaxPages(false)));
        }
        finally
        {
            session.Lock.Release();
        }
    }

    // ---- 드래그 재정렬 ----
    [HttpPost("sessions/{sessionId}/reorder")]
    public async Task<ActionResult<SessionDto>> Reorder(string sessionId, [FromBody] ReorderRequest request)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        await session.Lock.WaitAsync();
        try
        {
            var lookup = session.Pages.ToDictionary(p => p.Id);
            if (request.OrderedPageIds.Count != session.Pages.Count || request.OrderedPageIds.Any(id => !lookup.ContainsKey(id)))
                return BadRequest(new ErrorResponse("페이지 목록이 세션 상태와 일치하지 않습니다."));

            var reordered = request.OrderedPageIds.Select(id => lookup[id]).ToList();
            session.Pages.Clear();
            session.Pages.AddRange(reordered);
            RenumberDisplayIndexes(session);

            return Ok(ToSessionDto(session, DocumentLimits.GetMaxPages(false)));
        }
        finally
        {
            session.Lock.Release();
        }
    }

    // ---- 페이지 렌더링 (썸네일/미리보기) ----
    [HttpGet("sessions/{sessionId}/pages/{pageId}/render")]
    public IActionResult RenderPage(string sessionId, string pageId, [FromQuery] int width = PdfRenderService.ThumbnailWidth, [FromQuery] bool crop = true)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        var page = session.Pages.FirstOrDefault(p => p.Id == pageId);
        if (page is null)
            return NotFound(new ErrorResponse("페이지를 찾을 수 없습니다."));

        if (!session.SourceFiles.TryGetValue(page.SourceFileId, out var sourcePath) || !System.IO.File.Exists(sourcePath))
            return NotFound(new ErrorResponse("원본 파일을 찾을 수 없습니다."));

        var clampedWidth = Math.Clamp(width, 60, PdfRenderService.PreviewWidth);

        try
        {
            var jpeg = _renderService.RenderPageJpeg(page, sourcePath, clampedWidth, crop);
            return File(jpeg, "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "페이지 렌더링 실패: session={SessionId} page={PageId}", sessionId, pageId);
            return StatusCode(500, new ErrorResponse("페이지를 렌더링하는 중 오류가 발생했습니다."));
        }
    }

    // ---- 최종 결과물 내보내기: 시작 (백그라운드로 실행, 즉시 응답)
    // fromDisplayIndex/toDisplayIndex를 주면 해당 범위만 분리해서 내보낸다(스플릿). 안 주면 전체 내보내기. ----
    [HttpPost("sessions/{sessionId}/export/start")]
    public async Task<IActionResult> StartExport(string sessionId, [FromQuery] int? fromDisplayIndex, [FromQuery] int? toDisplayIndex)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        if (session.Pages.Count == 0)
            return BadRequest(new ErrorResponse("내보낼 페이지가 없습니다."));

        List<PdfPageState> pagesToExport;
        if (fromDisplayIndex.HasValue || toDisplayIndex.HasValue)
        {
            var from = fromDisplayIndex ?? 1;
            var to = toDisplayIndex ?? session.Pages.Count;
            if (from < 1 || to < from)
                return BadRequest(new ErrorResponse("페이지 범위가 올바르지 않습니다."));

            pagesToExport = session.Pages.Where(p => p.DisplayIndex >= from && p.DisplayIndex <= to).ToList();
            if (pagesToExport.Count == 0)
                return BadRequest(new ErrorResponse("해당 범위에 해당하는 페이지가 없습니다."));
        }
        else
        {
            pagesToExport = session.Pages.ToList();
        }

        // TODO: 로그인 붙으면 User.Identity.IsAuthenticated 인 경우 아래 체험판 체크를 건너뛰도록 분기
        var trialId = GetOrCreateTrialId();
        if (!_trialUsage.CanExport(trialId))
            return StatusCode(StatusCodes.Status402PaymentRequired,
                new ErrorResponse("체험판은 다운로드 1회까지 무료입니다. 무제한 사용은 로그인 후 이용해 주세요."));

        if (!await session.Lock.WaitAsync(0))
            return Conflict(new ErrorResponse("이미 내보내기가 진행 중입니다."));

        var job = _exportProgress.Start(sessionId, pagesToExport.Count);

        _ = Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream();
                _renderService.Export(session, pagesToExport, ms, (completed, total) => job.Completed = completed);
                job.Result = ms.ToArray();
                job.Status = ExportStatus.Done;
                _trialUsage.RecordExport(trialId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PDF 내보내기 실패: session={SessionId}", sessionId);
                job.Status = ExportStatus.Error;
                job.Error = "PDF를 생성하는 중 오류가 발생했습니다.";
            }
            finally
            {
                session.Lock.Release();
            }
        });

        return Accepted(new { total = job.Total });
    }

    // ---- 범위 삭제 (몇 페이지부터 몇 페이지까지 한번에 삭제) ----
    [HttpDelete("sessions/{sessionId}/pages/range")]
    public async Task<ActionResult<SessionDto>> DeletePageRange(string sessionId, [FromQuery] int fromDisplayIndex, [FromQuery] int toDisplayIndex)
    {
        if (!_sessionStore.TryGet(sessionId, out var session))
            return NotFound(new ErrorResponse("세션을 찾을 수 없습니다."));

        if (fromDisplayIndex < 1 || toDisplayIndex < fromDisplayIndex)
            return BadRequest(new ErrorResponse("페이지 범위가 올바르지 않습니다."));

        await session.Lock.WaitAsync();
        try
        {
            var toRemove = session.Pages.Where(p => p.DisplayIndex >= fromDisplayIndex && p.DisplayIndex <= toDisplayIndex).ToList();
            if (toRemove.Count == 0)
                return BadRequest(new ErrorResponse("해당 범위에 해당하는 페이지가 없습니다."));

            if (toRemove.Count == session.Pages.Count)
                return BadRequest(new ErrorResponse("모든 페이지를 삭제할 수는 없습니다."));

            foreach (var page in toRemove)
                session.Pages.Remove(page);

            RenumberDisplayIndexes(session);

            return Ok(ToSessionDto(session, DocumentLimits.GetMaxPages(false)));
        }
        finally
        {
            session.Lock.Release();
        }
    }

    // ---- 내보내기 진행률 조회 (폴링용) ----
    [HttpGet("sessions/{sessionId}/export/progress")]
    public IActionResult GetExportProgress(string sessionId)
    {
        if (!_exportProgress.TryGet(sessionId, out var job))
            return NotFound(new ErrorResponse("진행 중인 내보내기가 없습니다."));

        return Ok(new
        {
            completed = job.Completed,
            total = job.Total,
            status = job.Status.ToString(),
            error = job.Error
        });
    }

    // ---- 완료된 결과물 다운로드 ----
    [HttpGet("sessions/{sessionId}/export/result")]
    public IActionResult GetExportResult(string sessionId)
    {
        if (!_exportProgress.TryGet(sessionId, out var job) || job.Status != ExportStatus.Done || job.Result is null)
            return NotFound(new ErrorResponse("완료된 결과물이 없습니다."));

        var bytes = job.Result;
        _exportProgress.Remove(sessionId);
        return File(bytes, "application/pdf", "export.pdf");
    }

    private string GetOrCreateTrialId()
    {
        if (Request.Cookies.TryGetValue(TrialUsageService.CookieName, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        var newId = Guid.NewGuid().ToString("N");
        Response.Cookies.Append(TrialUsageService.CookieName, newId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });
        return newId;
    }

    private static void RenumberDisplayIndexes(PdfSession session)
    {
        for (var i = 0; i < session.Pages.Count; i++)
            session.Pages[i].DisplayIndex = i + 1;
    }

    private static SessionDto ToSessionDto(PdfSession session, int maxPages) =>
        new(session.Id, session.Pages.Select(ToPageDto).ToList(), maxPages);

    private static PageDto ToPageDto(PdfPageState p) => new(
        p.Id,
        p.DisplayIndex,
        p.SourceType.ToString(),
        p.Rotation,
        p.Crop.X, p.Crop.Y, p.Crop.Width, p.Crop.Height,
        p.Brightness, p.Contrast, p.Midtones, p.AutoExposure,
        p.PageWidthPt, p.PageHeightPt,
        p.InkStrokes.Select(s => new InkStrokeDto(
            s.Points.Select(pt => new InkPointDto(pt.X, pt.Y)).ToList(),
            s.Color,
            s.ThicknessRatio)).ToList()
    );
}
