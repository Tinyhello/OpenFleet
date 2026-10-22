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
    public DbSet<BatchRun> BatchRuns => Set<BatchRun>();
    public DbSet<StationTool> StationTools => Set<StationTool>();
    public DbSet<ToolChangeRecord> ToolChanges => Set<ToolChangeRecord>();
    public DbSet<MaterialLot> Lots => Set<MaterialLot>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<OperationExecution> Operations => Set<OperationExecution>();
    public DbSet<InspectionResult> InspectionResults => Set<InspectionResult>();
    public DbSet<TruckSchedule> TruckSchedules => Set<TruckSchedule>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<RouteNode> RouteNodes => Set<RouteNode>();
    public DbSet<RouteEdge> RouteEdges => Set<RouteEdge>();
    public DbSet<TransportTask> TransportTasks => Set<TransportTask>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<VehicleWait> VehicleWaits => Set<VehicleWait>();
    public DbSet<MachineDelay> MachineDelays => Set<MachineDelay>();
    public DbSet<RodSize> RodSizes => Set<RodSize>();
    public DbSet<PunchSpec> PunchSpecs => Set<PunchSpec>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<BomVersion> BomVersions => Set<BomVersion>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<CuttingJob> CuttingJobs => Set<CuttingJob>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ScrapRecord> ScrapRecords => Set<ScrapRecord>();
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
        configuration.Properties<MachineDelayCause>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<ShortageReason>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<TaskResolution>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<ProcessStepKind>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<ItemKind>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<StockMovementReason>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<PurchaseOrderStatus>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<ProductionOrderKind>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<RodSize>(e =>
        {
            e.HasKey(r => r.DiameterMm);
            e.Property(r => r.DiameterMm).ValueGeneratedNever();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_RodSizes_PiecesPerBin", "\"PiecesPerBin\" > 0");
                t.HasCheckConstraint("CK_RodSizes_BlankLength",
                    "\"UpperBlankLengthMm\" > 0 AND \"DieBlankLengthMm\" > 0 AND \"LowerBlankLengthMm\" > 0");
            });
        });

        model.Entity<PunchSpec>(e =>
        {
            e.HasKey(s => s.Code);
            e.Property(s => s.Code).HasMaxLength(32);
            e.Property(s => s.Name).HasMaxLength(64);
            e.Property(s => s.Shape).HasMaxLength(64);
            e.HasOne(s => s.Rod).WithMany().HasForeignKey(s => s.RodDiameterMm).OnDelete(DeleteBehavior.Restrict);
            // 覆寫值可以留空（沿用），有填就必須是正數。
            e.ToTable(t => t.HasCheckConstraint("CK_PunchSpecs_Overrides",
                "(\"UpperBlankLengthMm\" IS NULL OR \"UpperBlankLengthMm\" > 0) AND " +
                "(\"DieBlankLengthMm\" IS NULL OR \"DieBlankLengthMm\" > 0) AND " +
                "(\"LowerBlankLengthMm\" IS NULL OR \"LowerBlankLengthMm\" > 0) AND " +
                "(\"RoughSecondsPerPiece\" IS NULL OR \"RoughSecondsPerPiece\" > 0) AND " +
                "(\"FinishSecondsPerPiece\" IS NULL OR \"FinishSecondsPerPiece\" > 0)"));
        });

        model.Entity<Item>(e =>
        {
            e.HasKey(i => i.Code);
            e.Property(i => i.Code).HasMaxLength(48);
            e.Property(i => i.Name).HasMaxLength(96);
            e.Property(i => i.PunchSpecCode).HasMaxLength(32);
            e.Property(i => i.PartCode).HasMaxLength(32);
            e.HasIndex(i => new { i.Kind, i.PunchSpecCode });
            e.HasOne<PunchSpec>().WithMany().HasForeignKey(i => i.PunchSpecCode).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<BomVersion>(e =>
        {
            e.Property(b => b.Id).ValueGeneratedNever();
            e.Property(b => b.ItemCode).HasMaxLength(48);
            // 同一個品項的版本號不重複；兩個請求同時發新版時由資料庫擋下其中一個。
            e.HasIndex(b => new { b.ItemCode, b.Version }).IsUnique();
            e.HasOne<Item>().WithMany().HasForeignKey(b => b.ItemCode).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(b => b.Lines).WithOne().HasForeignKey(l => l.BomVersionId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_BomVersions_Version", "\"Version\" > 0"));
        });

        model.Entity<BomLine>(e =>
        {
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.ChildItemCode).HasMaxLength(48);
            e.HasIndex(l => new { l.BomVersionId, l.ChildItemCode }).IsUnique();
            e.HasOne<Item>().WithMany().HasForeignKey(l => l.ChildItemCode).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_BomLines_Quantity", "\"Quantity\" > 0"));
        });

        model.Entity<StockBalance>(e =>
        {
            e.Property(b => b.Id).ValueGeneratedNever();
            e.Property(b => b.ItemCode).HasMaxLength(48);
            e.Property(b => b.StationCode).HasMaxLength(32);
            e.Property(b => b.Version).IsRowVersion();
            e.Ignore(b => b.Available);
            // 一個品項在一個站點只有一列；兩邊同時建立第一列時由資料庫擋下其中一個。
            e.HasIndex(b => new { b.ItemCode, b.StationCode }).IsUnique();
            e.HasOne<Item>().WithMany().HasForeignKey(b => b.ItemCode).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Station>().WithMany().HasForeignKey(b => b.StationCode).OnDelete(DeleteBehavior.Restrict);
            // 程式寫錯時由資料庫擋下：不會是負數，保留的不會比在庫多。
            e.ToTable(t => t.HasCheckConstraint("CK_StockBalances_Quantities",
                "\"OnHand\" >= 0 AND \"Reserved\" >= 0 AND \"Reserved\" <= \"OnHand\""));
        });

        model.Entity<StockReservation>(e =>
        {
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.ItemCode).HasMaxLength(48);
            e.Property(r => r.Reference).HasMaxLength(64);
            e.HasIndex(r => r.Reference);
            e.HasOne<StockBalance>().WithMany().HasForeignKey(r => r.StockBalanceId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_StockReservations_Quantity",
                "\"Quantity\" > 0 AND \"Remaining\" >= 0 AND \"Remaining\" <= \"Quantity\""));
        });

        model.Entity<StockMovement>(e =>
        {
            e.Property(m => m.ItemCode).HasMaxLength(48);
            e.Property(m => m.StationCode).HasMaxLength(32);
            e.Property(m => m.Reference).HasMaxLength(64);
            e.HasIndex(m => new { m.ItemCode, m.Id });
            e.HasOne<Item>().WithMany().HasForeignKey(m => m.ItemCode).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Station>().WithMany().HasForeignKey(m => m.StationCode).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<PurchaseOrder>(e =>
        {
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Number).HasMaxLength(32);
            e.Property(o => o.ItemCode).HasMaxLength(48);
            e.Property(o => o.Supplier).HasMaxLength(64);
            e.Property(o => o.RequestKey).HasMaxLength(64);
            e.Property(o => o.Version).IsRowVersion();
            e.HasIndex(o => o.Number).IsUnique();
            e.HasIndex(o => o.RequestKey).IsUnique().HasFilter("\"RequestKey\" IS NOT NULL");
            e.HasIndex(o => new { o.Status, o.ItemCode });
            e.HasOne<Item>().WithMany().HasForeignKey(o => o.ItemCode).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_PurchaseOrders_Quantity", "\"Quantity\" > 0"));
        });

        model.Entity<SalesOrder>(e =>
        {
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Number).HasMaxLength(32);
            e.Property(o => o.Customer).HasMaxLength(64);
            e.Property(o => o.RequestKey).HasMaxLength(64);
            e.Ignore(o => o.NumberSuffix);
            e.HasIndex(o => o.Number).IsUnique();
            // 同一個 Idempotency-Key 只會有一張訂單；兩個同鍵的請求同時到，由資料庫擋下後到的那一個。
            e.HasIndex(o => o.RequestKey).IsUnique().HasFilter("\"RequestKey\" IS NOT NULL");
            e.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.SalesOrderId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<SalesOrderLine>(e =>
        {
            e.Property(l => l.Id).ValueGeneratedNever();
            e.Property(l => l.ItemCode).HasMaxLength(48);
            e.HasIndex(l => new { l.SalesOrderId, l.LineNumber }).IsUnique();
            e.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemCode).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_SalesOrderLines_Quantity", "\"Quantity\" > 0"));
        });

        model.Entity<ProductionOrder>(e =>
        {
            e.Property(o => o.Id).ValueGeneratedNever();
            e.Property(o => o.Number).HasMaxLength(32);
            e.Property(o => o.ItemCode).HasMaxLength(48);
            e.Property(o => o.PartCode).HasMaxLength(32);
            e.Ignore(o => o.Parts);
            e.HasIndex(o => o.Number).IsUnique();
            e.HasIndex(o => o.ParentId);
            e.HasIndex(o => o.SalesOrderLineId);
            e.HasOne<SalesOrderLine>().WithMany().HasForeignKey(o => o.SalesOrderLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(o => o.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Item>().WithMany().HasForeignKey(o => o.ItemCode).OnDelete(DeleteBehavior.Restrict);
            // 開立時的規格存成同一張表的欄位（Spec_ 開頭）。
            e.OwnsOne(o => o.Spec, spec =>
            {
                spec.Property(s => s.SpecCode).HasMaxLength(32);
                spec.Property(s => s.SpecName).HasMaxLength(64);
            });
            e.Navigation(o => o.Spec).IsRequired();
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ProductionOrders_Quantity", "\"Quantity\" > 0");
                // 主工單沒有上層；補做工單一定連回主工單。
                t.HasCheckConstraint("CK_ProductionOrders_Parent", "(\"Kind\" = 'Main') = (\"ParentId\" IS NULL)");
            });
        });

        model.Entity<ScrapRecord>(e =>
        {
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.LotNumber).HasMaxLength(64);
            e.Property(r => r.PartCode).HasMaxLength(32);
            // 同一框的同一次檢驗只記一筆，不會重複補做。
            e.HasIndex(r => new { r.LotId, r.InspectionRound }).IsUnique();
            e.HasIndex(r => r.MainOrderId);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(r => r.MainOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(r => r.SourceOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(r => r.MakeupOrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<MaterialLot>().WithMany().HasForeignKey(r => r.LotId).OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_ScrapRecords_Quantity", "\"Quantity\" > 0"));
        });

        model.Entity<CuttingJob>(e =>
        {
            e.Property(j => j.Id).ValueGeneratedNever();
            e.Property(j => j.LotNumber).HasMaxLength(64);
            e.Property(j => j.BlankItemCode).HasMaxLength(48);
            e.Property(j => j.BarItemCode).HasMaxLength(48);
            e.Ignore(j => j.IsReady);
            e.Ignore(j => j.BarsMissing);
            // 一框最多一筆切料。
            e.HasIndex(j => j.LotId).IsUnique();
            e.HasOne<MaterialLot>().WithOne().HasForeignKey<CuttingJob>(j => j.LotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Item>().WithMany().HasForeignKey(j => j.BlankItemCode).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Item>().WithMany().HasForeignKey(j => j.BarItemCode).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_CuttingJobs_Quantities",
                "\"Bars\" >= 0 AND \"BarsReserved\" >= 0 AND \"BarsReserved\" <= \"Bars\" AND \"PiecesPerBar\" > 0 AND \"ExtraBlanks\" >= 0"));
        });

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
            e.Property(s => s.StageName).HasMaxLength(32);
            e.Ignore(s => s.IsManual);
            // 每種工序只檢查自己用到的欄位。
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ProcessSteps_SecondsPerPiece", "\"Kind\" <> 'Piece' OR \"SecondsPerPiece\" > 0");
                t.HasCheckConstraint("CK_ProcessSteps_ToolLife", "NOT \"UsesTool\" OR \"ToolLifePieces\" > 0");
                t.HasCheckConstraint("CK_ProcessSteps_TestSpeed", "\"TestSpeed\" >= 1");
                t.HasCheckConstraint("CK_ProcessSteps_Batch",
                    "\"Kind\" <> 'Batch' OR (\"BatchMinPieces\" > 0 AND \"BatchMaxPieces\" >= \"BatchMinPieces\" AND \"BatchSeconds\" > 0)");
            });
        });

        model.Entity<BatchRun>(e =>
        {
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.StationCode).HasMaxLength(32);
            e.Ignore(r => r.IsRunning);
            e.Ignore(r => r.Progress);
            e.HasOne<Station>().WithMany().HasForeignKey(r => r.StationCode).OnDelete(DeleteBehavior.Restrict);
            // 一站同一時間只有一爐在進行。
            e.HasIndex(r => r.StationCode).IsUnique().HasFilter("\"CompletedAt\" IS NULL").HasDatabaseName("UX_BatchRuns_RunningStation");
        });

        model.Entity<StationTool>(e =>
        {
            e.HasKey(t => t.StationCode);
            e.Property(t => t.StationCode).HasMaxLength(32);
            e.Ignore(t => t.IsChanging);
            e.Ignore(t => t.IsWaitingForSpare);
            e.Ignore(t => t.IsStopped);
            e.HasOne<Station>().WithOne().HasForeignKey<StationTool>(t => t.StationCode).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ToolChangeRecord>(e =>
        {
            e.ToTable("ToolChanges");
            e.Property(r => r.StationCode).HasMaxLength(32);
            e.HasIndex(r => new { r.StationCode, r.Id });
            e.HasOne<Station>().WithMany().HasForeignKey(r => r.StationCode).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ProductionSettings>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.SourceStationCode).HasMaxLength(32);
            e.Property(s => s.BufferStationCode).HasMaxLength(32);
            e.Property(s => s.ToolStoreStationCode).HasMaxLength(32);
            e.Ignore(s => s.EffectiveHandoverSeconds);
            e.Ignore(s => s.TruckSpeed);
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
            // 框號會一路加上後綴（零件、反工、補做），所以比批次編號長。
            e.Property(l => l.LotNumber).HasMaxLength(64);
            e.Property(l => l.BatchNumber).HasMaxLength(32);
            e.HasIndex(l => l.BatchNumber);
            e.Property(l => l.PartCode).HasMaxLength(32);
            e.Property(l => l.SpecCode).HasMaxLength(32);
            e.HasIndex(l => l.ProductionOrderId);
            e.HasOne<ProductionOrder>().WithMany().HasForeignKey(l => l.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(l => l.StationCode).HasMaxLength(32);
            e.Property(l => l.VehicleId).HasMaxLength(32);
            e.Property(l => l.Version).IsRowVersion();
            e.Ignore(l => l.Location);
            e.HasIndex(l => l.LotNumber).IsUnique();
            e.HasIndex(l => new { l.StationCode, l.Status });
            e.HasOne<Station>().WithMany().HasForeignKey(l => l.StationCode).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Vehicle>().WithMany().HasForeignKey(l => l.VehicleId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<InspectionResult>(e =>
        {
            e.Property(r => r.StationCode).HasMaxLength(32);
            e.HasOne<MaterialLot>().WithMany().HasForeignKey(r => r.LotId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TruckSchedule>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("CK_TruckSchedules_Singleton", "\"Id\" = 1"));
        });

        model.Entity<Shipment>(e =>
        {
            e.Property(s => s.BatchNumber).HasMaxLength(32);
            e.Property(s => s.PartCode).HasMaxLength(32);
            e.Property(s => s.LotNumber).HasMaxLength(64);
            e.HasOne<MaterialLot>().WithMany().HasForeignKey(s => s.LotId).OnDelete(DeleteBehavior.Cascade);
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

        model.Entity<RouteNode>(e =>
        {
            e.HasKey(n => n.Code);
            e.Property(n => n.Code).HasMaxLength(32);
        });

        model.Entity<RouteEdge>(e =>
        {
            e.Property(r => r.FromCode).HasMaxLength(32);
            e.Property(r => r.ToCode).HasMaxLength(32);
            e.HasIndex(r => new { r.FromCode, r.ToCode }).IsUnique();
            e.HasOne<RouteNode>().WithMany().HasForeignKey(r => r.FromCode).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RouteNode>().WithMany().HasForeignKey(r => r.ToCode).OnDelete(DeleteBehavior.Cascade);
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
            e.Ignore(t => t.NeedsAttention);
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
            e.Ignore(a => a.IsLoaded);
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

        model.Entity<VehicleWait>(e =>
        {
            e.Ignore(w => w.IsOpen);
            e.HasOne<TransportTask>().WithMany().HasForeignKey(w => w.TaskId).OnDelete(DeleteBehavior.Cascade);
            // 一個任務同一時間只有一筆進行中的等待；一台機台同一時間也只有一筆進行中的耽誤。
            e.HasIndex(w => w.TaskId).IsUnique().HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("UX_VehicleWaits_OpenTask");
        });

        model.Entity<MachineDelay>(e =>
        {
            e.Ignore(d => d.IsOpen);
            e.Property(d => d.StationCode).HasMaxLength(32);
            e.HasOne<TransportTask>().WithMany().HasForeignKey(d => d.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Station>().WithMany().HasForeignKey(d => d.StationCode).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => d.StationCode).IsUnique().HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("UX_MachineDelays_OpenStation");
            e.HasIndex(d => d.StepSequence);
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
