using System;

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

        /// <summary>
        /// 空車移動到 <see cref="VehicleCommand.Destination"/>（例如回充電維護區待命）。
        /// 不是派工：沒有 AssignmentId，收到新的 Transport 時可以被取代。
        /// </summary>
        MoveTo,
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

        /// <summary>Transport 的卸料交接點，或 MoveTo 的目的地。</summary>
        public Position3? Destination { get; set; }
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
