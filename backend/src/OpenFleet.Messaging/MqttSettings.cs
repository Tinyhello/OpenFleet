namespace OpenFleet.Messaging;

/// <summary>對應 appsettings.json 的 "Mqtt" 區段（Options pattern）。</summary>
public sealed class MqttSettings
{
    public const string SectionName = "Mqtt";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;

    /// <summary>Broker 上的連線識別；同一 ClientId 同時只能有一條連線。</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>寫入訊息外層 ProducerId，供接收端去重與追蹤。</summary>
    public string ProducerId { get; set; } = string.Empty;

    public string FactoryId { get; set; } = "demo";

    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>斷線後每隔多久嘗試重連。</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);
}
