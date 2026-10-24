using System.Collections.Generic;
using System.Text.RegularExpressions;
using OpenFleet.Contracts;

namespace OpenFleet.Layout
{
    /// <summary>模組的數量規則：至少一台、剛好一個、不限。</summary>
    public enum ModuleCountRule
    {
        AtLeastOne,
        ExactlyOne,
        Unlimited,
    }

    /// <summary>
    /// 目錄裡一種模組的規格。座標是區域座標：模組中心是原點，朝向 0 時交接點在 +Z 側（朝走道）。
    /// 占地是矩形（寬 X × 深 Z），中心偏移 (OffsetX, OffsetZ)。
    /// </summary>
    public sealed class ModuleSpec
    {
        public ModuleKind Kind { get; set; }

        /// <summary>工序代碼（對應 ProcessSteps.Code），只有製程種類有；其他是 null。</summary>
        public string? ProcessCode { get; set; }

        /// <summary>固定代碼（原料倉、成品倉、出貨、刀具庫）；其他是 null。</summary>
        public string? FixedCode { get; set; }

        public double Width { get; set; }
        public double Depth { get; set; }
        public double OffsetX { get; set; }
        public double OffsetZ { get; set; }

        public bool HasHandoff { get; set; }
        public double HandoffX { get; set; }
        public double HandoffZ { get; set; }

        /// <summary>Unity 端的模型名稱（Blender 模型，或程式組裝）。</summary>
        public string? Model { get; set; }

        public int? InboundCapacity { get; set; }
        public int? OutboundCapacity { get; set; }

        public string ZhName { get; set; } = "";
        public string EnName { get; set; } = "";

        /// <summary>量詞（號機、號爐…），沒有量詞的種類是 null。</summary>
        public string? Unit { get; set; }

        public ModuleCountRule Count { get; set; }
    }

    /// <summary>模組目錄：每一種模組的占地、交接點、模型、容量與名稱。工序的順序、時間與刀具以資料庫的 ProcessSteps 為準，這裡只負責對應。</summary>
    public static class ModuleCatalog
    {
        /// <summary>九道製程工序，依加工順序排列。</summary>
        public static readonly string[] ProcessCodes = { "cutting", "rough", "fine", "heat", "temper", "plating", "polish", "qc", "packing" };

        /// <summary>倉儲站的固定代碼。</summary>
        public static readonly string[] FacilityCodes = { "raw-store", "finished-store", "shipping", "tool-store" };

        private static readonly Dictionary<ModuleKind, ModuleSpec> Specs = Build();

        public static ModuleSpec Get(ModuleKind kind) => Specs[kind];

        public static IEnumerable<ModuleSpec> All => Specs.Values;

        /// <summary>製程種類（逐件或整爐）。</summary>
        public static bool IsProcess(ModuleKind kind) => Specs[kind].ProcessCode != null;

        /// <summary>四種必須剛好一個的倉儲站。</summary>
        public static bool IsFacility(ModuleKind kind) =>
            kind == ModuleKind.RawMaterialStore || kind == ModuleKind.FinishedGoodsStore || kind == ModuleKind.ShippingDock || kind == ModuleKind.ToolStore;

        /// <summary>AGV 車位（大型或小型）。</summary>
        public static bool IsBay(ModuleKind kind) => kind == ModuleKind.AgvBayLarge || kind == ModuleKind.AgvBaySmall;

        /// <summary>工站（製程與倉儲站）：有交接點，會派車送料。</summary>
        public static bool IsStation(ModuleKind kind) => IsProcess(kind) || IsFacility(kind);

        /// <summary>
        /// 工序代碼對應的模組種類；沒有這個工序時回傳 null。
        /// </summary>
        public static ModuleKind? ProcessKindOf(string processCode)
        {
            foreach (var spec in Specs.Values)
            {
                if (spec.ProcessCode == processCode)
                {
                    return spec.Kind;
                }
            }

            return null;
        }

        /// <summary>
        /// 代碼是否符合該種類的規則：製程機台 {工序代碼}-{n}（n 從 1 起）、倉儲站固定代碼、
        /// AGV 車位 agv-NN（兩位數以上）、裝飾物 misc-N／carrier-N。
        /// </summary>
        public static bool IsValidCode(ModuleKind kind, string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }

            var spec = Specs[kind];
            if (spec.ProcessCode != null)
            {
                return Regex.IsMatch(code, "^" + Regex.Escape(spec.ProcessCode) + "-[1-9][0-9]*$");
            }

            if (spec.FixedCode != null)
            {
                return code == spec.FixedCode;
            }

            if (IsBay(kind))
            {
                return Regex.IsMatch(code, "^agv-[0-9]{2,}$");
            }

            if (kind == ModuleKind.MiscStore)
            {
                return Regex.IsMatch(code, "^misc-[1-9][0-9]*$");
            }

            return Regex.IsMatch(code, "^carrier-[1-9][0-9]*$");
        }

