namespace OpenFleet.Simulator;

/// <summary>對應 "Simulator" 設定區段。車輛沿固定的封閉路線循環行駛，只用來產生位置變化，不代表派工或交通規則。</summary>
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
}

public sealed class RoutePoint
{
    public double X { get; set; }
    public double Z { get; set; }
}
