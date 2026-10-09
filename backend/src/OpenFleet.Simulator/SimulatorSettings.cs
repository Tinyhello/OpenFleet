namespace OpenFleet.Simulator;

/// <summary>
/// 對應 "Simulator" 設定區段。車輛平常靜止；路徑與可以開到哪裡都由後端的放行決定，模擬器不知道路網。
/// </summary>
public sealed class SimulatorSettings
{
    public const string SectionName = "Simulator";

    public List<string> VehicleIds { get; set; } = [];

    /// <summary>遙測發送間隔；預設 200ms（每秒 5 次，文件中的初始候選值）。</summary>
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 車輛啟動時的位置（公尺，X／Z 平面）。示範設定是各自的充電車位；沒有列出的車排在原點旁邊。
    /// </summary>
    public Dictionary<string, RoutePoint> StartPositions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>空車車速（公尺／秒）。</summary>
    public double Speed { get; set; } = 2;

    /// <summary>
    /// 滿載（<see cref="LoadCapacity"/> 件）時的車速。載貨時依件數在空車與滿載車速之間線性遞減。
    /// 使用者 2026-10-09 決定：空車是原本車速的 2 倍，滿載維持原本的 1 m/s。不同內容物的差異等有第二種零件時再加。
    /// </summary>
    public double FullLoadSpeed { get; set; } = 1;

    /// <summary>車速計算用的滿載件數。不是派工的載重上限（那個規則還沒定）。</summary>
    public int LoadCapacity { get; set; } = 30;

    /// <summary>
    /// 指令沒有指定交接時間時，在交接點取料或卸料所需的時間。代表「站點設備確認取放」的簡化模擬：
    /// 時間到就回報，沒有真正的交接設備。後端的搬運指令會依件數帶入交接時間，這個值只是後備。
    /// </summary>
    public TimeSpan HandoverDuration { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class RoutePoint
{
    public double X { get; set; }
    public double Z { get; set; }
}
