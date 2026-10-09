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
}
