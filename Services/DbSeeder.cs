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

            // ── 轻量迁移 ──────────────────────────────────────────────────────
            // EnsureCreated 只在"库文件不存在"时建全部表:库已存在时,新增的表/列它一律不管。
            // 所以新表要显式 CREATE TABLE IF NOT EXISTS,新列要查过 PRAGMA 再 ALTER。
            // 全程不删库、不动老表数据。

            // 1) 新表(老库升级用)。列定义与 EF Core 对 SQLite 的默认映射保持一致,
            //    保证"新建库(EnsureCreated 建的)"和"老库升级"最终结构相同。
            db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""ProductModels"" (
    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_ProductModels"" PRIMARY KEY,
    ""ProductKey"" TEXT NOT NULL,
    ""ModelName"" TEXT NOT NULL,
    ""ModelVersion"" TEXT NOT NULL,
    ""Description"" TEXT NOT NULL,
    ""Manufacturer"" TEXT NOT NULL,
    ""Icon"" TEXT NOT NULL,
    ""Photo"" TEXT NOT NULL,
    ""ConfigJson"" TEXT NOT NULL,
    ""Revision"" INTEGER NOT NULL,
    ""Status"" TEXT NOT NULL,
    ""PublishedAt"" TEXT NULL,
    ""PublishedRevision"" INTEGER NOT NULL,
    ""CreatedByUserId"" TEXT NOT NULL,
    ""CreatedAt"" TEXT NOT NULL,
    ""UpdatedAt"" TEXT NOT NULL
)");
            db.Database.ExecuteSqlRaw(
                @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ProductModels_ProductKey"" ON ""ProductModels"" (""ProductKey"")");

            db.Database.ExecuteSqlRaw(@"
CREATE TABLE IF NOT EXISTS ""DeviceAlarms"" (
    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_DeviceAlarms"" PRIMARY KEY,
    ""DeviceCode"" TEXT NOT NULL,
    ""Event"" TEXT NOT NULL,
    ""Level"" TEXT NOT NULL,
    ""ParamsJson"" TEXT NOT NULL,
    ""Timestamp"" INTEGER NOT NULL,
    ""CreatedAt"" TEXT NOT NULL
)");
            db.Database.ExecuteSqlRaw(
                @"CREATE INDEX IF NOT EXISTS ""IX_DeviceAlarms_DeviceCode_CreatedAt"" ON ""DeviceAlarms"" (""DeviceCode"", ""CreatedAt"")");

            // 2) 新列。SQLite 的 ALTER TABLE ADD COLUMN 加 NOT NULL 列时必须带 DEFAULT。
            //    AccessMode 默认 'Passthrough' → 所有老设备自动归到透传模式,行为一个字不变。
            foreach (var (table, col, sql) in new[]
            {
                ("Devices", "ImageData",  @"ALTER TABLE ""Devices"" ADD COLUMN ""ImageData"" BLOB"),
                ("Devices", "ImageMime",  @"ALTER TABLE ""Devices"" ADD COLUMN ""ImageMime"" TEXT"),
                ("Devices", "AccessMode", @"ALTER TABLE ""Devices"" ADD COLUMN ""AccessMode"" TEXT NOT NULL DEFAULT 'Passthrough'"),
                ("Devices", "ProductKey", @"ALTER TABLE ""Devices"" ADD COLUMN ""ProductKey"" TEXT NULL"),
            })
            {
                if (!ColumnExists(db, table, col)) db.Database.ExecuteSqlRaw(sql);
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

        /// <summary>
        /// 表里有没有这一列。用来决定要不要 ALTER —— 只补缺的,重复启动不会刷失败日志。
        /// table/column 都是本文件里写死的常量,不接受外部输入。
        /// </summary>
        private static bool ColumnExists(AppDbContext db, string table, string column)
        {
            var conn = db.Database.GetDbConnection();
            var wasOpen = conn.State == System.Data.ConnectionState.Open;
            if (!wasOpen) conn.Open();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                    if (string.Equals(rd.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        return true;   // 列1 = name
                return false;
            }
            finally { if (!wasOpen) conn.Close(); }
        }
    }
}
