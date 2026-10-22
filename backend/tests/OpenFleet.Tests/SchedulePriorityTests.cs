using OpenFleet.Api.Production;
using Free = OpenFleet.Api.Production.MachineAssignment.FreeMachine;
using Setup = OpenFleet.Api.Production.MachineAssignment.Setup;

namespace OpenFleet.Tests;

/// <summary>P6 第 2 步：急單、交期優先的排序規則（不需要資料庫）。</summary>
public sealed class SchedulePriorityTests
{
    private static readonly DateOnly Soon = new(2026, 10, 14);
    private static readonly DateOnly Later = new(2026, 10, 19);

    [Fact]
    public void Rush_comes_first_then_earlier_due_dates_and_orders_without_a_due_date_last()
    {
        var shuffled = new[]
        {
            new SchedulePriority(false, null),
            new SchedulePriority(false, Later),
            new SchedulePriority(true, null),
            new SchedulePriority(false, Soon),
            new SchedulePriority(true, Later),
            SchedulePriority.None,
        };

        Assert.Equal(
            new[]
            {
                new SchedulePriority(true, Later), new SchedulePriority(true, null), new SchedulePriority(false, Soon),
                new SchedulePriority(false, Later), SchedulePriority.None, SchedulePriority.None,
            },
            shuffled.OrderBy(p => p));
    }

    [Fact]
    public void Priority_beats_changeover_and_equal_priority_keeps_the_changeover_rule()
    {
        var last = new Setup("B-A", "upper-punch", 30);
        Free[] machine = [new("rough-1", 1, last)];
        // 先到的是急單的 24 mm（要換線），後到的是一般訂單的 30 mm（不用換線）：急單先做。
        var rush = new Setup("B-R", "upper-punch", 24);
        var normal = new Setup("B-N", "upper-punch", 30);
        var priorities = new Dictionary<Setup, SchedulePriority> { [rush] = new(true, null), [normal] = SchedulePriority.None };

        var assigned = MachineAssignment.Assign(machine, [normal, rush], b => b, isLathe: true, b => priorities[b]);
        Assert.Equal("B-R", Assert.Single(assigned).Item.BatchNumber);

        // 交期一樣時，照原本的規則先做不用換線的，不管誰先到。
        var due = new SchedulePriority(false, Soon);
        assigned = MachineAssignment.Assign(machine, [rush, normal], b => b, isLathe: true, _ => due);
        Assert.Equal("B-N", Assert.Single(assigned).Item.BatchNumber);
    }
}