        private static Dictionary<ModuleKind, ModuleSpec> Build()
        {
            var list = new List<ModuleSpec>
            {
                Process(ModuleKind.Cutting, "cutting", "Station_Cutting", 30, "切料", "Cutting", "號機"),
                Process(ModuleKind.RoughLathe, "rough", "Station_CncLathe", 30, "粗加工", "Rough", "號機"),
                Process(ModuleKind.FineLathe, "fine", "Station_CncLathe", 30, "精加工", "Fine", "號機"),
                Process(ModuleKind.QuenchFurnace, "heat", "Station_HeatTreatment", null, "淬火", "Quench", "號爐"),
                Process(ModuleKind.TemperFurnace, "temper", "Station_HeatTreatment", null, "回火", "Temper", "號爐"),
                Process(ModuleKind.PlatingLine, "plating", "Station_Plating", null, "電鍍", "Plating", "號槽"),
                Process(ModuleKind.Polisher, "polish", "Station_Polishing", null, "拋光", "Polishing", "號機"),
                Process(ModuleKind.InspectionBench, "qc", "Station_QualityInspection", null, "品管", "Inspection", "號站"),
                Process(ModuleKind.PackingBench, "packing", "Station_Packing", null, "包裝", "Packing", "號站"),
                Facility(ModuleKind.RawMaterialStore, "raw-store", "Station_RawMaterialStore", "原料倉", "Raw Material"),
                Facility(ModuleKind.FinishedGoodsStore, "finished-store", "Station_FinishedGoodsStore", "成品倉", "Finished Goods"),
                Facility(ModuleKind.ShippingDock, "shipping", "Station_Shipping", "出貨", "Shipping"),
                Facility(ModuleKind.ToolStore, "tool-store", "Station_ToolStore", "刀具庫", "Tool Store"),
                Bay(ModuleKind.AgvBayLarge, "大型 AGV", "Large AGV"),
                Bay(ModuleKind.AgvBaySmall, "小型 AGV", "Small AGV"),
                Decor(ModuleKind.MiscStore, "Station_MiscStore", "雜物倉庫", "General Storage", 6.4, 4.6, 0, -0.1, ModuleCountRule.Unlimited),
                Decor(ModuleKind.CarrierStorage, null, "載具存放區", "Bins / Pallets", 7.0, 3.4, 0, 0, ModuleCountRule.Unlimited),
            };

            var result = new Dictionary<ModuleKind, ModuleSpec>();
            foreach (var spec in list)
            {
                result[spec.Kind] = spec;
            }

            return result;
        }

        // 製程機台與倉儲站共用的占地與交接點（區域 (0, 3.4)）。車床與其他製程站的占地都是 6.4 × 4.6，
        // 和 Unity 的區域框線一致（車床間距 8 m，見實作紀錄第 4 節）。
        private static ModuleSpec Process(ModuleKind kind, string code, string model, int? capacity, string zh, string en, string unit) => new ModuleSpec
        {
            Kind = kind,
            ProcessCode = code,
            Width = 6.4,
            Depth = 4.6,
            OffsetX = 0,
            OffsetZ = -0.1,
            HasHandoff = true,
            HandoffX = 0,
            HandoffZ = 3.4,
            Model = model,
            InboundCapacity = capacity,
            OutboundCapacity = capacity,
            ZhName = zh,
            EnName = en,
            Unit = unit,
            Count = ModuleCountRule.AtLeastOne,
        };

        private static ModuleSpec Facility(ModuleKind kind, string code, string model, string zh, string en) => new ModuleSpec
        {
            Kind = kind,
            FixedCode = code,
            Width = 6.4,
            Depth = 4.6,
            OffsetX = 0,
            OffsetZ = -0.1,
            HasHandoff = true,
            HandoffX = 0,
            HandoffZ = 3.4,
            Model = model,
            ZhName = zh,
            EnName = en,
            Count = ModuleCountRule.ExactlyOne,
        };

        // AGV 車位：車停在中心往 +Z 0.2 m 的節點上，占地 1.6 × 3.0（中心往 -Z 偏 0.15）。
        private static ModuleSpec Bay(ModuleKind kind, string zh, string en) => new ModuleSpec
        {
            Kind = kind,
            Width = 1.6,
            Depth = 3.0,
            OffsetX = 0,
            OffsetZ = -0.15,
            HasHandoff = true,
            HandoffX = 0,
            HandoffZ = 0.2,
            Model = "ChargingBay",
            ZhName = zh,
            EnName = en,
            Count = ModuleCountRule.AtLeastOne,
        };

        private static ModuleSpec Decor(ModuleKind kind, string? model, string zh, string en, double width, double depth, double offsetX, double offsetZ, ModuleCountRule rule) => new ModuleSpec
        {
            Kind = kind,
            Width = width,
            Depth = depth,
            OffsetX = offsetX,
            OffsetZ = offsetZ,
            HasHandoff = false,
            Model = model,
            ZhName = zh,
            EnName = en,
            Count = rule,
        };
    }
}
