using OpenFleet.Contracts;

namespace OpenFleet.Api.Traffic;

/// <summary>對應 "Traffic" 設定區段。</summary>
public sealed class TrafficOptions
{
    public const string SectionName = "Traffic";

    public TimeSpan ControlInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>車輛前方最多可以先預約幾個節點。越多行駛越順，但占用的路也越多。</summary>
    public int Lookahead { get; set; } = 2;

    /// <summary>被別台車擋住超過這段時間，就把被占用的路排除後重新規劃。</summary>
    public TimeSpan ReplanAfter { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>改道或退讓之後，隔這麼久才會再改一次，避免兩台車互相讓來讓去。</summary>
    public TimeSpan ReplanCooldown { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>放行指令送出後，車輛的遙測沒有反映出來就每隔這麼久重送一次。</summary>
    public TimeSpan ResendAfter { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>一台車這一輪的輸入：它回報的位置與進度，以及後端要它去哪裡。</summary>
public sealed record TrafficInput(
    string VehicleId,
    bool Online,
    Guid TelemetrySessionId,
    double X,
    double Z,
    Guid? PlanId,
    long Revision,
    int ReachedCount,
    string? GoalNode,
    bool CanMove);

/// <summary>要送給某台車的放行內容。</summary>
public sealed record TrafficClearance(string VehicleId, VehicleClearance Clearance);

/// <summary>
/// 交通控制（Singleton，狀態在記憶體）：替每台車規劃路徑、預約節點與路段，並決定各車可以開到哪裡。
///
/// 規則：
/// 1. 節點與路段同一時間只屬於一台車。車持有「目前所在的節點」到「已放行的最後一個節點」之間的所有節點與路段，
///    通過之後才釋放。
/// 2. 已放行的部分不會收回或更改；改道只會換掉還沒放行的部分。
/// 3. 進入只有一個出入口的支線前，路口與支線要一起拿到，拿不到就停在路口之前，不會堵住路口。
/// 4. 被擋住太久：把別台車占用的節點與路段排除後重新規劃（例如換到另一條車道）。
/// 5. 互相等待形成循環（死結）：挑一台改道；沒有別的路可走時，先退到旁邊的空節點讓路。
///
/// 這個類別不碰資料庫、MQTT 或時鐘以外的任何東西，所有輸入都由呼叫端傳入，方便單獨測試。
/// </summary>
public sealed class TrafficController(RoadNetwork network, TrafficOptions options, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _vehicles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeOwner = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), string> _edgeOwner = [];

    /// <summary>某台車目前所在（或正要抵達）的節點；還不認得這台車時回傳 null。</summary>
    public string? NodeOf(string vehicleId)
    {
        lock (_gate)
        {
            return _vehicles.TryGetValue(vehicleId, out var entry) ? entry.Path[entry.Reached - 1] : null;
        }
    }

    /// <summary>某台車是否停在指定的節點上（已經到達，而且前方沒有已放行的路）。</summary>
    public bool IsAt(string vehicleId, string node)
    {
        lock (_gate)
        {
            return _vehicles.TryGetValue(vehicleId, out var entry)
                && entry.Reached == entry.Released
                && entry.Path[entry.Reached - 1] == node;
        }
    }

    /// <summary>目前的預約狀況（節點 → 車輛），供測試與除錯。</summary>
    public IReadOnlyDictionary<string, string> ReservedNodes()
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(_nodeOwner, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 執行一輪交通控制，回傳這一輪需要送出（或重送）的放行內容。
    /// </summary>
    public IReadOnlyList<TrafficClearance> Update(IReadOnlyCollection<TrafficInput> inputs)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var entries = new List<(Entry Entry, TrafficInput Input)>(inputs.Count);

            // 依 ID 排序：誰先拿到路是確定的，同樣的輸入會得到同樣的結果。
            foreach (var input in inputs.OrderBy(i => i.VehicleId, StringComparer.Ordinal))
            {
                var entry = Track(input);
                SyncProgress(entry, input);
                entries.Add((entry, input));
            }

            foreach (var (entry, input) in entries)
            {
                // 離線的車維持原地與原有的預約：它實際上還占著那個位置。
                var goal = input.Online ? input.GoalNode : null;
                SetGoal(entry, goal, input.Online && input.CanMove);
            }

            foreach (var (entry, _) in entries)
            {
                Extend(entry, now);
            }

            ResolveDeadlocks(now);
            ReplanLongWaits(now);

            var clearances = new List<TrafficClearance>();
            foreach (var (entry, input) in entries)
            {
                // 內容有變就送；車輛回報的版本落後時定期重送（訊息遺失，或模擬器剛重啟）。
                var behind = input.Online && (input.PlanId != entry.PlanId || input.Revision < entry.Revision);
                if (entry.Dirty || (behind && now - entry.SentAt >= options.ResendAfter))
                {
                    entry.Dirty = false;
                    entry.SentAt = now;
                    clearances.Add(new TrafficClearance(entry.VehicleId, ToClearance(entry)));
                }
            }

            return clearances;
        }
    }

    /// <summary>第一次看到的車、或設備重啟過（新的遙測 Session）的車：放在最近的節點上，從那裡重新開始。</summary>
    private Entry Track(TrafficInput input)
    {
        if (_vehicles.TryGetValue(input.VehicleId, out var entry) && entry.SessionId == input.TelemetrySessionId)
        {
            return entry;
        }

        if (entry is not null)
        {
            ReleaseAll(entry);
        }

        var start = network.Nearest(input.X, input.Z).Code;
        entry = new Entry(input.VehicleId, input.TelemetrySessionId, start);
        _vehicles[input.VehicleId] = entry;
        Hold(entry);
        return entry;
    }

    /// <summary>依車輛回報的進度，釋放它已經離開的節點與路段。</summary>
    private void SyncProgress(Entry entry, TrafficInput input)
    {
        // 進度只會前進，而且不會超過已放行的範圍；回報的是別的路徑時（還沒收到新的放行）先不採用。
        if (input.PlanId == entry.PlanId)
        {
            var reached = Math.Clamp(input.ReachedCount, entry.Reached, entry.Released);
            if (reached != entry.Reached)
            {
                entry.Reached = reached;
                ReleaseAll(entry);
                Hold(entry);
            }
        }
    }

    private void SetGoal(Entry entry, string? goal, bool canMove)
    {
        entry.CanMove = canMove;
        if (entry.Goal == goal)
        {
            return;
        }

        entry.Goal = goal;
        entry.BlockedSince = null;
        entry.BlockedBy = null;

        // 從「已放行的最後一個節點」接上新的路：車一定會走到那裡，之後才照新的路走。
        var anchor = entry.Path[entry.Released - 1];
        var route = goal is null ? [anchor] : network.FindPath(anchor, goal) ?? [anchor];
        Reroute(entry, route);
    }

    /// <summary>
    /// 換掉還沒放行的部分。上一段路已經走完時開一個新的 PlanId（路徑從目前的節點重新編號），
    /// 否則沿用原本的 PlanId、只提高版本，車輛已經走過的進度仍然有效。
    /// </summary>
    private void Reroute(Entry entry, IReadOnlyList<string> routeFromAnchor)
    {
        if (entry.Reached == entry.Path.Count)
        {
            entry.PlanId = Guid.NewGuid();
            entry.Path = [.. routeFromAnchor];
            entry.Reached = 1;
            entry.Released = 1;
            entry.Revision = 1;
        }
        else
        {
            entry.Path = [.. entry.Path.Take(entry.Released), .. routeFromAnchor.Skip(1)];
            entry.Revision++;
        }

        entry.Dirty = true;
    }

    /// <summary>在前瞻範圍內盡量往前預約。拿不到時記下是被誰擋住。</summary>
    private void Extend(Entry entry, DateTimeOffset now)
    {
        if (!entry.CanMove)
        {
            entry.BlockedSince = null;
            entry.BlockedBy = null;
            return;
        }

        while (entry.Released < entry.Path.Count && entry.Released - entry.Reached < options.Lookahead)
        {
            var hops = 1;

            // 下一步是路口、再下一步是支線：兩個要一起拿到，否則停在路口之前。
            if (entry.Released + 1 < entry.Path.Count && network.IsDeadEnd(entry.Path[entry.Released + 1]))
            {
                hops = 2;
            }

            string? blocker = null;
            for (var i = 0; i < hops && blocker is null; i++)
            {
                var a = entry.Path[entry.Released - 1 + i];
                var b = entry.Path[entry.Released + i];
                blocker = OwnerOtherThan(entry, _nodeOwner.GetValueOrDefault(b))
                    ?? OwnerOtherThan(entry, _edgeOwner.GetValueOrDefault(RoadNetwork.EdgeKey(a, b)));
            }

            if (blocker is not null)
            {
                entry.BlockedBy = blocker;
                entry.BlockedSince ??= now;
                return;
            }

            entry.Released += hops;
            entry.Revision++;
            entry.Dirty = true;
            Hold(entry);
        }

        entry.BlockedSince = null;
        entry.BlockedBy = null;
    }

    /// <summary>
    /// 死結：A 等 B、B 等 A（或更長的循環）。循環裡的車誰也不會自己動，所以要主動處理：
    /// 依序找一台能改道的車；都不能改道時，找一台能退到旁邊空節點的車先讓路。
    /// </summary>
    private void ResolveDeadlocks(DateTimeOffset now)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);

        foreach (var start in _vehicles.Values.OrderBy(e => e.VehicleId, StringComparer.Ordinal))
        {
            if (handled.Contains(start.VehicleId) || FindCycle(start) is not { } cycle)
            {
                continue;
            }

            handled.UnionWith(cycle.Select(e => e.VehicleId));

            // ID 較大的先讓：規則固定，結果可重現。之後可以改成依任務優先順序決定。
            var candidates = cycle
                .Where(e => now >= e.NoReplanUntil)
                .OrderByDescending(e => e.VehicleId, StringComparer.Ordinal)
                .ToList();

            if (!candidates.Any(e => TryReplan(e, now)))
            {
                _ = candidates.Any(e => TryYield(e, now));
            }
        }
    }

