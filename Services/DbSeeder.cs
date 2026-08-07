using System;
using System.Linq;
using MaxChemical.DtuServer.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>启动时确保数据库已建,并在无管理员时创建一个管理员账号。</summary>
    public static class DbSeeder
    {
        public static void EnsureCreatedAndSeed(IServiceProvider sp, IConfiguration cfg, ILogger logger)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();

            // 轻量迁移:给已存在的库补新列。先查现有列,只补缺失的,避免重复 ALTER 触发失败日志;不删库保数据。
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            var existingCols = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA table_info(Devices)";
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) existingCols.Add(rd.GetString(1)); // 列1 = name
            }
            finally { if (!wasOpen) conn.Close(); }

            foreach (var (col, sql) in new[]
            {
                ("ImageData", "ALTER TABLE Devices ADD COLUMN ImageData BLOB"),
                ("ImageMime", "ALTER TABLE Devices ADD COLUMN ImageMime TEXT"),
            })
            {
                if (!existingCols.Contains(col)) db.Database.ExecuteSqlRaw(sql);
            }

            if (db.Users.Any(u => u.Role == "Admin")) return;

            var adminUser = cfg["Admin:Username"] ?? "admin";
            var adminPwd = cfg["Admin:Password"];
            if (string.IsNullOrWhiteSpace(adminPwd))
            {
                // 没配密码就给个随机的并打到日志,提醒首次登录后修改。
                adminPwd = Guid.NewGuid().ToString("N").Substring(0, 12);
                logger.LogWarning("未配置 Admin:Password,已生成临时管理员密码: {Pwd} (请尽快在 appsettings 设置并重启)", adminPwd);
            }

            var (ph, ps) = SecurityUtil.Hash(adminPwd);
            var apiKey = SecurityUtil.NewApiKey();
            var apiSecret = SecurityUtil.NewApiSecret();
            var (sh, ss) = SecurityUtil.Hash(apiSecret);

            db.Users.Add(new User
            {
                Username = adminUser,
                Email = cfg["Admin:Email"] ?? "",
                Role = "Admin",
                PasswordHash = ph,
                PasswordSalt = ps,
                ApiKey = apiKey,
                ApiSecretHash = sh,
                ApiSecretSalt = ss,
            });
            db.SaveChanges();

            logger.LogInformation("已创建管理员 '{User}'。API Key={Key} API Secret={Secret} (Secret 仅此一次显示)",
                adminUser, apiKey, apiSecret);
        }
    }
}
