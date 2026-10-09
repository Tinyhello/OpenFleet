using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OpenFleet.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// 註冊唯一的 <see cref="MqttConnection"/>：同一個實例同時作為 HostedService（負責連線）與 IMqttPublisher（供其他服務發送）。
    /// </summary>
    public static IServiceCollection AddOpenFleetMqtt(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MqttSettings>()
            .Bind(configuration.GetSection(MqttSettings.SectionName))
            .Validate(s => !string.IsNullOrWhiteSpace(s.ProducerId), "Mqtt:ProducerId is required.")
            .Validate(s => s.Port is > 0 and < 65536, "Mqtt:Port is out of range.")
            .ValidateOnStart();

        services.AddSingleton<MqttConnection>();
        services.AddSingleton<IMqttPublisher>(sp => sp.GetRequiredService<MqttConnection>());
        services.AddHostedService(sp => sp.GetRequiredService<MqttConnection>());
        return services;
    }

    public static IServiceCollection AddMqttHandler<THandler>(this IServiceCollection services)
        where THandler : class, IMqttMessageHandler
    {
        services.AddSingleton<IMqttMessageHandler, THandler>();
        return services;
    }
}