    /// <summary>沒有形成循環，但已經等很久（例如前車停在站點上下料）：試著繞過去。</summary>
    private void ReplanLongWaits(DateTimeOffset now)
    {
        foreach (var entry in _vehicles.Values)
        {
            if (entry.BlockedSince is { } since && now - since >= options.ReplanAfter && now >= entry.NoReplanUntil)
            {
                TryReplan(entry, now);
            }
        }
    }

    private List<Entry>? FindCycle(Entry start)
    {
        var chain = new List<Entry>();
        var current = start;

        while (current.BlockedBy is { } blockerId && _vehicles.TryGetValue(blockerId, out var blocker))
        {
            chain.Add(current);
            if (blocker == start)
            {
                return chain;
            }

            if (chain.Contains(blocker))
            {
                // 循環存在，但不包含起點；輪到循環裡的車當起點時再處理。
                return null;
            }

            current = blocker;
        }

        return null;
    }

    /// <summary>避開別台車目前占用的節點與路段重新規劃；找到不同的路才算成功。</summary>
    private bool TryReplan(Entry entry, DateTimeOffset now)
    {
        if (entry.Goal is null)
        {
            return false;
        }

        var anchor = entry.Path[entry.Released - 1];
        var blockedNodes = _nodeOwner.Where(p => p.Value != entry.VehicleId).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var blockedEdges = _edgeOwner.Where(p => p.Value != entry.VehicleId).Select(p => p.Key).ToHashSet();
        var route = network.FindPath(anchor, entry.Goal, blockedNodes, blockedEdges);

        if (route is null || route.SequenceEqual(entry.Path.Skip(entry.Released - 1)))
        {
            return false;
        }

        Reroute(entry, route);
        entry.NoReplanUntil = now + options.ReplanCooldown;
        entry.BlockedSince = null;
        entry.BlockedBy = null;
        return true;
    }

