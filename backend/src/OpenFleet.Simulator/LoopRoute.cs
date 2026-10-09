namespace OpenFleet.Simulator;

/// <summary>封閉折線路線：給定沿線距離，求出所在座標。</summary>
public sealed class LoopRoute
{
    private readonly RoutePoint[] _points;
    private readonly double[] _segmentLengths;

    public LoopRoute(IReadOnlyList<RoutePoint> points)
    {
        _points = [.. points];
        _segmentLengths = new double[_points.Length];
        for (var i = 0; i < _points.Length; i++)
        {
            _segmentLengths[i] = Distance(_points[i], _points[(i + 1) % _points.Length]);
        }

        Length = _segmentLengths.Sum();
    }

    /// <summary>整圈長度（公尺）。</summary>
    public double Length { get; }

    public static double MeasureLength(IReadOnlyList<RoutePoint> points) => new LoopRoute(points).Length;

    /// <summary><paramref name="distance"/> 超過一圈時自動取餘數。</summary>
    public (double X, double Z) PointAt(double distance)
    {
        var remaining = ((distance % Length) + Length) % Length;
        for (var i = 0; i < _points.Length; i++)
        {
            var segment = _segmentLengths[i];
            if (remaining <= segment && segment > 0)
            {
                var from = _points[i];
                var to = _points[(i + 1) % _points.Length];
                var t = remaining / segment;
                return (from.X + (to.X - from.X) * t, from.Z + (to.Z - from.Z) * t);
            }

            remaining -= segment;
        }

        return (_points[0].X, _points[0].Z);
    }

    private static double Distance(RoutePoint a, RoutePoint b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));
}
