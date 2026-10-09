using OpenFleet.Simulator;

namespace OpenFleet.Tests;

public class LoopRouteTests
{
    // 10 x 4 的矩形，整圈 28 m。
    private static readonly LoopRoute Rectangle = new(
    [
        new RoutePoint { X = 0, Z = 0 },
        new RoutePoint { X = 10, Z = 0 },
        new RoutePoint { X = 10, Z = 4 },
        new RoutePoint { X = 0, Z = 4 },
    ]);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(5, 5, 0)]
    [InlineData(12, 10, 2)]
    [InlineData(17, 7, 4)]
    [InlineData(26, 0, 2)] // 最後一段接回起點
    [InlineData(33, 5, 0)] // 超過一圈取餘數
    public void Point_at_distance_follows_the_closed_polyline(double distance, double x, double z)
    {
        Assert.Equal(28, Rectangle.Length, 6);

        var point = Rectangle.PointAt(distance);

        Assert.Equal(x, point.X, 6);
        Assert.Equal(z, point.Z, 6);
    }

    [Theory]
    [InlineData(5, -3, 5)]    // 下邊外側
    [InlineData(12, 2, 12)]   // 右邊外側
    [InlineData(4, 3, 20)]    // 內側偏上：最近的是上邊
    [InlineData(-2, -2, 0)]   // 角落外：對到轉角
    public void Project_finds_the_closest_position_on_the_loop(double x, double z, double expected)
    {
        Assert.Equal(expected, Rectangle.Project(x, z), 6);
    }

    [Fact]
    public void Path_joins_the_loop_follows_the_shorter_direction_and_leaves_at_the_target()
    {
        // 下邊外側 (2, -1) 到上邊外側 (8, 5)：順向 16 m，逆向 12 m，所以往西繞。
        var shorter = Rectangle.PlanPath(2, -1, 3, 5);
        Assert.Equal(new[] { (2d, 0d), (0d, 0d), (0d, 4d), (3d, 4d), (3d, 5d) }, shorter);

        // 起點已經在走道上時，不重複加入起點。
        var onLoop = Rectangle.PlanPath(2, 0, 6, 0);
        Assert.Equal(new[] { (6d, 0d) }, onLoop);
    }
}
