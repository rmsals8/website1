using backend.Models;
using Microsoft.EntityFrameworkCore;

namespace backend.Data;

/// <summary>
/// MariaDB 연결 확인용 최소 DbContext.
/// 스키마(Users/UserLogins/Documents 등)는 인증 방식이 확정된 뒤 DbSet과 마이그레이션을 추가할 예정.
/// 지금은 연결 문자열 배선과 헬스체크, 다운로드 카운트 목적만 담당한다.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<DownloadEvent> DownloadEvents => Set<DownloadEvent>();

    // TODO: 인증 방식 확정 후 추가
    // public DbSet<User> Users => Set<User>();
    // public DbSet<UserLogin> UserLogins => Set<UserLogin>();
}
