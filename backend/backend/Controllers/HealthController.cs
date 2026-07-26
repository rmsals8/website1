using backend.Data;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/health")]
public class HealthController : ControllerBase
{
    private readonly AppDbContext _db;

    public HealthController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });

    /// <summary>MariaDB 연결 문자열이 실제로 접속 가능한지 확인 (스키마 없이도 동작)</summary>
    [HttpGet("db")]
    public async Task<IActionResult> CheckDb()
    {
        try
        {
            var canConnect = await _db.Database.CanConnectAsync();
            return canConnect
                ? Ok(new { status = "connected" })
                : StatusCode(503, new { status = "unreachable" });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "error", message = ex.Message });
        }
    }
}
