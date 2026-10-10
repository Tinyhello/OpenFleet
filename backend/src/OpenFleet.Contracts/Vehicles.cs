using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    /// <summary>公尺為單位的座標；Unity 端自行轉換座標系。</summary>
    public sealed class Position3
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    public enum VehicleCommandType
    {
        /// <summary>P1 通訊驗證用：設備收到後回報 Accepted 與 Completed，不做實際動作。</summary>
        Ping,

        /// <summary>
        /// 搬運：前往 <see cref="VehicleCommand.Pickup"/> 取料（回報 Loaded），再到
        /// <see cref="VehicleCommand.Destination"/> 卸料（回報 Completed）。必須帶 AssignmentId。
        /// </summary>
        Transport,

    }

    /// <summary>後端 → 設備／模擬器。</summary>
    public sealed class VehicleCommand
    {
        /// <summary>指令唯一值；設備以此判斷是否為重送的同一道指令。</summary>
        public Guid CommandId { get; set; }

        /// <summary>派工紀錄；P1 的 Ping 沒有派工，因此可為 null。</summary>
        public Guid? AssignmentId { get; set; }

        public VehicleCommandType CommandType { get; set; }

        /// <summary>設備收到時若已超過此時間，必須回報 Rejected 而不是執行。</summary>
        public DateTimeOffset ExpiresAt { get; set; }

        /// <summary>Transport 的取料交接點。座標由後端提供，設備不需要認得站點。</summary>
        public Position3? Pickup { get; set; }

        /// <summary>Transport 的卸料交接點。</summary>
        public Position3? Destination { get; set; }

        /// <summary>Transport 搬運的件數；設備用來決定載貨時的車速，並回報在遙測裡。</summary>
        public int LoadQuantity { get; set; }

        /// <summary>Transport 搬運的內容（零件代碼）。</summary>
        public string? LoadCode { get; set; }

        /// <summary>Transport 在取料點、卸料點各需要的交接時間（秒）；省略時使用設備自己的預設值。</summary>
        public double? HandoverSeconds { get; set; }
    }

    /// <summary>
    /// 後端 → 設備／模擬器：放行。告訴車輛要走的路徑，以及目前可以開到哪裡。
    /// 車輛只能沿 <see cref="Path"/> 開到第 <see cref="ReleasedCount"/> 個點然後停下，等下一次放行。
    /// 已放行的部分不會被更改；同一個 <see cref="PlanId"/> 之內，<see cref="Revision"/> 較大的內容取代舊的。
    /// </summary>
    public sealed class VehicleClearance
    {
        /// <summary>一段路的識別。走完一段、開始下一段時會換新的值，點的編號從頭算起。</summary>
        public Guid PlanId { get; set; }

        /// <summary>同一段路之內遞增；設備用來忽略重送或亂序的舊訊息。</summary>
        public long Revision { get; set; }

        /// <summary>依序要經過的點。第一個是出發時所在的點。</summary>
        public List<Position3> Path { get; set; } = new List<Position3>();

        /// <summary>已放行的點數：可以開到 Path[ReleasedCount - 1]。</summary>
        public int ReleasedCount { get; set; }
    }

    public enum VehicleEventType
    {
        Accepted,
        Rejected,
        Completed,
        Failed,

        /// <summary>Transport：已在取料點完成交接，物料在車上。抵達不等於已取料，所以另外回報。</summary>
        Loaded,
    }

    /// <summary>設備／模擬器 → 後端。業務結果回報，與位置遙測分開。</summary>
    public sealed class VehicleEvent
    {
        public Guid CommandId { get; set; }
        public Guid? AssignmentId { get; set; }
        public VehicleEventType EventType { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>車輛此刻在做什麼。只用於顯示；業務狀態仍以事件（Loaded、Completed）為準。</summary>
    public enum VehicleActivity
    {
        Idle,
        Moving,

        /// <summary>在取料點交接中（上料）。</summary>
        Loading,

        /// <summary>在卸料點交接中（下料）。</summary>
        Unloading,
    }

    /// <summary>設備／模擬器 → 後端。高頻位置回報，可遺失。</summary>
    public sealed class VehicleTelemetry
    {
        /// <summary>設備每次啟動換一個新值；Sequence 只在同一個 Session 內比較。</summary>
        public Guid SessionId { get; set; }

        /// <summary>同一 Session 內遞增，用來丟棄亂序或重複的舊資料。</summary>
        public long Sequence { get; set; }

        public Position3 Position { get; set; } = new Position3();

        /// <summary>公尺／秒。</summary>
        public double Speed { get; set; }

        public VehicleActivity Activity { get; set; }

        /// <summary>上料或下料的進度，0～1；其他時候為 0。</summary>
        public double ActivityProgress { get; set; }

        /// <summary>車上的件數；0 表示空車。取料交接完成後才算在車上，卸料交接完成後歸零。</summary>
        public int LoadQuantity { get; set; }

        /// <summary>車上的內容（零件代碼）；空車時為 null。</summary>
        public string? LoadCode { get; set; }

        /// <summary>
        /// 接下來要依序經過的點（後端放行的路徑中還沒走到的部分），最後一點是這一段的目的地；沒有時是空的。
        /// </summary>
        public List<Position3> Route { get; set; } = new List<Position3>();

        /// <summary><see cref="Route"/> 的前幾個點已經放行（可以開過去）；其餘的還在等預約。</summary>
        public int RouteReleasedCount { get; set; }

        /// <summary>目前依循的放行（<see cref="VehicleClearance.PlanId"/>）；還沒收到過放行時為 null。</summary>
        public Guid? RoutePlanId { get; set; }

        /// <summary>目前套用的放行版本；後端用來判斷車輛是否已收到最新的放行。</summary>
        public long RouteRevision { get; set; }

        /// <summary>這一段路已經到達的點數；後端據此釋放車輛已經離開的路段。</summary>
        public int RouteReachedCount { get; set; }

        /// <summary>
        /// 設備正在執行的搬運指令；沒有時為 null。後端用來對帳：
        /// 後端認為還在進行的派工，設備卻說沒有這道指令（例如設備重啟），那趟搬運就要進恢復流程。
        /// </summary>
        public Guid? ActiveCommandId { get; set; }
    }

    /// <summary>車型。由後端的車輛設定決定；設定裡沒有的車輛為 Unknown。</summary>
    public enum VehicleType
    {
        Unknown,

        /// <summary>大型車：車床相關物料（單件、料箱、托盤）。</summary>
        Large,

        /// <summary>小型車：只配送刀具，不負責換刀。</summary>
        Small,
    }

    public enum VehicleConnectivity
    {
        Online,
        Offline,
    }

    /// <summary>後端 → Unity／HTTP 快照。經後端確認後的車輛狀態。</summary>
    public sealed class VehicleState
    {
        public string VehicleId { get; set; } = string.Empty;
        public VehicleType VehicleType { get; set; }
        public VehicleConnectivity Connectivity { get; set; }
        public Position3 Position { get; set; } = new Position3();
        public double Speed { get; set; }
        public VehicleActivity Activity { get; set; }
        public double ActivityProgress { get; set; }
        public int LoadQuantity { get; set; }
        public string? LoadCode { get; set; }

        /// <summary>接下來要依序經過的點，最後一點是目的地（見 <see cref="VehicleTelemetry.Route"/>）。</summary>
        public List<Position3> Route { get; set; } = new List<Position3>();

        /// <summary><see cref="Route"/> 的前幾個點已經放行；顯示端可以用不同的樣式畫出「已放行」與「等待中」。</summary>
        public int RouteReleasedCount { get; set; }
        public Guid? RoutePlanId { get; set; }
        public long RouteRevision { get; set; }
        public int RouteReachedCount { get; set; }

        /// <summary>設備回報正在執行的搬運指令（見 <see cref="VehicleTelemetry.ActiveCommandId"/>）。</summary>
        public Guid? ActiveCommandId { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public Guid TelemetrySessionId { get; set; }
        public long TelemetrySequence { get; set; }
        public Guid? LastCommandId { get; set; }
        public VehicleEventType? LastCommandEvent { get; set; }

        /// <summary>後端每次啟動換一個新值。Version 只在同一個 Epoch 內比較。</summary>
        public Guid StateEpoch { get; set; }

        /// <summary>後端每次改變此車狀態就加 1；Unity 可用來丟棄比快照更舊的更新（同一個 StateEpoch 內）。</summary>
        public long Version { get; set; }
    }
}
