namespace OpenFleet.Simulator;

/// <summary>封閉折線路線（環狀主走道）：沿線距離與座標的換算，以及沿走道的路徑規劃。</summary>
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

    /// <summary>把任意點投影到路線上最近的位置，回傳該位置的沿線距離。</summary>
    public double Project(double x, double z)
    {
        var best = 0.0;
        var bestSquared = double.MaxValue;
        var travelled = 0.0;

        for (var i = 0; i < _points.Length; i++)
        {
            var from = _points[i];
            var to = _points[(i + 1) % _points.Length];
            var segment = _segmentLengths[i];
            if (segment > 0)
            {
                var t = Math.Clamp(((x - from.X) * (to.X - from.X) + (z - from.Z) * (to.Z - from.Z)) / (segment * segment), 0, 1);
                var dx = from.X + (to.X - from.X) * t - x;
                var dz = from.Z + (to.Z - from.Z) * t - z;
                var squared = dx * dx + dz * dz;
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    best = travelled + segment * t;
                }
            }

            travelled += segment;
        }

        return best;
    }

    /// <summary>
    /// 從任意點到任意點的路徑：先接上走道、沿走道走較短的方向、再離開走道到終點。
    /// 回傳依序要經過的點（不含起點、含終點）。只是幾何上的最短方向，沒有單行道、會車或避讓等交通規則。
    /// </summary>
    public IReadOnlyList<(double X, double Z)> PlanPath(double fromX, double fromZ, double toX, double toZ)
    {
        var enter = Project(fromX, fromZ);
        var exit = Project(toX, toZ);
        var forward = ((exit - enter) % Length + Length) % Length;
        var goForward = forward <= Length - forward;
        var travel = goForward ? forward : Length - forward;

        // 途中經過的轉角：依行進方向排序。
        var corners = new List<(double Along, (double X, double Z) Point)>();
        var vertex = 0.0;
        for (var i = 0; i < _points.Length; i++)
        {
            var along = goForward
                ? ((vertex - enter) % Length + Length) % Length
                : ((enter - vertex) % Length + Length) % Length;
            if (along > Epsilon && along < travel - Epsilon)
            {
                corners.Add((along, (_points[i].X, _points[i].Z)));
            }

            vertex += _segmentLengths[i];
        }

        var path = new List<(double X, double Z)> { PointAt(enter) };
        path.AddRange(corners.OrderBy(c => c.Along).Select(c => c.Point));
        path.Add(PointAt(exit));
        path.Add((toX, toZ));

        // 去掉與前一點重疊的點（例如起點本來就在走道上）。
        var result = new List<(double X, double Z)>(path.Count);
        var previous = (X: fromX, Z: fromZ);
        foreach (var point in path)
        {
            if (Math.Abs(point.X - previous.X) > Epsilon || Math.Abs(point.Z - previous.Z) > Epsilon)
            {
                result.Add(point);
                previous = point;
            }
        }

        return result;
    }

    private const double Epsilon = 1e-6;

    private static double Distance(RoutePoint a, RoutePoint b) => Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Z - a.Z, 2));
}
