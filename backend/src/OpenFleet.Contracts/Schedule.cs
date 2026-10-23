using System;
using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    // P6 排程預估的 HTTP 契約（GET /api/schedule）。

    /// <summary>某張訂單（或某一框）的預估為什麼停住、算不出完成時間。</summary>
    public enum ForecastBlocker
    {
        None,

        /// <summary>圓棒不夠，還在等料（要等採購到貨或盤點補進）。</summary>
        WaitingForMaterial,

        /// <summary>整爐工序湊不到開爐的下限，也沒有其他料會再進來（要等別的訂單一起開爐）。</summary>
        WaitingForFurnaceFill,
    }

    /// <summary>
    /// 排程預估：照現在廠內的狀態與排程規則（急單、交期、換線、同批次）往後推算，
    /// 每台機台接下來做哪幾框、每座爐接下來開哪幾爐、每張訂單預估什麼時候做完與出貨。
    /// <see cref="Normal"/> 一律用正常速度（不加速）算，交期比較只看它（使用者 2026-10-23）；
    /// 測試模式開著時另外附上用目前加速倍率算的 <see cref="Accelerated"/>，示範時看大約幾秒做完。
    /// </summary>
    public sealed class ScheduleDto
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public bool TestMode { get; set; }
        public ScheduleForecastDto Normal { get; set; } = new ScheduleForecastDto();

        /// <summary>測試模式關閉時是 null。</summary>
        public ScheduleForecastDto? Accelerated { get; set; }
    }

    public sealed class ScheduleForecastDto
    {
        /// <summary>逐件工序的機台（車床、拋光、品管、包裝、切料），依站與編號排序。</summary>
        public List<MachineScheduleDto> Machines { get; set; } = new List<MachineScheduleDto>();

        /// <summary>整爐工序（淬火、回火、電鍍），一站一座爐。</summary>
        public List<FurnaceScheduleDto> Furnaces { get; set; } = new List<FurnaceScheduleDto>();

        /// <summary>還沒出貨的客戶訂單。</summary>
        public List<OrderForecastDto> Orders { get; set; } = new List<OrderForecastDto>();
    }

    public sealed class MachineScheduleDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string StationCode { get; set; } = string.Empty;

        /// <summary>依開始時間排序；第一筆可能是正在做的那一框。</summary>
        public List<ScheduledBinDto> Bins { get; set; } = new List<ScheduledBinDto>();
    }

    /// <summary>某台機台預估會做的一框。</summary>
    public sealed class ScheduledBinDto
    {
        public string LotNumber { get; set; } = string.Empty;

        /// <summary>批次編號（有工單的框是主工單號碼）。</summary>
        public string BatchNumber { get; set; } = string.Empty;

        /// <summary>所屬的客戶訂單；沒有訂單的框是 null。</summary>
        public string? SalesOrderNumber { get; set; }
        public bool IsRush { get; set; }
        public string PartCode { get; set; } = string.Empty;
        public int RodDiameterMm { get; set; }
        public int Quantity { get; set; }

        /// <summary>已經在做（開始時間是實際開始的時間，在現在之前）。</summary>
        public bool Running { get; set; }

        /// <summary>開始前要先換線。</summary>
        public bool Changeover { get; set; }
        public DateTimeOffset StartAt { get; set; }
        public DateTimeOffset EndAt { get; set; }
    }

    public sealed class FurnaceScheduleDto
    {
        public string StationCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public List<ScheduledBatchRunDto> Runs { get; set; } = new List<ScheduledBatchRunDto>();

        /// <summary>預估最後留在爐前、湊不到一爐的框。</summary>
        public List<string> StrandedLotNumbers { get; set; } = new List<string>();
        public int StrandedPieces { get; set; }
    }

    public sealed class ScheduledBatchRunDto
    {
        /// <summary>已經在跑（開始時間是實際開爐的時間）。</summary>
        public bool Running { get; set; }
        public int Pieces { get; set; }
        public List<string> LotNumbers { get; set; } = new List<string>();
        public DateTimeOffset StartAt { get; set; }
        public DateTimeOffset EndAt { get; set; }
    }

    public sealed class OrderForecastDto
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public DateTime? DueDate { get; set; }
        public bool IsRush { get; set; }

        /// <summary>每一框都已經包裝完（等裝箱或貨車）。</summary>
        public bool Finished { get; set; }

        /// <summary>預估最後一框包裝完的時間；已經包完或算不出來（<see cref="Blocker"/>）時是 null。</summary>
        public DateTimeOffset? FinishAt { get; set; }

        /// <summary>預估貨車載走的時間；算不出來時是 null。</summary>
        public DateTimeOffset? ShipAt { get; set; }

        /// <summary>
        /// 趕不趕得上交期：預估出貨的時間在交期那一天結束之前（UTC）。沒有交期、或算不出來時是 null。
        /// 只有正常速度的預估（<see cref="ScheduleDto.Normal"/>）有這一欄。
        /// </summary>
        public bool? OnTime { get; set; }
        public ForecastBlocker Blocker { get; set; }

        /// <summary>預估停住的框（<see cref="Blocker"/> 不是 None 時）。</summary>
        public List<string> BlockedLotNumbers { get; set; } = new List<string>();
    }
}
