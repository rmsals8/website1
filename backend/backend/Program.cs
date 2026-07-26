using backend.Data;
using backend.Services;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;

// .env 파일(로컬 전용, 깃허브에 안 올라감)을 프로세스 환경변수로 로드.
// 배포 환경(Cloudtype 등)에서는 .env 없이 플랫폼이 제공하는 환경변수를 그대로 사용하면 됨.
Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

const string FrontendCorsPolicy = "FrontendDev";

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// MariaDB 연결 (ConnectionStrings:Default <- .env의 ConnectionStrings__Default)
var connectionString = builder.Configuration.GetConnectionString("Default");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
}

// PDF 편집 파이프라인 서비스
builder.Services.AddSingleton<PdfSessionStore>();
builder.Services.AddSingleton<PdfRenderService>();
builder.Services.AddSingleton<TrialUsageService>();
builder.Services.AddSingleton<ExportProgressService>();
builder.Services.AddHostedService<SessionCleanupService>();

// 프론트엔드(Vite, 기본 5173 포트) 개발 서버 허용.
// 쿠키 기반 trial_id를 주고받아야 하므로 AllowCredentials 필요 -> AllowAnyOrigin과 함께 쓸 수 없어 명시적 Origin 지정.
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendCorsPolicy, policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 로컬 개발(http-only)에서는 리다이렉트할 https 포트가 없어 경고만 찍고 넘어감.
// 배포 환경(Cloudtype 등)에서는 앞단 프록시가 https를 이미 처리해서 내부로는 http로 전달하므로,
// 여기서 또 https로 리다이렉트를 걸면 무한 리다이렉트 루프가 생길 수 있어 Development에서만 사용한다.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors(FrontendCorsPolicy);

app.UseAuthorization();

app.MapControllers();

app.Run();
