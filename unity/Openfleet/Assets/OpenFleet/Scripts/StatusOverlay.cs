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
    /// 機台的備料區、成品區、加工進度與可收合的刀具進度；車輛與站點交接的進度。
    /// 站點狀態以 HTTP 輪詢取得，車輛狀態來自 FleetView 已經收到的 MQTT 更新；這裡不保存也不推算任何業務狀態。
    /// 由 PresentationController 在執行時建立；場景物件（站點、車輛）與顏色都向它取得。
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

            StartCoroutine(RefreshLoop());
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            _stations.Clear();
            _vehicleBars.Clear();
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
        }

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
        /// 一個站點的進度條。加工站：備料、成品、加工，另有可收合的刀具列（預設收合，主要看加工進度）。
        /// 其他站點只在有料時顯示一行件數。
        /// </summary>
        private sealed class StationWidget
        {
            private const float TrackWidth = 64f;

            private readonly BarRow _inbound = new BarRow("備料", Colors.Inbound, TrackWidth);
            private readonly BarRow _outbound = new BarRow("成品", Colors.Outbound, TrackWidth);
            private readonly BarRow _machining = new BarRow("加工", Colors.Machining, TrackWidth);
            private readonly BarRow _tool = new BarRow("刀具", Colors.Tool, TrackWidth);
            private readonly Button _toolToggle;
            private readonly Label _summary = new Label { pickingMode = PickingMode.Ignore };
            private bool _toolExpanded;
            private bool _isMachine;
            private string _spare = string.Empty;

            public StationWidget(StationAnchor anchor)
            {
                Anchor = anchor;
                Root = new VisualElement { pickingMode = PickingMode.Ignore };
                Frame(Root);

                Root.Add(_summary);
                Root.Add(_inbound.Root);
                Root.Add(_outbound.Root);
                Root.Add(_machining.Root);

                // 只有這顆按鈕會攔截滑鼠，其餘部分讓滑鼠穿透到場景。
                _toolToggle = new Button(ToggleTool);
                _toolToggle.style.height = 17;
                _toolToggle.style.fontSize = 11;
                _toolToggle.style.marginTop = 3;
                _toolToggle.style.marginBottom = _toolToggle.style.marginLeft = _toolToggle.style.marginRight = 0;
                _toolToggle.style.paddingTop = _toolToggle.style.paddingBottom = 0;
                _toolToggle.style.color = Colors.InputText;
                Root.Add(_toolToggle);
                Root.Add(_tool.Root);
                ShowToolRow();
            }

            public VisualElement Root { get; }

            public StationAnchor Anchor { get; }

            /// <summary>沒有任何東西可顯示時整個隱藏（例如沒有料的倉庫）。</summary>
            public bool HasContent { get; private set; }

            public void Show(StationStatusDto status)
            {
                _isMachine = status.IsMachine;
                _inbound.SetVisible(_isMachine);
                _outbound.SetVisible(_isMachine);
                _machining.SetVisible(_isMachine);
                _summary.style.display = _isMachine ? DisplayStyle.None : DisplayStyle.Flex;

                if (!_isMachine)
                {
                    // 倉庫：等待搬運的件數；暫存點：已暫存的件數。
                    var total = status.InboundQuantity + status.OutboundQuantity;
                    _summary.text = status.OutboundQuantity > 0 ? $"待搬運 {status.OutboundQuantity} 件" : $"暫存 {status.InboundQuantity} 件";
                    HasContent = total > 0;
                    ShowToolRow();
                    return;
                }

                HasContent = true;
                _inbound.Set("備料", Ratio(status.InboundQuantity, status.InboundCapacity), Count(status.InboundQuantity, status.InboundCapacity));
                _outbound.Set("成品", Ratio(status.OutboundQuantity, status.OutboundCapacity), Count(status.OutboundQuantity, status.OutboundCapacity));

                var tool = status.Tool;
                var changing = tool != null && tool.Changing;
                if (changing)
                {
                    // 換刀時機台暫停：主要的加工列直接顯示「換刀」，不必展開刀具列也看得到。
                    _machining.Set("換刀", (float)tool.ChangeProgress, $"{Mathf.RoundToInt((float)tool.ChangeProgress * 100f)}%", Colors.ToolChange);
                }
                else if (status.Machining != null)
                {
                    var m = status.Machining;
                    _machining.Set("加工", Ratio(m.PiecesCompleted, m.Quantity), $"{m.PiecesCompleted}/{m.Quantity}", Colors.Machining);
                }
                else if (status.ShortageCause.HasValue)
                {
                    // 機台因為叫不到車而停著：整條填滿，和一般的待機區分開來。
                    // 車都在忙是缺車；車輛全部離線是重大事故，用不同的字與顏色。
                    var offline = status.ShortageReason == ShortageReason.FleetOffline;
                    string waitingFor;
                    switch (status.ShortageCause.Value)
                    {
                        case MachineDelayCause.WaitingForMaterial: waitingFor = "等料"; break;
                        case MachineDelayCause.WaitingForTool: waitingFor = "等刀"; break;
                        default: waitingFor = "待搬走"; break;
                    }

                    _machining.Set(offline ? "離線" : "缺車", 1f, waitingFor, offline ? Colors.Incident : Colors.Shortage);
                }
                else if (tool != null && tool.WaitingForTool)
                {
                    // 刀具已到壽命、新刀已經在路上：停機等刀，但不是缺車。
                    _machining.Set("等刀", 1f, "配送中", Colors.WaitingForTool);
                }
                else
                {
                    _machining.Set("加工", 0f, "待機", Colors.Machining);
                }

                if (tool != null)
                {
                    _tool.Set("刀具", Ratio(tool.PiecesUsed, tool.LifePieces), $"{tool.PiecesUsed}/{tool.LifePieces}");
                    // 備用的新刀狀態寫在按鈕上，不必展開也看得到。
                    _spare = tool.Spare == ToolSpareStatus.Delivered ? "　新刀已到"
                        : tool.Spare == ToolSpareStatus.Requested ? "　新刀配送中"
                        : string.Empty;
                }

                ShowToolRow();
            }

            private void ToggleTool()
            {
                _toolExpanded = !_toolExpanded;
                ShowToolRow();
            }

            private void ShowToolRow()
            {
                _toolToggle.style.display = _isMachine ? DisplayStyle.Flex : DisplayStyle.None;
                _toolToggle.text = (_toolExpanded ? "刀具 ▲" : "刀具 ▼") + _spare;
                _tool.SetVisible(_isMachine && _toolExpanded);
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
