using backend.Data;
using backend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace backend.Controllers;

[ApiController]
[Route("api/download")]
public class DownloadController : ControllerBase
{
    private readonly AppDbContext _db;

    public DownloadController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>다운로드 버튼 클릭 시 프론트에서 호출(fire-and-forget). 다운로드 자체를 막지 않도록 실패해도 클라이언트에는 큰 영향 없음.</summary>
    [HttpPost("track")]
    public async Task<IActionResult> Track([FromBody] TrackDownloadRequest? request)
    {
        var evt = new DownloadEvent
        {
            Platform = string.IsNullOrWhiteSpace(request?.Platform) ? "desktop-windows" : request!.Platform!,
        };
        _db.DownloadEvents.Add(evt);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>총 다운로드 수 확인용(운영자가 대시보드 없이 빠르게 확인하는 목적).</summary>
    [HttpGet("count")]
    public async Task<IActionResult> Count([FromQuery] string? platform)
    {
        var query = _db.DownloadEvents.AsQueryable();
        if (!string.IsNullOrWhiteSpace(platform))
            query = query.Where(e => e.Platform == platform);

        var count = await query.CountAsync();
        return Ok(new { count });
    }
}

public class TrackDownloadRequest
{
    public string? Platform { get; set; }
}
