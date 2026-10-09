namespace OpenFleet.Simulator;

/// <summary>
/// 對應 "Simulator" 設定區段。車輛啟動時平均分布在環狀走道上並保持靜止，收到指令才沿走道移動。
/// 沒有任何交通規則：車輛之間不會避讓。
/// </summary>
public sealed class SimulatorSettings
{
    public const string SectionName = "Simulator";

    public List<string> VehicleIds { get; set; } = [];

    /// <summary>遙測發送間隔；預設 200ms（每秒 5 次，文件中的初始候選值）。</summary>
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 封閉路線的轉折點（公尺，X／Z 平面），最後一點會接回第一點。
    /// 預設是 Unity 暫定工廠佈局的環狀主走道中心線。
    /// </summary>
    public List<RoutePoint> Route { get; set; } = [];

    /// <summary>公尺／秒。</summary>
    public double Speed { get; set; } = 1;

    /// <summary>
    /// 在交接點取料或卸料所需的時間。代表「站點設備確認取放」的簡化模擬：時間到就回報，沒有真正的交接設備。
    /// 示範值，尚未定案。
    /// </summary>
    public TimeSpan HandoverDuration { get; set; } = TimeSpan.FromSeconds(2);
}

public sealed class RoutePoint
{
    public double X { get; set; }
    public double Z { get; set; }
}
