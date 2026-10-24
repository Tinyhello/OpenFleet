namespace OpenFleet.Api.Fleet;

/// <summary>
/// 對應 "Fleet" 設定區段。以下預設值是 P1 示範用的暫定值，不是已確認的業務規則。
/// 車輛與它們的車位（車型、待命位置）不在這裡：它們來自使用中的佈局（P7 第 2 步）。
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    /// <summary>超過此時間沒收到任何遙測或事件，車輛標記為 Offline。</summary>
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan LivenessCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>指令的有效期限；設備超時才收到就應回報 Rejected。</summary>
    public TimeSpan CommandTimeToLive { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan DispatchInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 對帳的寬限時間：在線的車輛連續這麼久都回報「沒有在執行這道指令」，才認定設備弄丟了這趟搬運（例如設備重啟）。
    /// 留一點時間是因為設備做完之後，「完成」的回報可能比遙測晚到。
    /// </summary>
    public TimeSpan LostCommandGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>同一個任務被設備拒絕或執行失敗這麼多次後標記為 Failed，避免無限重派。</summary>
    public int MaxDispatchAttempts { get; set; } = 3;
}
