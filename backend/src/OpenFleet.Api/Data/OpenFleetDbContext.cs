using Microsoft.EntityFrameworkCore;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>
/// EF Core 的工作單元（Scoped）：每個 HTTP 請求、每則 MQTT 訊息、背景工作的每一輪各自建立一個，
/// 用完就丟。它不是執行緒安全的，所以 Singleton 服務不能直接持有它，要透過 IServiceScopeFactory 建立 scope。
/// </summary>
public sealed class OpenFleetDbContext(DbContextOptions<OpenFleetDbContext> options) : DbContext(options)
{
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<ProcessStep> ProcessSteps => Set<ProcessStep>();
    public DbSet<ProductionSettings> ProductionSettings => Set<ProductionSettings>();
    public DbSet<MaterialLot> Lots => Set<MaterialLot>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<OperationExecution> Operations => Set<OperationExecution>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<TransportTask> TransportTasks => Set<TransportTask>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // enum 存成字串：直接查資料表就看得懂，之後調整 enum 順序也不會讓舊資料變成別的意思。
        configuration.Properties<LotStatus>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<OperationStatus>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<TransportTaskStatus>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<TransportTaskKind>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<AssignmentStatus>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<VehicleType>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Station>(e =>
        {
            e.HasKey(s => s.Code);
            e.Property(s => s.Code).HasMaxLength(32);
            e.Property(s => s.Name).HasMaxLength(64);
        });

        model.Entity<ProcessStep>(e =>
        {
            e.HasKey(s => s.Sequence);
            e.Property(s => s.Sequence).ValueGeneratedNever();
            e.Property(s => s.StationCode).HasMaxLength(32);
            e.HasOne<Station>().WithMany().HasForeignKey(s => s.StationCode).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_ProcessSteps_Duration", "\"DurationSeconds\" > 0"));
        });

        model.Entity<ProductionSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.PartCode).HasMaxLength(32);
            e.Property(s => s.SourceStationCode).HasMaxLength(32);
            e.Property(s => s.BufferStationCode).HasMaxLength(32);
            e.ToTable(t => t.HasCheckConstraint("CK_ProductionSettings_Singleton", "\"Id\" = 1"));
        });

        model.Entity<MaterialLot>(e =>
        {
            e.ToTable("Lots", t =>
            {
                // 「同一時間只在一個實體位置」寫成資料庫約束，程式寫錯時由資料庫擋下。
                t.HasCheckConstraint("CK_Lots_OneLocation", "(\"StationCode\" IS NULL) <> (\"VehicleId\" IS NULL)");
                t.HasCheckConstraint("CK_Lots_Quantity", "\"Quantity\" > 0");
            });
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.LotNumber).HasMaxLength(32);
            e.Property(l => l.PartCode).HasMaxLength(32);
            e.Property(l => l.StationCode).HasMaxLength(32);
            e.Property(l => l.VehicleId).HasMaxLength(32);
            e.Property(l => l.Version).IsRowVersion();
            e.Ignore(l => l.Location);
            e.HasIndex(l => l.LotNumber).IsUnique();
            e.HasIndex(l => new { l.StationCode, l.Status });
            e.HasOne<Station>().WithMany().HasForeignKey(l => l.StationCode).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Vehicle>().WithMany().HasForeignKey(l => l.VehicleId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<InventoryMovement>(e =>
        {
            e.Property(m => m.From).HasMaxLength(48);
            e.Property(m => m.To).HasMaxLength(48);
            e.Property(m => m.Reason).HasMaxLength(32);
            e.HasIndex(m => new { m.LotId, m.Id });
            e.HasOne<MaterialLot>().WithMany().HasForeignKey(m => m.LotId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<OperationExecution>(e =>
        {
            e.ToTable("Operations");
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.StationCode).HasMaxLength(32);
            e.HasIndex(o => new { o.StationCode, o.Status });
            e.HasOne(o => o.Lot).WithMany().HasForeignKey(o => o.LotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Station>().WithMany().HasForeignKey(o => o.StationCode).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Vehicle>(e =>
        {
            e.Property(v => v.Id).HasMaxLength(32);
        });

        model.Entity<TransportTask>(e =>
        {
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.FromStationCode).HasMaxLength(32);
            e.Property(t => t.ToStationCode).HasMaxLength(32);
            e.Property(t => t.FailureReason).HasMaxLength(256);
            e.Property(t => t.Version).IsRowVersion();
            e.HasIndex(t => new { t.Status, t.CreatedAt });
            e.HasOne(t => t.Lot).WithMany().HasForeignKey(t => t.LotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Station>().WithMany().HasForeignKey(t => t.FromStationCode).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Station>().WithMany().HasForeignKey(t => t.ToStationCode).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(t => t.Assignments).WithOne(a => a.Task).HasForeignKey(a => a.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.Events).WithOne().HasForeignKey(ev => ev.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Assignment>(e =>
        {
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.VehicleId).HasMaxLength(32);
            e.Property(a => a.Reason).HasMaxLength(256);
            e.Property(a => a.Version).IsRowVersion();
            e.Ignore(a => a.IsActive);
            e.Ignore(a => a.IsAwaitingReply);
            e.HasOne<Vehicle>().WithMany().HasForeignKey(a => a.VehicleId).OnDelete(DeleteBehavior.Restrict);

            // 部分唯一索引：只對「有效」的派工要求唯一。兩個工作者同時派同一台車時，
            // 第二筆 INSERT 會被資料庫拒絕，不需要靠應用程式的鎖。
            var active = string.Join(", ", Assignment.ActiveStatuses.Select(s => $"'{s}'"));
            e.HasIndex(a => a.VehicleId).IsUnique().HasFilter($"\"Status\" IN ({active})").HasDatabaseName("UX_Assignments_ActiveVehicle");
            e.HasIndex(a => a.TaskId).IsUnique().HasFilter($"\"Status\" IN ({active})").HasDatabaseName("UX_Assignments_ActiveTask");
            e.HasIndex(a => new { a.TaskId, a.Attempt }).IsUnique();
        });

        model.Entity<TaskEvent>(e =>
        {
            e.Property(ev => ev.VehicleId).HasMaxLength(32);
            e.Property(ev => ev.Reason).HasMaxLength(256);
            e.HasOne(ev => ev.Assignment).WithMany().HasForeignKey(ev => ev.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.Property(m => m.Topic).HasMaxLength(256);
            // 只有尚未發送的列需要被快速找到。
            e.HasIndex(m => m.Id).HasFilter("\"PublishedAt\" IS NULL").HasDatabaseName("IX_OutboxMessages_Unpublished");
        });

        model.Entity<InboxMessage>(e =>
        {
            e.HasKey(m => new { m.ProducerId, m.MessageId });
            e.Property(m => m.ProducerId).HasMaxLength(64);
        });
    }
}
