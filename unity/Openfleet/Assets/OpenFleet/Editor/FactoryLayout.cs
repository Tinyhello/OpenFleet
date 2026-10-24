using UnityEngine;

namespace OpenFleet.Unity.Editor
{
    internal enum StationKind
    {
        Cutting,
        RawMaterialStore,
        RoughMachining,
        FineMachining,
        HeatTreatment,
        Plating,
        Polishing,
        QualityInspection,
        Packing,
        Shipping,
        FinishedGoodsStore,
        ToolStore,

        /// <summary>只是場景的倉儲區，不是後端的站點。</summary>
        Storage,
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

        /// <summary>true：交接點在站點的 -Z 側（上排、島區）；false：在 +Z 側（下排）。</summary>
        public bool FacesSouth { get; }
    }

    /// <summary>
    /// 暫定的顯示用佈局（2026-09-27，使用者同意「每道工序先放一站」；2026-10-12 依使用者指定的相鄰關係重排）。
    /// 站點數量與位置不是已確認的業務規則；P2 起站點應由後端資料提供，Unity 只負責畫出來。
    ///
    /// 走道的車道位置必須與後端的路網相同（見 OuterLane／InnerLane）。
    /// 下排（靠南牆）由左到右依製程順序：切料、原料倉、粗加工、精加工、淬火、回火、電鍍；
    /// 切料站在原料倉左邊，廠房因此往西加寬 8 m（使用者 2026-10-22 選的），場地中心不在原點。
    /// 上排（靠北牆）由右到左：拋光、雜物倉庫、品管、包裝、成品倉、出貨。雜物倉庫只是場景，沒有後端站點（見 Storages）；
    /// 成品倉在 P5 變成真正的站點（多出來的零件送進去）。
    /// 西牆中間是工廠大門，進料與出貨共用（使用者 2026-10-12）。
    /// 刀具庫在走道內側的島區中央，交接點接在內圈車道下邊；位置必須和後端 DemoRoadNetwork 的 IslandHandoffZ 一致。
    /// </summary>
    internal static class FactoryLayout
    {
        /// <summary>地面（公尺，X／Z）。牆貼著上下兩排站點的後方；西端 -34、東端 42（西側多放切料站；P7 第 1b 步下排往東加長）。</summary>
        public static readonly Rect Floor = Rect.MinMaxRect(-34f, -15.2f, 42f, 15.2f);

        /// <summary>
        /// 環狀走道有內外兩條車道（都不限方向）。數值是各車道中心線圍成的矩形，
        /// 必須與後端路網的節點座標相同（backend/src/OpenFleet.Api/Traffic/RoadNetwork.cs 的 DemoRoadNetwork）。
        /// </summary>
        public static readonly Rect OuterLane = Rect.MinMaxRect(-30f, -7f, 38f, 7f);
        public static readonly Rect InnerLane = Rect.MinMaxRect(-28.2f, -5.2f, 36.2f, 5.2f);

        /// <summary>車道中心線到鋪面邊緣的距離。</summary>
        public const float LaneEdge = 1.2f;
        public const float InnerLaneEdge = 0.9f;

        /// <summary>站點中心到交接點的距離。交接點離車道 2.4 m，方框不會壓到走道的鋪面。</summary>
        public const float HandoffOffset = 3.4f;

        /// <summary>工廠大門：開在西牆中間，進料與出貨共用。寬度是 Claude 挑的。</summary>
        public const float GateWidth = 10f;
        public const float GateZ = 0f;

        public static readonly StationSpec[] Stations =
        {
            new StationSpec("cutting-1", StationKind.Cutting, "切料", "Cutting", new Vector2(-28f, -12.8f), false),
            new StationSpec("raw-store", StationKind.RawMaterialStore, "原料倉", "Raw Material", new Vector2(-20f, -12.8f), false),
            new StationSpec("rough-1", StationKind.RoughMachining, "粗加工 1 號機", "Rough #1", new Vector2(-12f, -12.8f), false),
            new StationSpec("rough-2", StationKind.RoughMachining, "粗加工 2 號機", "Rough #2", new Vector2(-4f, -12.8f), false),
            new StationSpec("fine-1", StationKind.FineMachining, "精加工 1 號機", "Fine #1", new Vector2(4f, -12.8f), false),
            new StationSpec("fine-2", StationKind.FineMachining, "精加工 2 號機", "Fine #2", new Vector2(12f, -12.8f), false),
            new StationSpec("heat-1", StationKind.HeatTreatment, "淬火", "Quench", new Vector2(20f, -12.8f), false),
            new StationSpec("temper-1", StationKind.HeatTreatment, "回火", "Temper", new Vector2(28f, -12.8f), false),
            new StationSpec("plating-1", StationKind.Plating, "電鍍", "Plating", new Vector2(36f, -12.8f), false),
            new StationSpec("polish-1", StationKind.Polishing, "拋光", "Polishing", new Vector2(20f, 12.8f), true),
            new StationSpec("qc-1", StationKind.QualityInspection, "品管", "Inspection", new Vector2(4f, 12.8f), true),
            new StationSpec("packing-1", StationKind.Packing, "包裝", "Packing", new Vector2(-4f, 12.8f), true),
            new StationSpec("finished-store", StationKind.FinishedGoodsStore, "成品倉", "Finished Goods", new Vector2(-12f, 12.8f), true),
            new StationSpec("shipping", StationKind.Shipping, "出貨", "Shipping", new Vector2(-20f, 12.8f), true),
            new StationSpec("tool-store", StationKind.ToolStore, "刀具庫", "Tool Store", new Vector2(0f, 0.2f), true),
        };

        /// <summary>
        /// 只有模型與標示的倉儲區（使用者 2026-10-12 要求加上）：沒有交接點、後端沒有對應的站點，AGV 不會去。
        /// 不放在 Factory/Stations 底下，執行時不會被當成站點。Id 是模型檔名。
        /// </summary>
        public static readonly StationSpec[] Storages =
        {
            new StationSpec("Station_MiscStore", StationKind.Storage, "雜物倉庫", "General Storage", new Vector2(12f, 12.8f), true),
        };

        /// <summary>
        /// 走道內側島區：充電維護區，一台車一格（示範車隊 4 台大型車加 1 台小型車，使用者 2026-10-22）。
        /// 車位的位置必須和後端 DemoRoadNetwork 的 ParkingColumns 相同。
        /// </summary>
        public static readonly Vector2 ChargingArea = new Vector2(-10.05f, 0f);
        public static readonly float[] ChargingBayOffsets = { -4.35f, -2.55f, -0.25f, 2.05f, 4.35f };

        /// <summary>走道內側島區：料箱／托盤存放區（歸還位置待定，這裡只是示意）。</summary>
        public static readonly Vector2 CarrierArea = new Vector2(8f, 0f);
    }
}
