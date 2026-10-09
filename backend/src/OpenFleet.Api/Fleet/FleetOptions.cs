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
    /// 車輛 ID → 車型。P1 沒有資料庫，先由設定提供；P2 改為資料表。
    /// 不在清單中的車輛仍會接受回報，但車型為 <see cref="VehicleType.Unknown"/>。
    /// </summary>
    public Dictionary<string, VehicleType> VehicleTypes { get; set; } = new(StringComparer.Ordinal);
}
