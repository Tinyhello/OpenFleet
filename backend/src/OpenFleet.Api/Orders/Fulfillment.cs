using OpenFleet.Contracts;

namespace OpenFleet.Api.Orders;

/// <summary>訂單出貨的共用規則：訂單服務（同意分批時檢查）和生產端（裝箱出貨）用同一套。</summary>
public static class Fulfillment
{
    /// <summary>完成：包裝完成（之後併進出貨箱或送成品倉的也算），或走完製程送到暫存點。</summary>
    public static bool IsFinished(LotStatus status, int? step) =>
        status is LotStatus.Packed or LotStatus.Boxed || (status == LotStatus.Staged && step is null);

    /// <summary>還在流動：等料、在原料倉、在車上、在各站排隊或加工。</summary>
    public static bool IsActive(LotStatus status, int? step) =>
        !IsFinished(status, step) && status is not (LotStatus.Scrapped or LotStatus.Shipped);

    /// <summary>
    /// 一行明細現在包好、可以出的完整組數（使用者 2026-10-22：分批以完整的一組為單位）：
    /// 整組取每種零件包好支數的最小值；單一零件就是包好的支數。不超過還沒出的數量 <paramref name="remaining"/>。
    /// </summary>
    public static int CompleteSets(IReadOnlyList<string> parts, Func<string, int> packed, int remaining) =>
        Math.Max(0, Math.Min(remaining, parts.Min(packed)));
}