    /// <summary>
    /// 退讓：先移到旁邊一個空的節點（優先選不是支線的），再從那裡回到原本的目的地。
    /// 讓出來的節點就是對方要走的路。
    /// </summary>
    private bool TryYield(Entry entry, DateTimeOffset now)
    {
        if (entry.Goal is null || entry.Released != entry.Reached)
        {
            // 還有已放行、沒走完的路時不退讓：那段路已經是它的，走完再說。
            return false;
        }

        var anchor = entry.Path[entry.Released - 1];
        var aside = network.Neighbours(anchor)
            .Where(n => !_nodeOwner.ContainsKey(n.To) && !_edgeOwner.ContainsKey(RoadNetwork.EdgeKey(anchor, n.To)))
            .OrderBy(n => network.IsDeadEnd(n.To))
            .ThenBy(n => n.Length)
            .Select(n => n.To)
            .FirstOrDefault();

        if (aside is null || network.FindPath(aside, entry.Goal) is not { } back)
        {
            return false;
        }

        Reroute(entry, [anchor, .. back]);
        entry.NoReplanUntil = now + options.ReplanCooldown;
        entry.BlockedSince = null;
        entry.BlockedBy = null;
        return true;
    }

    /// <summary>登記這台車目前持有的節點與路段：從所在節點到已放行的最後一個節點。</summary>
    private void Hold(Entry entry)
    {
        for (var i = entry.Reached - 1; i < entry.Released; i++)
        {
            _nodeOwner[entry.Path[i]] = entry.VehicleId;
            if (i > entry.Reached - 1)
            {
                _edgeOwner[RoadNetwork.EdgeKey(entry.Path[i - 1], entry.Path[i])] = entry.VehicleId;
            }
        }
    }

