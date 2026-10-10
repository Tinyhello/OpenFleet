using OpenFleet.Contracts;

namespace OpenFleet.Api.Fleet;

/// <summary>
/// 對應 "Fleet" 設定區段。以下預設值是 P1 示範用的暫定值，不是已確認的業務規則（見 document/任務規劃.md 決策紀錄）。
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    /// <summary>超過此時間沒收到任何遙測或事件，車輛標記為 Offline。</summary>
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan LivenessCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>指令的有效期限；設備超時才收到就應回報 Rejected。</summary>
    public TimeSpan CommandTimeToLive { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 車輛 ID → 車型。啟動時寫入 Vehicles 資料表（已存在的車輛會更新車型）。
    /// 不在清單中的車輛仍會接受回報，但車型為 <see cref="VehicleType.Unknown"/>，也不會被派工。
    /// </summary>
    public Dictionary<string, VehicleType> VehicleTypes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>參與自動派工的車輛。示範設定是兩台大型車（物料）與一台小型車（刀具）。</summary>
    public List<string> DispatchVehicleIds { get; set; } = [];

    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>同一個任務被設備拒絕這麼多次後標記為 Failed，避免無限重派。</summary>
    public int MaxDispatchAttempts { get; set; } = 3;

    /// <summary>
    /// 車輛 ID → 待命位置（充電維護區的車位，會對應到最近的路網節點）。沒有任務的車輛會被交通控制引導回去。
    /// 席位數與充電規則尚未定案，這裡只是示範用的固定車位。
    /// </summary>
    public Dictionary<string, ParkingSlot> Parking { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ParkingSlot
{
    public double X { get; set; }
    public double Z { get; set; }
}
