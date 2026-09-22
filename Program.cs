using MaxChemical.DtuServer;
using MaxChemical.DtuServer.Data;
using MaxChemical.DtuServer.Devices;
using MaxChemical.DtuServer.Endpoints;
using MaxChemical.DtuServer.Hubs;
using MaxChemical.DtuServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// 以系统服务方式运行(开机自启)。两句各认各的平台、在对方平台上是空操作,
// 所以同一份代码在 Windows 服务和 Linux systemd 下都能正确托管:
//   Windows → 走 SCM;Linux → 走 systemd(Type=notify 的就绪通知 + journald 日志分级)
builder.Host.UseWindowsService();
builder.Host.UseSystemd();

// ── 数据层(SQLite 单文件持久化:用户/设备/授权)──
var conn = builder.Configuration.GetConnectionString("Default") ?? "Data Source=maxchemic.db";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(conn));
builder.Services.AddSingleton<TokenService>();

// ── DTU 透传(保持不变)──
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 64 * 1024;
    o.KeepAliveInterval = TimeSpan.FromSeconds(5);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<DtuServerManager>();

// 设备类型档案(读测点/下控制)。新增通讯类型 = 加一个 IDeviceProfile 并在此注册。
builder.Services.AddSingleton<IDeviceProfile, HighPreactorProfile>();
builder.Services.AddSingleton<IDeviceProfile, TwoLiquidOneGasProfile>();
builder.Services.AddSingleton<IDeviceProfile, HighTempFurnaceProfile>();
builder.Services.AddSingleton<IDeviceProfile, SiliconCarbideChipProfile>();
builder.Services.AddSingleton<DeviceProfileRegistry>();
// 设备型号目录(通讯档案 + 控制面板的命名搭配)。新增型号 = 改 DeviceModelCatalog 一行。
builder.Services.AddSingleton<DeviceModelCatalog>();
// 工艺组态上云快照(桌面「上云」上传的 SVG + 实时值,网页 process.html 读取)
builder.Services.AddSingleton<ProcessStore>();

// ── MQTT 接入(设备接入网关 + 手机APP 那条链路)──
// 与上面的 DTU 透传完全平行:Mqtt:Enabled=false(默认)时本服务空转,老部署升级零影响。
builder.Services.AddSingleton(builder.Configuration.GetSection("Mqtt").Get<MqttOptions>() ?? new MqttOptions());
builder.Services.AddSingleton<MqttGatewayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttGatewayService>());

// ── 鉴权:Web 看板/后台用 Cookie;开放 API 用 JWT Bearer ──
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login.html";
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // 对 API/后台路径,未登录返回 401(而不是 302 跳登录页),方便前端 fetch 处理
        static bool IsApiPath(PathString p) =>
            p.StartsWithSegments("/api") || p.StartsWithSegments("/admin") ||
            p.StartsWithSegments("/auth") || p.StartsWithSegments("/web");
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (IsApiPath(ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        // 已登录但权限不足(如非管理员访问 /admin):返回 403 而不是跳转
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (IsApiPath(ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.ResolveKey(builder.Configuration),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", p => p.RequireAuthenticatedUser().RequireRole("Admin"));
    options.AddPolicy("ApiBearer", p => p.RequireAuthenticatedUser()
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme));
});

// ── Swagger / OpenAPI ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "MaxChemic 开放 API", Version = "v1" });
    // 用完整类型名做 schemaId,避免不同命名空间下同名 DTO(如多处的 BindReq)冲突
    c.CustomSchemaIds(t => (t.FullName ?? t.Name).Replace("+", ".").Replace("MaxChemical.DtuServer.Endpoints.", ""));
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "先调 /api/v1/token 用 API Key/Secret 换令牌,再填到这里",
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// 反向代理转发头(Linux 上 nginx 终止 TLS 时必需,必须排在所有其他中间件前面)。
// 不加的话程序看到的永远是 http://127.0.0.1:5000:登录跳转会掉到 http、
// Secure Cookie 不生效、二维码链接也会退化。默认只信任回环地址来的转发头,
// 也就是只认本机 nginx —— 直连部署时没有这些头,这段等于不存在。
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});

// 建库 + 首启种子管理员
DbSeeder.EnsureCreatedAndSeed(app.Services, app.Configuration, app.Logger);

app.UseSwagger();
app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "MaxChemic 开放 API v1"));