    private void ReleaseAll(Entry entry)
    {
        foreach (var node in _nodeOwner.Where(p => p.Value == entry.VehicleId).Select(p => p.Key).ToList())
        {
            _nodeOwner.Remove(node);
        }

        foreach (var edge in _edgeOwner.Where(p => p.Value == entry.VehicleId).Select(p => p.Key).ToList())
        {
            _edgeOwner.Remove(edge);
        }
    }

    private static string? OwnerOtherThan(Entry entry, string? owner) => owner is null || owner == entry.VehicleId ? null : owner;

    private VehicleClearance ToClearance(Entry entry) => new()
    {
        PlanId = entry.PlanId,
        Revision = entry.Revision,
        ReleasedCount = entry.Released,
        Path = entry.Path.Select(code =>
        {
            var node = network.Node(code);
            return new Position3 { X = node.X, Y = 0, Z = node.Z };
        }).ToList(),
    };

    private sealed class Entry(string vehicleId, Guid sessionId, string startNode)
    {
        public string VehicleId { get; } = vehicleId;
        public Guid SessionId { get; } = sessionId;

        public Guid PlanId { get; set; } = Guid.NewGuid();
        public long Revision { get; set; } = 1;

        /// <summary>這一段路依序經過的節點；第一個是出發時所在的節點。</summary>
        public List<string> Path { get; set; } = [startNode];

        /// <summary>車輛已經到達的節點數（至少 1：出發點）。</summary>
        public int Reached { get; set; } = 1;

        /// <summary>已放行的節點數；車可以開到 Path[Released - 1]。</summary>
        public int Released { get; set; } = 1;

        public string? Goal { get; set; }
        public bool CanMove { get; set; }

        // 一開始就送一次：讓車對齊到路網的節點上。
        public bool Dirty { get; set; } = true;
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.MinValue;
        public string? BlockedBy { get; set; }
        public DateTimeOffset? BlockedSince { get; set; }
        public DateTimeOffset NoReplanUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
