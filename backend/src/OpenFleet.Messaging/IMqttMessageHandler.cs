using MQTTnet.Protocol;

namespace OpenFleet.Messaging;

/// <summary>
/// 一個 Topic filter 的處理者。以 DI 註冊多個實作，由 <see cref="MqttConnection"/> 在每次連線後訂閱。
/// 發送回覆用的 publisher 以參數傳入，避免處理者與連線互相依賴（建構子循環相依）。
/// </summary>
public interface IMqttMessageHandler
{
    string TopicFilter { get; }

    MqttQualityOfServiceLevel QualityOfService { get; }

    Task HandleAsync(MqttInboundMessage message, IMqttPublisher publisher, CancellationToken cancellationToken);
}

public sealed record MqttInboundMessage(string Topic, ReadOnlyMemory<byte> Payload);

public interface IMqttPublisher
{
    bool IsConnected { get; }

    MqttSettings Settings { get; }

    /// <summary>未連線時丟出 <see cref="MqttNotConnectedException"/>；呼叫端決定要重試、丟棄或回報錯誤。</summary>
    Task PublishAsync<T>(string topic, T message, MqttQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken);
}

public sealed class MqttNotConnectedException() : InvalidOperationException("MQTT client is not connected.");
