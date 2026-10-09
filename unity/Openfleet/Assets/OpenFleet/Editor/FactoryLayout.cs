using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    internal enum StationKind
    {
        RawMaterialStore,
        RoughMachining,
        FineMachining,
        HeatTreatment,
        Plating,
        Polishing,
        QualityInspection,
        Packing,
        Shipping,
        ToolStore,
    }

    internal readonly struct StationSpec
    {
        public StationSpec(string id, StationKind kind, string zhName, string enName, Vector2 position, bool facesSouth)
        {
            Id = id;
            Kind = kind;
            ZhName = zhName;
            EnName = enName;
            Position = position;
            FacesSouth = facesSouth;
        }

        public string Id { get; }
        public StationKind Kind { get; }
        public string ZhName { get; }
        public string EnName { get; }

        /// <summary>站點中心（公尺，X／Z）。</summary>
        public Vector2 Position { get; }

        /// <summary>true：交接點在站點的 -Z 側（上排）；false：在 +Z 側（下排）。</summary>
        public bool FacesSouth { get; }
    }

    /// <summary>
    /// 暫定的顯示用佈局（2026-09-27，使用者同意「每道工序先放一站」）。
    /// 站點數量與位置不是已確認的業務規則；P2 起站點應由後端資料提供，Unity 只負責畫出來。
    ///
    /// 環狀主走道的中心線必須與模擬器設定 Simulator:Route 相同（backend/src/OpenFleet.Simulator/appsettings.json）。
    /// 下排站點由左到右、上排由右到左，依製程順序沿走道排列。
    /// </summary>
    internal static class FactoryLayout
    {
        public static readonly Vector2 FloorSize = new Vector2(48f, 32f);

        /// <summary>走道中心線的四個角（與 Simulator:Route 相同）。</summary>
        public static readonly Vector2[] AisleLoop =
        {
            new Vector2(-18f, -6f),
            new Vector2(18f, -6f),
            new Vector2(18f, 6f),
            new Vector2(-18f, 6f),
        };

        public const float AisleWidth = 2.4f;

        /// <summary>站點中心到交接點的距離（交接點位於走道外緣旁）。</summary>
        public const float HandoffOffset = 3.4f;

        public static readonly StationSpec[] Stations =
        {
            new StationSpec("raw-store", StationKind.RawMaterialStore, "原料倉", "Raw Material", new Vector2(-16f, -11f), false),
            new StationSpec("rough", StationKind.RoughMachining, "粗加工", "Rough Machining", new Vector2(-8f, -11f), false),
            new StationSpec("fine", StationKind.FineMachining, "精加工", "Fine Machining", new Vector2(0f, -11f), false),
            new StationSpec("heat", StationKind.HeatTreatment, "淬火／回火", "Quench / Temper", new Vector2(8f, -11f), false),
            new StationSpec("plating", StationKind.Plating, "電鍍", "Plating", new Vector2(16f, -11f), false),
            new StationSpec("polish", StationKind.Polishing, "拋光", "Polishing", new Vector2(16f, 11f), true),
            new StationSpec("qc", StationKind.QualityInspection, "品管", "Inspection", new Vector2(8f, 11f), true),
            new StationSpec("packing", StationKind.Packing, "包裝", "Packing", new Vector2(0f, 11f), true),
            new StationSpec("shipping", StationKind.Shipping, "出貨", "Shipping", new Vector2(-8f, 11f), true),
            new StationSpec("tool-store", StationKind.ToolStore, "刀具庫", "Tool Store", new Vector2(-16f, 11f), true),
        };

        /// <summary>走道內側島區：充電維護區（位置、席位數為待定規則，這裡只畫 3 個示意車位）。</summary>
        public static readonly Vector2 ChargingArea = new Vector2(-8f, 0f);

        /// <summary>走道內側島區：料箱／托盤存放區（歸還位置待定，這裡只是示意）。</summary>
        public static readonly Vector2 CarrierArea = new Vector2(8f, 0f);
    }
}
