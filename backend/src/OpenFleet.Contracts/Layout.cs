using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    /// <summary>
    /// 佈局中的模組種類。工序種類（前九個）對應 ProcessSteps 的工序代碼；倉儲種類是固定代碼；
    /// AGV 車位與裝飾物的代碼由編號產生（agv-NN、misc-N、carrier-N）。
    /// </summary>
    public enum ModuleKind
    {
        Cutting,
        RoughLathe,
        FineLathe,
        QuenchFurnace,
        TemperFurnace,
        PlatingLine,
        Polisher,
        InspectionBench,
        PackingBench,
        RawMaterialStore,
        FinishedGoodsStore,
        ShippingDock,
        ToolStore,
        AgvBayLarge,
        AgvBaySmall,
        MiscStore,
        CarrierStorage,
    }

    /// <summary>一份工廠佈局的文件：廠房大小、模組與路網。以 JSON 存進資料庫，也是 API 的傳輸格式。</summary>
    public sealed class FactoryLayoutDocument
    {
        public LayoutFloor Floor { get; set; } = new LayoutFloor();
        public List<LayoutModule> Modules { get; set; } = new List<LayoutModule>();
        public List<LayoutRoadNode> RoadNodes { get; set; } = new List<LayoutRoadNode>();
        public List<LayoutRoadEdge> RoadEdges { get; set; } = new List<LayoutRoadEdge>();
    }

    /// <summary>地面的範圍（公尺，X／Z）。</summary>
    public sealed class LayoutFloor
    {
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
    }

    public sealed class LayoutModule
    {
        public string Code { get; set; } = "";
        public ModuleKind Kind { get; set; }
        public double X { get; set; }
        public double Z { get; set; }

        /// <summary>朝向：0、90、180、270 度（從上往下看順時針，和 Unity 的 yaw 相同）。</summary>
        public int Rotation { get; set; }
    }

    public sealed class LayoutRoadNode
    {
        public string Code { get; set; } = "";
        public double X { get; set; }
        public double Z { get; set; }
    }

    public sealed class LayoutRoadEdge
    {
        public string From { get; set; } = "";
        public string To { get; set; } = "";
    }

    public sealed class LayoutSummaryDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsDefault { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? AppliedAt { get; set; }

        /// <summary>使用中的佈局存檔後還沒套用：存的文件和工廠正在跑的不同。非使用中的佈局恆為 false。</summary>
        public bool HasUnappliedChanges { get; set; }
    }

    public sealed class LayoutDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsDefault { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? AppliedAt { get; set; }

        /// <summary>同 <see cref="LayoutSummaryDto.HasUnappliedChanges"/>。</summary>
        public bool HasUnappliedChanges { get; set; }

        /// <summary>GET active 是工廠正在跑的（套用快照）；其他端點是存下來可編輯的文件。</summary>
        public FactoryLayoutDocument Document { get; set; } = new FactoryLayoutDocument();
    }

    public sealed class SaveLayoutRequest
    {
        public string Name { get; set; } = "";
        public FactoryLayoutDocument Document { get; set; } = new FactoryLayoutDocument();
    }

    public sealed class SaveLayoutResponse
    {
        public LayoutDto Layout { get; set; } = new LayoutDto();
        public LayoutValidationDto Validation { get; set; } = new LayoutValidationDto();
    }

    public enum LayoutIssueSeverity
    {
        Error,
        Warning,
    }

    public sealed class LayoutIssueDto
    {
        public string Code { get; set; } = "";
        public LayoutIssueSeverity Severity { get; set; }
        public string Message { get; set; } = "";
        public List<string> ModuleCodes { get; set; } = new List<string>();
        public List<string> NodeCodes { get; set; } = new List<string>();
        public List<LayoutRoadEdge> Edges { get; set; } = new List<LayoutRoadEdge>();
    }

    public sealed class LayoutValidationDto
    {
        /// <summary>沒有錯誤就是有效；警告不影響套用。</summary>
        public bool IsValid { get; set; }
        public List<LayoutIssueDto> Issues { get; set; } = new List<LayoutIssueDto>();
    }

    /// <summary>MQTT retained：目前使用中的佈局版本與車輛清單（模擬器據此增減車輛，Unity 據此重建場景）。</summary>
    public sealed class LayoutRevisionMessage
    {
        public Guid LayoutId { get; set; }
        public string Name { get; set; } = "";
        public DateTimeOffset AppliedAt { get; set; }
        public List<LayoutVehicle> Vehicles { get; set; } = new List<LayoutVehicle>();
    }

    public sealed class LayoutVehicle
    {
        public string VehicleId { get; set; } = "";
        public VehicleType Type { get; set; }
        public double HomeX { get; set; }
        public double HomeZ { get; set; }
    }

    public sealed class LayoutBusyDto
    {
        public string Message { get; set; } = "";
        public int LotsInProgress { get; set; }
        public int ActiveTasks { get; set; }
    }
}
