namespace OpenFleet.Contracts
{
    /// <summary>
    /// 測試用的模擬設定：HTTP（GET／PUT /api/simulation）與 MQTT（後端 → 模擬器，retained）共用。
    /// </summary>
    public sealed class SimulationSettingsDto
    {
        /// <summary>模擬時間倍率；1 是正常速度。行駛、上下料、加工、換刀都依這個倍率加快。</summary>
        public double TimeScale { get; set; } = 1;
    }
}
