using Microsoft.EntityFrameworkCore;

namespace MaxChemical.DtuServer.Data
{
    /// <summary>平台数据(SQLite 单文件持久化):用户、设备、授权关系。</summary>
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users => Set<User>();
        public DbSet<Device> Devices => Set<Device>();
        public DbSet<Grant> Grants => Set<Grant>();

        protected override void OnModelCreating(ModelBuilder b)
        {
            b.Entity<User>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Username).IsUnique();
                e.HasIndex(x => x.ApiKey).IsUnique();
            });

            b.Entity<Device>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => x.Code).IsUnique();
            });

            b.Entity<Grant>(e =>
            {
                e.HasKey(x => x.Id);
                e.HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
            });
        }
    }
}
