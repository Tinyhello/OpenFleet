namespace OpenFleet.Contracts
{
    public enum VehicleChannel
    {
        Commands,
        Events,
        Telemetry,

        /// <summary>後端 → 設備：放行（路徑與可以開到哪裡）。</summary>
        Clearance,
    }

    /// <summary>
    /// MQTT Topic 命名集中在這裡，後端、模擬器與 Unity 共用，避免各自拼字串。
    /// 格式：openfleet/v1/{factoryId}/vehicles/{vehicleId}/{commands|events|telemetry|clearance}
    ///       openfleet/v1/{factoryId}/state/vehicles/{vehicleId}
    /// </summary>
    public static class Topics
    {
        public const string Root = "openfleet/v1";

        public static string Vehicle(string factoryId, string vehicleId, VehicleChannel channel)
        {
            return $"{Root}/{factoryId}/vehicles/{vehicleId}/{ChannelName(channel)}";
        }

        /// <summary>訂閱所有車輛某一類訊息的萬用字元 filter。</summary>
        public static string AllVehicles(string factoryId, VehicleChannel channel)
        {
            return Vehicle(factoryId, "+", channel);
        }

        /// <summary>使用中的佈局版本（retained）：模擬器據此增減車輛，Unity 據此重建場景。</summary>
        public static string Layout(string factoryId)
        {
            return $"{Root}/{factoryId}/layout";
        }

        public static string VehicleState(string factoryId, string vehicleId)
        {
            return $"{Root}/{factoryId}/state/vehicles/{vehicleId}";
        }

        public static string AllVehicleStates(string factoryId)
        {
            return VehicleState(factoryId, "+");
        }

        /// <summary>從車輛 Topic 取出 vehicleId；格式不符時回傳 false。</summary>
        public static bool TryParseVehicle(string topic, out string factoryId, out string vehicleId, out VehicleChannel channel)
        {
            factoryId = string.Empty;
            vehicleId = string.Empty;
            channel = default;

            // openfleet / v1 / {factory} / vehicles / {vehicle} / {channel}
            var parts = topic.Split('/');
            if (parts.Length != 6 || parts[0] != "openfleet" || parts[1] != "v1" || parts[3] != "vehicles")
            {
                return false;
            }

            if (parts[2].Length == 0 || parts[4].Length == 0 || !TryParseChannel(parts[5], out channel))
            {
                return false;
            }

            factoryId = parts[2];
            vehicleId = parts[4];
            return true;
        }

        private static string ChannelName(VehicleChannel channel)
        {
            switch (channel)
            {
                case VehicleChannel.Commands: return "commands";
                case VehicleChannel.Events: return "events";
                case VehicleChannel.Telemetry: return "telemetry";
                case VehicleChannel.Clearance: return "clearance";
                default: throw new System.ArgumentOutOfRangeException(nameof(channel), channel, null);
            }
        }

        private static bool TryParseChannel(string name, out VehicleChannel channel)
        {
            switch (name)
            {
                case "commands": channel = VehicleChannel.Commands; return true;
                case "events": channel = VehicleChannel.Events; return true;
                case "telemetry": channel = VehicleChannel.Telemetry; return true;
                case "clearance": channel = VehicleChannel.Clearance; return true;
                default: channel = default; return false;
            }
        }
    }
}
