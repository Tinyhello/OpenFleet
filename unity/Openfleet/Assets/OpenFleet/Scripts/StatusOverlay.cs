using System.Collections;
using System.Collections.Generic;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Colors = OpenFleet.Unity.PresentationController.Colors;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 畫在場景物件上方的進度條（UI Toolkit，螢幕座標，每個 frame 跟著攝影機重新定位）：
    /// 機台的備料區、成品區、每台機台的加工進度與可收合的刀具進度；整爐工序（淬火、回火、電鍍）的集料與這一爐的進度；車輛與站點交接的進度。
    /// 站點狀態以 HTTP 輪詢取得，車輛狀態來自 FleetView 已經收到的 MQTT 更新；這裡不保存也不推算任何業務狀態。
    /// 由 PresentationController 在執行時建立；場景物件（站點、車輛）與顏色都向它取得。
    /// 佈局重建時（StationsChanged）整批丟掉站點的進度條，下一次輪詢依新的站點重新建立。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class StatusOverlay : MonoBehaviour
    {
        private const float RefreshSeconds = 0.5f;

        // 路徑虛線：每一段的長度與間隔（公尺）。以世界座標計算，所以遠處的虛線會隨透視變短。
        private const float DashLength = 0.7f;
        private const float GapLength = 0.5f;

        /// <summary>虛線往目的地流動的速度（公尺／秒）。只是動畫，和車速無關。</summary>
        private const float DashFlowSpeed = 1.6f;

        /// <summary>虛線離地面的高度（公尺）。</summary>
        private const float RouteHeight = 0.1f;

        /// <summary>交接進度條在車輛上方的高度（公尺）。</summary>
        private const float VehicleBarHeight = 2.1f;

        private readonly Dictionary<string, StationWidget> _stations = new Dictionary<string, StationWidget>();
        private readonly Dictionary<VehicleView, VehicleBar> _vehicleBars = new Dictionary<VehicleView, VehicleBar>();
        private readonly List<VehicleView> _stale = new List<VehicleView>();
        private readonly List<Vector3> _routePoints = new List<Vector3>();
        private PresentationController _presentation;
        private VisualElement _root;
        private VisualElement _routeLayer;
        private Camera _camera;

        private string Api => _presentation.Fleet.ApiBaseUrl;

        private void OnEnable()
        {
            _presentation = PresentationController.Instance;
            _root = GetComponent<UIDocument>().rootVisualElement;
            _root.Clear();
            _root.pickingMode = PickingMode.Ignore;
            ProductionPanel.ApplyFont(_root);

            // 路徑虛線畫在最底層（先加入的元素在下面），進度條蓋在它上面。
            _routeLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            _routeLayer.style.position = Position.Absolute;
            _routeLayer.style.left = _routeLayer.style.top = _routeLayer.style.right = _routeLayer.style.bottom = 0;
            _routeLayer.generateVisualContent += DrawRoutes;
            _root.Add(_routeLayer);

            _presentation.StationsChanged += ClearStations;
            _presentation.LayoutEditingChanged += ShowForEditing;
            ShowForEditing();
            StartCoroutine(RefreshLoop());
        }

        private void OnDisable()
        {
            if (_presentation != null)
            {
                _presentation.StationsChanged -= ClearStations;
                _presentation.LayoutEditingChanged -= ShowForEditing;
            }

            StopAllCoroutines();
            _stations.Clear();
            _vehicleBars.Clear();
        }

        /// <summary>
        /// 站點換了：舊的進度條綁在已經刪除的站點物件上，全部移除；新的在下一次輪詢到狀態時依新的錨點建立。
        /// 車輛的進度條不受影響。
        /// </summary>
        private void ClearStations()
        {
            foreach (var widget in _stations.Values)
            {
                widget.Root.RemoveFromHierarchy();
            }

            _stations.Clear();
        }

        /// <summary>
        /// 佈局編輯中（場景畫的是草稿，車輛隱藏）：機台與車輛上的進度條、路徑虛線整層隱藏；離開編輯時恢復。
        /// 編輯中站點會先換成空的（見 LayoutEditor），所以輪詢到的狀態也不會再建立新的進度條。
        /// </summary>
        private void ShowForEditing()
        {
            _root.style.display = _presentation.LayoutEditing ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private IEnumerator RefreshLoop()
        {
            while (true)
            {
                using (var request = UnityWebRequest.Get($"{Api}/api/stations/status"))
                {
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        foreach (var status in FleetJson.Deserialize<StationStatusDto[]>(request.downloadHandler.text))
                        {
                            Apply(status);
                        }
                    }
                }

                yield return new WaitForSecondsRealtime(RefreshSeconds);
            }
        }

        private void Apply(StationStatusDto status)
        {
            if (!_presentation.Stations.TryGetValue(status.Code, out var anchor))
            {
                return;
            }

            anchor.ShowContents(status.InboundQuantity > 0, status.OutboundQuantity > 0);

            if (!_stations.TryGetValue(status.Code, out var widget))
            {
                widget = new StationWidget(anchor);
                _stations.Add(status.Code, widget);
                _root.Add(widget.Root);
            }

            widget.Show(status);
        }

        private void LateUpdate()
        {
            if (_camera == null)
            {
                _camera = Camera.main;
            }

            if (_camera == null || _root.panel == null)
            {
                return;
            }

            foreach (var widget in _stations.Values)
            {
                Place(widget.Root, widget.Anchor.OverlayPosition, widget.HasContent);
            }

            UpdateVehicleBars();

            // 虛線會流動、車和攝影機也會動，所以每個 frame 重畫。
            _routeLayer.MarkDirtyRepaint();
        }

        /// <summary>
        /// 每台車從目前位置沿「接下來要經過的點」畫一條流動的虛線，終點畫一個圓圈。
        /// 亮的部分是後端已經放行、車可以開過去的路；暗的部分已經規劃好，但還在等路段預約。
        /// 路徑來自車輛狀態，這裡只負責畫，不自己規劃路線。
        /// </summary>
        private void DrawRoutes(MeshGenerationContext context)
        {
            if (_camera == null || _root.panel == null)
            {
                return;
            }

            var painter = context.painter2D;
            painter.strokeColor = Colors.Route;
            painter.fillColor = Colors.Route;
            painter.lineWidth = 3f;
            painter.lineCap = LineCap.Round;

            var period = DashLength + GapLength;
            // 位移隨時間增加，虛線就往路徑的前方（目的地）移動。
            var shift = Time.unscaledTime * DashFlowSpeed % period;

            foreach (var vehicle in _presentation.Vehicles)
            {
                var state = vehicle.State;
                if (state == null || state.Connectivity != VehicleConnectivity.Online || state.Route == null || state.Route.Count == 0)
                {
                    continue;
                }

                // 從畫面上的車（已平滑處理的位置）開始，接著是後端給的路徑點。
                _routePoints.Clear();
                _routePoints.Add(OnFloor(vehicle.transform.position));
                foreach (var point in state.Route)
                {
                    _routePoints.Add(OnFloor(new Vector3((float)point.X, 0f, (float)point.Z)));
                }

                // 畫兩次：先畫已放行的部分（亮），再畫還在等預約的部分（暗）。第 i 段的終點是路徑的第 i 個點。
                for (var pass = 0; pass < 2; pass++)
                {
                    painter.strokeColor = pass == 0 ? Colors.Route : Colors.RouteWaiting;
                    painter.BeginPath();
                    var travelled = 0f;
                    for (var i = 0; i < _routePoints.Count - 1; i++)
                    {
                        var from = _routePoints[i];
                        var to = _routePoints[i + 1];
                        var length = Vector3.Distance(from, to);
                        var released = i < state.RouteReleasedCount;
                        if (length < 0.001f || released != (pass == 0))
                        {
                            travelled += length;
                            continue;
                        }

                        // 沿這一段走，依「到起點的累計距離」決定哪些區間是實線。跨過轉角時圖樣不中斷。
                        var position = 0f;
                        while (position < length)
                        {
                            var phase = Mathf.Repeat(travelled + position - shift, period);
                            if (phase < DashLength)
                            {
                                var end = Mathf.Min(position + DashLength - phase, length);
                                if (ToPanel(Vector3.Lerp(from, to, position / length), out var a)
                                    && ToPanel(Vector3.Lerp(from, to, end / length), out var b))
                                {
                                    painter.MoveTo(a);
                                    painter.LineTo(b);
                                }

                                position = end;
                            }
                            else
                            {
                                position += period - phase;
                            }

                            // 浮點誤差可能讓 position 剛好停在邊界上不前進。
                            position += 0.0001f;
                        }

                        travelled += length;
                    }

                    painter.Stroke();
                }

                painter.strokeColor = Colors.Route;
                if (ToPanel(_routePoints[_routePoints.Count - 1], out var destination))
                {
                    painter.BeginPath();
                    painter.Arc(destination, 7f, 0f, 360f);
                    painter.Stroke();
                    painter.BeginPath();
                    painter.Arc(destination, 3f, 0f, 360f);
                    painter.Fill();
                }
            }
        }

        private Vector3 OnFloor(Vector3 position)
        {
            position.y = RouteHeight;
            return position;
        }

        /// <summary>世界座標轉成面板座標；在攝影機後方時回傳 false（投影結果沒有意義）。</summary>
        private bool ToPanel(Vector3 world, out Vector2 panel)
        {
            if (_camera.WorldToViewportPoint(world).z <= 0.05f)
            {
                panel = default;
                return false;
            }

            panel = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, world, _camera);
            return true;
        }

        private void UpdateVehicleBars()
        {
            foreach (var vehicle in _presentation.Vehicles)
            {
                var state = vehicle.State;
                var handover = state != null && state.Connectivity == VehicleConnectivity.Online
                    && (state.Activity == VehicleActivity.Loading || state.Activity == VehicleActivity.Unloading);

                if (!_vehicleBars.TryGetValue(vehicle, out var bar))
                {
                    if (!handover)
                    {
                        continue;
                    }

                    bar = new VehicleBar();
                    _vehicleBars.Add(vehicle, bar);
                    _root.Add(bar.Root);
                }

                if (handover)
                {
                    var percent = Mathf.RoundToInt((float)state.ActivityProgress * 100f);
                        // AGV 與站點交接一框（或一把刀）：在取料點是取，在卸料點是交。
                    bar.Row.Set(state.Activity == VehicleActivity.Loading ? "取貨" : "交貨", (float)state.ActivityProgress, $"{percent}%");
                }

                Place(bar.Root, vehicle.transform.position + Vector3.up * VehicleBarHeight, handover);
            }

            // 車型改變時 FleetView 會換一個物件，舊的進度條要移除。
            _stale.Clear();
            foreach (var pair in _vehicleBars)
            {
                if (pair.Key == null)
                {
                    _stale.Add(pair.Key);
                    pair.Value.Root.RemoveFromHierarchy();
                }
            }

            foreach (var key in _stale)
            {
                _vehicleBars.Remove(key);
            }
        }

        /// <summary>把元件的中心放到世界座標對應的畫面位置；在攝影機後方或不需要顯示時隱藏。</summary>
        private void Place(VisualElement element, Vector3 world, bool visible)
        {
            if (!visible || _camera.WorldToViewportPoint(world).z <= 0f)
            {
                element.style.display = DisplayStyle.None;
                return;
            }

            var position = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, world, _camera);
            element.style.display = DisplayStyle.Flex;
            element.style.left = position.x;
            element.style.top = position.y;

            // 進度條隨距離縮小：56 m 以內維持原本的大小，更遠時和距離成反比（世界裡的大小不變，鄰近機台的條不會互相壓到）。
            // 縮到下限 MinBarScale 就不再變小（再小就看不清楚了）。數字是 Claude 暫定。
            var scale = Mathf.Clamp(BarReferenceDistance / Mathf.Max(0.1f, Vector3.Distance(_camera.transform.position, world)), MinBarScale, 1f);
            element.style.scale = new Scale(new Vector3(scale, scale, 1f));
        }

        /// <summary>
        /// 進度條維持原本大小的最遠攝影機距離（公尺）。以前是 56 m（舊預設視角），但那時相鄰機台的條就已經快碰到（機台間距 8 m，
        /// 條寬約 10 m 的世界尺寸）；新的預設視角更遠，要讓 8 m 間距的條不互相蓋住，參考距離取 40 m（Claude 暫定）。
        /// </summary>
        private const float BarReferenceDistance = 40f;

        /// <summary>進度條縮小的下限（倍數）。</summary>
        private const float MinBarScale = 0.4f;

        /// <summary>浮動小面板的共同外觀：深色半透明底，以自己的中心對齊定位點。</summary>
        private static void Frame(VisualElement element)
        {
            var s = element.style;
            s.position = Position.Absolute;
            s.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            s.backgroundColor = Colors.OverlayBack;
            s.color = Color.white;
            s.fontSize = 12;
            s.paddingTop = s.paddingBottom = 3;
            s.paddingLeft = s.paddingRight = 6;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 4;
        }

        /// <summary>車輛上方的交接進度條。</summary>
        private sealed class VehicleBar
        {
            public VehicleBar()
            {
                Root = new VisualElement { pickingMode = PickingMode.Ignore };
                Frame(Root);
                Root.Add(Row.Root);
            }

            public VisualElement Root { get; }

            public BarRow Row { get; } = new BarRow(string.Empty, Colors.Handover, 80);
        }

        /// <summary>一列進度條：標籤、長條、右側文字。</summary>
        private sealed class BarRow
        {
            private readonly Label _label;
            private readonly VisualElement _fill;
            private readonly Label _value;

            public BarRow(string label, Color color, float trackWidth)
            {
                Root = new VisualElement { pickingMode = PickingMode.Ignore };
                Root.style.flexDirection = FlexDirection.Row;
                Root.style.alignItems = Align.Center;
                Root.style.height = 19;
                Root.style.flexShrink = 0;

                _label = new Label(label) { pickingMode = PickingMode.Ignore };
                _label.style.width = 30;
                _label.style.flexShrink = 0;
                _label.style.whiteSpace = WhiteSpace.NoWrap;
                _label.style.unityTextAlign = TextAnchor.MiddleLeft;
                Compact(_label);
                Root.Add(_label);

                var track = new VisualElement { pickingMode = PickingMode.Ignore };
                track.style.width = trackWidth;
                track.style.height = 8;
                track.style.flexShrink = 0;
                track.style.backgroundColor = Colors.BarTrack;
                Root.Add(track);

                _fill = new VisualElement { pickingMode = PickingMode.Ignore };
                _fill.style.height = Length.Percent(100);
                _fill.style.backgroundColor = color;
                track.Add(_fill);

                _value = new Label { pickingMode = PickingMode.Ignore };
                _value.style.width = 52;
                _value.style.flexShrink = 0;
                _value.style.whiteSpace = WhiteSpace.NoWrap;
                _value.style.unityTextAlign = TextAnchor.MiddleRight;
                Compact(_value);
                Root.Add(_value);
            }

            /// <summary>預設主題的 Label 有外距與內距，小面板裡全部歸零。</summary>
            private static void Compact(Label label)
            {
                label.style.marginTop = label.style.marginBottom = label.style.marginLeft = label.style.marginRight = 0;
                label.style.paddingTop = label.style.paddingBottom = label.style.paddingLeft = label.style.paddingRight = 0;
            }

            public VisualElement Root { get; }

            public void Set(string label, float ratio, string value, Color? color = null)
            {
                _label.text = label;
                _fill.style.width = Length.Percent(Mathf.Clamp01(ratio) * 100f);
                _value.text = value;
                if (color.HasValue)
                {
                    _fill.style.backgroundColor = color.Value;
                }
            }

            public void SetVisible(bool visible)
            {
                Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>
        /// 一個站點的進度條。逐件工序的站：備料、成品（同一站的機台共用），每台機台一列加工進度，
        /// 另有可收合的刀具列（每台一列，預設收合，主要看加工進度）。整爐工序：集料、完成、這一爐。
        /// 其他站點只在有料時顯示一行件數。
        /// </summary>
        private sealed class StationWidget
        {
            private const float TrackWidth = 64f;

            private readonly BarRow _inbound = new BarRow("備料", Colors.Inbound, TrackWidth);
            private readonly BarRow _outbound = new BarRow("成品", Colors.Outbound, TrackWidth);
            private readonly BarRow _furnace = new BarRow("爐內", Colors.Machining, TrackWidth);
            private readonly BarRow _testWait = new BarRow("等待", Colors.TestWait, TrackWidth);
            private readonly VisualElement _machineRows = new VisualElement { pickingMode = PickingMode.Ignore };
            private readonly VisualElement _toolRows = new VisualElement { pickingMode = PickingMode.Ignore };
            private readonly List<BarRow> _machines = new List<BarRow>();
            private readonly List<BarRow> _tools = new List<BarRow>();
            private readonly Button _toolToggle;
            private readonly Label _summary = new Label { pickingMode = PickingMode.Ignore };
            private bool _toolExpanded;
            private bool _isMachine;
            private bool _hasTool;
            private string _spare = string.Empty;

            public StationWidget(StationAnchor anchor)
            {
                Anchor = anchor;
                Root = new VisualElement { pickingMode = PickingMode.Ignore };
                Frame(Root);

                Root.Add(_summary);
                Root.Add(_inbound.Root);
                Root.Add(_outbound.Root);
                Root.Add(_furnace.Root);
                Root.Add(_testWait.Root);
                Root.Add(_machineRows);

                // 只有這顆按鈕會攔截滑鼠，其餘部分讓滑鼠穿透到場景。
                _toolToggle = new Button(ToggleTool);
                _toolToggle.style.height = 17;
                _toolToggle.style.fontSize = 11;
                _toolToggle.style.marginTop = 3;
                _toolToggle.style.marginBottom = _toolToggle.style.marginLeft = _toolToggle.style.marginRight = 0;
                _toolToggle.style.paddingTop = _toolToggle.style.paddingBottom = 0;
                _toolToggle.style.color = Colors.InputText;
                Root.Add(_toolToggle);
                Root.Add(_toolRows);
                ShowToolRows();
            }

            public VisualElement Root { get; }

            public StationAnchor Anchor { get; }

            /// <summary>沒有任何東西可顯示時整個隱藏（例如沒有料的倉庫）。</summary>
            public bool HasContent { get; private set; }

            public void Show(StationStatusDto status)
            {
                _isMachine = status.IsMachine;
                var isFurnace = _isMachine && status.Batch != null;
                _inbound.SetVisible(_isMachine);
                _outbound.SetVisible(_isMachine);
                _furnace.SetVisible(isFurnace);
                _testWait.SetVisible(false);
                _machineRows.style.display = _isMachine && !isFurnace ? DisplayStyle.Flex : DisplayStyle.None;
                _summary.style.display = _isMachine ? DisplayStyle.None : DisplayStyle.Flex;

                if (!_isMachine)
                {
                    _hasTool = false;
                    if (status.Truck != null)
                    {
                        // 出貨站：等貨車的支數與下一班貨車的倒數，沒有箱子時也顯示。
                        _summary.text = $"待出貨 {status.InboundQuantity} 支\n貨車 {Remaining(status.Truck.RemainingSeconds)}";
                        HasContent = true;
                        ShowToolRows();
                        return;
                    }

                    // 倉庫：等待搬運的件數；暫存點：已暫存的件數。
                    var total = status.InboundQuantity + status.OutboundQuantity;
                    _summary.text = status.OutboundQuantity > 0 ? $"待搬運 {status.OutboundQuantity} 件" : $"暫存 {status.InboundQuantity} 件";
                    HasContent = total > 0;
                    ShowToolRows();
                    return;
                }

                HasContent = true;
                if (isFurnace)
                {
                    _hasTool = false;
                    ShowBatch(status);
                    ShowToolRows();
                    return;
                }

                _inbound.Set("備料", Ratio(status.InboundQuantity, status.InboundCapacity), Count(status.InboundQuantity, status.InboundCapacity));
                _outbound.Set("成品", Ratio(status.OutboundQuantity, status.OutboundCapacity), Count(status.OutboundQuantity, status.OutboundCapacity));

                // 一站一台時第一欄寫狀態（加工、換線、缺車…）；兩台以上時第一欄寫機台編號，狀態寫在右邊。
                var machines = status.Machines;
                var several = machines.Count > 1;
                Resize(_machines, _machineRows, machines.Count, Colors.Machining);
                for (var i = 0; i < machines.Count; i++)
                {
                    ShowMachine(_machines[i], machines[i], status, several);
                }

                var spares = new List<string>();
                var toolCount = 0;
                foreach (var machine in machines)
                {
                    if (machine.Tool == null)
                    {
                        continue;
                    }

                    Resize(_tools, _toolRows, toolCount + 1, Colors.Tool);
                    var tool = machine.Tool;
                    _tools[toolCount++].Set(several ? $"{machine.Number}號" : "刀具", Ratio(tool.PiecesUsed, tool.LifePieces), $"{tool.PiecesUsed}/{tool.LifePieces}");
                    // 備用的新刀狀態寫在按鈕上，不必展開也看得到。
                    var prefix = several ? $"{machine.Number}號" : string.Empty;
                    if (tool.Spare == ToolSpareStatus.Delivered)
                    {
                        spares.Add(prefix + "新刀已到");
                    }
                    else if (tool.Spare == ToolSpareStatus.Requested)
                    {
                        spares.Add(prefix + "新刀配送中");
                    }
                }

                Resize(_tools, _toolRows, toolCount, Colors.Tool);
                _hasTool = toolCount > 0;
                _spare = spares.Count > 0 ? "　" + string.Join("、", spares) : string.Empty;
                ShowToolRows();
            }

            /// <summary>
            /// 一台機台的一列。<paramref name="several"/> 為 true 時第一欄是機台編號，狀態（換線、換刀、缺車…）寫在右邊的文字裡；
            /// 顏色一樣依狀態區分。
            /// </summary>
            private static void ShowMachine(BarRow row, MachineStatusDto machine, StationStatusDto station, bool several)
            {
                var tool = machine.Tool;
                var number = $"{machine.Number}號";
                if (tool != null && tool.Changing)
                {
                    // 換刀時機台暫停：主要的加工列直接顯示「換刀」，不必展開刀具列也看得到。
                    var percent = $"{Mathf.RoundToInt((float)tool.ChangeProgress * 100f)}%";
                    row.Set(several ? number : "換刀", (float)tool.ChangeProgress, several ? "換刀" + percent : percent, Colors.ToolChange);
                }
                else if (machine.Machining != null)
                {
                    var m = machine.Machining;
                    if (m.ChangingOver)
                    {
                        // 機床換線：這一框的零件或桿徑和這台機台的上一框不同，先換工控程式，還沒開始做。
                        var percent = $"{Mathf.RoundToInt((float)m.ChangeoverProgress * 100f)}%";
                        row.Set(several ? number : "換線", (float)m.ChangeoverProgress, several ? "換線" + percent : percent, Colors.ToolChange);
                    }
                    else
                    {
                        // 品管站的進度是已檢驗的支數，包裝站是已包裝的支數。
                        row.Set(several ? number : WorkName(station), Ratio(m.PiecesCompleted, m.Quantity), $"{m.PiecesCompleted}/{m.Quantity}", Colors.Machining);
                    }
                }
                else if (machine.ShortageCause.HasValue)
                {
                    // 機台因為叫不到車而停著：整條填滿，和一般的待機區分開來。
                    // 車都在忙是缺車；車輛全部離線是重大事故，用不同的字與顏色。
                    var offline = machine.ShortageReason == ShortageReason.FleetOffline;
                    string waitingFor;
                    switch (machine.ShortageCause.Value)
                    {
                        case MachineDelayCause.WaitingForMaterial: waitingFor = "等料"; break;
                        case MachineDelayCause.WaitingForTool: waitingFor = "等刀"; break;
                        default: waitingFor = "待搬走"; break;
                    }

                    var state = offline ? "離線" : "缺車";
                    row.Set(several ? number : state, 1f, several ? state + waitingFor : waitingFor, offline ? Colors.Incident : Colors.Shortage);
                }
                else if (tool != null && tool.WaitingForTool)
                {
                    // 刀具已到壽命、新刀已經在路上：停機等刀，但不是缺車。
                    row.Set(several ? number : "等刀", 1f, several ? "等刀" : "配送中", Colors.WaitingForTool);
                }
                else
                {
                    row.Set(several ? number : WorkName(station), 0f, "待機", Colors.Machining);
                }
            }

            /// <summary>讓 <paramref name="rows"/> 剛好有 <paramref name="count"/> 列，多的移除、少的補上。</summary>
            private static void Resize(List<BarRow> rows, VisualElement container, int count, Color color)
            {
                while (rows.Count < count)
                {
                    var row = new BarRow(string.Empty, color, TrackWidth);
                    rows.Add(row);
                    container.Add(row.Root);
                }

                while (rows.Count > count)
                {
                    rows[rows.Count - 1].Root.RemoveFromHierarchy();
                    rows.RemoveAt(rows.Count - 1);
                }
            }

            /// <summary>
            /// 整爐工序的站：第一列是站內累積的支數對開爐下限（滿了才開），第二列是做完等搬走的支數，
            /// 第三列是這一爐的工序名稱（淬火、回火、電鍍）、進度與剩下的時間；
            /// 測試模式下爐前有料在等時多一列「等待」，等滿就開爐。
            /// </summary>
            private void ShowBatch(StationStatusDto status)
            {
                var batch = status.Batch;
                _inbound.Set("集料", Ratio(status.InboundQuantity, batch.MinPieces), $"{status.InboundQuantity}/{batch.MinPieces}");
                _outbound.Set("完成", status.OutboundQuantity > 0 ? 1f : 0f, status.OutboundQuantity.ToString());
                if (batch.Running)
                {
                    _furnace.Set(batch.StageName ?? "處理", (float)batch.StageProgress, Remaining(batch.RemainingSeconds), Colors.Machining);
                }
                else
                {
                    _furnace.Set("爐內", 0f, "待機", Colors.Machining);
                }

                // 測試模式專用：爐空著、爐前有料時，等滿設定的秒數就直接開爐（不管有沒有湊到下限）。
                _testWait.SetVisible(batch.TestWaiting);
                if (batch.TestWaiting)
                {
                    _testWait.Set("等待", (float)batch.TestWaitProgress, $"{Mathf.CeilToInt((float)batch.TestWaitRemainingSeconds)}s", Colors.TestWait);
                }
            }

            private static string WorkName(StationStatusDto status)
            {
                return status.IsInspection ? "檢驗" : status.IsPacking ? "包裝" : "加工";
            }

            /// <summary>剩餘時間（原本的工序時間，測試模式下走得比較快）：一小時以上顯示「時h分m」，不到一小時顯示「分m秒s」。</summary>
            private static string Remaining(double seconds)
            {
                var total = Mathf.Max(0, Mathf.CeilToInt((float)seconds));
                return total >= 3600 ? $"{total / 3600}h{total % 3600 / 60:00}m" : $"{total / 60}m{total % 60:00}s";
            }

            private void ToggleTool()
            {
                _toolExpanded = !_toolExpanded;
                ShowToolRows();
            }

            private void ShowToolRows()
            {
                // 沒有刀具的站（拋光、品管、包裝、切料、整爐工序）不顯示刀具列。
                var show = _isMachine && _hasTool;
                _toolToggle.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
                _toolToggle.text = (_toolExpanded ? "刀具 ▲" : "刀具 ▼") + _spare;
                _toolRows.style.display = show && _toolExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            }

            private static float Ratio(int quantity, int? capacity)
            {
                return capacity.HasValue && capacity.Value > 0 ? (float)quantity / capacity.Value : 0f;
            }

            private static string Count(int quantity, int? capacity)
            {
                return capacity.HasValue ? $"{quantity}/{capacity.Value}" : quantity.ToString();
            }
        }
    }
}
