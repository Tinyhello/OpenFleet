using OpenFleet.Api.Production;
using Setup = OpenFleet.Api.Production.MachineAssignment.Setup;
using Free = OpenFleet.Api.Production.MachineAssignment.FreeMachine;

namespace OpenFleet.Tests;

/// <summary>P6：空下來的機台接哪一框（純規則，不需要資料庫）。</summary>
public sealed class MachineAssignmentTests
{
    private static readonly Setup Upper30 = new("B-A", "upper-punch", 30);
    private static readonly Setup Upper24 = new("B-B", "upper-punch", 24);

    [Fact]
    public void Each_lathe_gets_the_bin_that_needs_no_changeover_on_it()
    {
        // 1 號機上一框是 30 mm、2 號機是 24 mm；排隊的是 24 mm（先到）與 30 mm：交叉配，兩台都不用換線。
        var waiting = new[] { new Setup("B-C", "upper-punch", 24), new Setup("B-D", "upper-punch", 30) };

        var assigned = MachineAssignment.Assign([new Free("rough-1", 1, Upper30), new Free("rough-2", 2, Upper24)], waiting, b => b, isLathe: true);

        Assert.Equal(new[] { ("rough-2", "B-C"), ("rough-1", "B-D") }, assigned.Select(a => (a.Machine.Code, a.Item.BatchNumber)));
    }

    [Fact]
    public void New_lathe_needs_no_changeover_and_takes_the_first_bin_in_line()
    {
        // 2 號機還沒做過（不用換線）：先到的 24 mm 給它，30 mm 給上一框也是 30 mm 的 1 號機。
        var waiting = new[] { new Setup("B-C", "upper-punch", 24), new Setup("B-D", "upper-punch", 30) };

        var assigned = MachineAssignment.Assign([new Free("rough-1", 1, Upper30), new Free("rough-2", 2, null)], waiting, b => b, isLathe: true);

        Assert.Equal(new[] { ("rough-2", "B-C"), ("rough-1", "B-D") }, assigned.Select(a => (a.Machine.Code, a.Item.BatchNumber)));
        Assert.False(MachineAssignment.NeedsChangeover(null, "die", 24));
        Assert.True(MachineAssignment.NeedsChangeover(Upper24, "die", 24));
        Assert.True(MachineAssignment.NeedsChangeover(Upper30, "upper-punch", 24));
    }

    [Fact]
    public void More_machines_than_bins_or_more_bins_than_machines()
    {
        // 只有一框：給不用換線的那一台（2 號機），1 號機繼續空著。
        var one = MachineAssignment.Assign(
            [new Free("rough-1", 1, Upper30), new Free("rough-2", 2, Upper24)], [new Setup("B-C", "upper-punch", 24)], b => b, isLathe: true);
        Assert.Equal("rough-2", Assert.Single(one).Machine.Code);

        // 一台機台、三框：先找同批次而且不用換線的，不是先到的。
        var three = MachineAssignment.Assign(
            [new Free("rough-1", 1, Upper30)],
            [new Setup("B-X", "upper-punch", 24), new Setup("B-Y", "upper-punch", 30), new Setup("B-A", "upper-punch", 30)],
            b => b, isLathe: true);
        Assert.Equal("B-A", Assert.Single(three).Item.BatchNumber);
    }

    [Fact]
    public void Other_stations_keep_bins_of_the_same_batch_together_before_anything_else()
    {
        // 拋光不換線：上一框是 B-A，同批次的另一框先做，不是先到的 B-B。
        var assigned = MachineAssignment.Assign(
            [new Free("polish-1", 1, Upper30)],
            [new Setup("B-B", "upper-punch", 30), new Setup("B-A", "die", 24)],
            b => b, isLathe: false);

        Assert.Equal("B-A", Assert.Single(assigned).Item.BatchNumber);
    }
}