// 静态网页(看板/登录/注册/账号/设备管理/绑定页)
app.UseDefaultFiles();
// 3D 模型等静态资源:显式注册 MIME,避免部分运行时把 .glb 当未知类型返回 404
var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypes.Mappings[".glb"] = "model/gltf-binary";
contentTypes.Mappings[".gltf"] = "model/gltf+json";
// 物模型示例文件用 .jsonc;未登记扩展名会被静态文件中间件当未知类型直接 404
contentTypes.Mappings[".jsonc"] = "application/json";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });

// Let's Encrypt HTTP-01 验证:放行 /.well-known/acme-challenge/ 下的明文、无扩展名文件,
// 让 80 端口的本程序直接返回验证令牌(Windows 上 win-acme、Linux 上 certbot --webroot 都用这个目录)。
//
// 建目录失败不能让整个服务起不来:Linux 的标准部署是程序目录 root 所有、服务账号只读,
// 这时 CreateDirectory 会抛 UnauthorizedAccessException —— 而 ACME 只是可选功能,
// 用 DNS-01 签发或让 nginx 终止 TLS 时根本不需要它。
var acmeDir = Path.Combine(app.Environment.ContentRootPath, "wwwroot", ".well-known", "acme-challenge");
try
{
    Directory.CreateDirectory(acmeDir);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(acmeDir),
        RequestPath = "/.well-known/acme-challenge",
        ServeUnknownFileTypes = true,          // 挑战文件无扩展名
        DefaultContentType = "text/plain",
    });
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex,
        "ACME 验证目录 {Dir} 不可用,HTTP-01 自动签发/续期将无法工作。" +
        "如果证书走 DNS-01 或由反向代理(nginx)管理,可以忽略这条。", acmeDir);
}

app.UseAuthentication();
app.UseAuthorization();

// ── 启动 DTU TCP 监听(保持不变)──
var mgr = app.Services.GetRequiredService<DtuServerManager>();
int dtuPort = app.Configuration.GetValue<int?>("DtuServer:ListenPort") ?? 9116;
int loginTimeout = app.Configuration.GetValue<int?>("DtuServer:LoginTimeoutMs") ?? 15000;
// 心跳空闲超时(毫秒):>0 才启用"长时间无收包判离线"兜底;0=仅靠 Keepalive+读循环检测断线
int heartbeatTimeout = app.Configuration.GetValue<int?>("DtuServer:HeartbeatTimeoutMs") ?? 0;

var dashboard = app.Services.GetRequiredService<IHubContext<DashboardHub>>();
mgr.DeviceOnline += info => { _ = dashboard.Clients.All.SendAsync("DeviceOnline", info); };
mgr.DeviceOffline += serial => { _ = dashboard.Clients.All.SendAsync("DeviceOffline", serial); };

// MQTT 设备的上下线/数据更新也推到同一个看板 Hub,前端不用区分来源
var mqttSvc = app.Services.GetRequiredService<MqttGatewayService>();
mqttSvc.DeviceOnlineChanged += (code, online) =>
{
    _ = online
        ? dashboard.Clients.All.SendAsync("DeviceOnline", new DtuOnlineInfo { Serial = code, ConnectedAt = DateTime.UtcNow, Online = true })
        : dashboard.Clients.All.SendAsync("DeviceOffline", code);
};
mqttSvc.DeviceDataUpdated += code => { _ = dashboard.Clients.All.SendAsync("DeviceData", code); };

mgr.Start(dtuPort, loginTimeout, heartbeatTimeout);
app.Lifetime.ApplicationStopping.Register(() => mgr.Stop());

// ── 业务端点 ──
app.MapAccount();   // /auth/*  自助注册、登录、API 凭证
app.MapWeb();       // /web/*   浏览器(Cookie)绑定、我的设备
app.MapAdmin();     // /admin/* 设备管理、二维码、授权(Admin)
app.MapApiV1();     // /api/v1/* 开放 API(Bearer)
app.MapProcess(app.Configuration); // /ingest/process/* 桌面上云 + /web/process/* 网页读取
app.MapModels();       // /admin/models/* 物模型编辑+导出+发布, /api/config/* 网关与APP拉配置
app.MapMqttDevices();  // /admin/mqtt-devices/* 网关设备管理, /web/* MQTT实时数据与指令

// ── DTU/看板 SignalR(保持不变)──
app.MapHub<DtuHub>("/dtuhub");
app.MapHub<DashboardHub>("/dashboardhub").RequireAuthorization();
app.MapGet("/api/online", () => mgr.OnlineDevices).RequireAuthorization();

app.Run();
