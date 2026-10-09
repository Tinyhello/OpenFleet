using System.Collections;
using System.Collections.Generic;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 畫在場景物件上方的進度條（UI Toolkit，螢幕座標，每個 frame 跟著攝影機重新定位）：
    /// 機台的備料區、成品區、加工進度與可收合的刀具進度；車輛上下料的進度。
    /// 站點狀態以 HTTP 輪詢取得，車輛狀態來自 FleetView 已經收到的 MQTT 更新；這裡不保存也不推算任何業務狀態。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class StatusOverlay : MonoBehaviour
    {
        [SerializeField] private FleetView fleetView;
        [SerializeField] private float refreshSeconds = 0.5f;

        [Header("路徑虛線")]
        [Tooltip("虛線每一段的長度與間隔（公尺）。以世界座標計算，所以遠處的虛線會隨透視變短。")]
        [SerializeField] private float dashLength = 0.7f;
        [SerializeField] private float gapLength = 0.5f;

        [Tooltip("虛線往目的地流動的速度（公尺／秒）。只是動畫，和車速無關。")]
        [SerializeField] private float dashFlowSpeed = 1.6f;

        [Tooltip("虛線離地面的高度（公尺）。")]
        [SerializeField] private float routeHeight = 0.1f;

        [Tooltip("上下料進度條在車輛上方的高度（公尺）。")]
        [SerializeField] private float vehicleBarHeight = 2.1f;

        private static readonly Color BackColor = new Color(0.08f, 0.09f, 0.11f, 0.82f);
        private static readonly Color TrackColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color InboundColor = new Color(0.36f, 0.62f, 0.95f);
        private static readonly Color OutboundColor = new Color(0.45f, 0.82f, 0.5f);
        private static readonly Color MachiningColor = new Color(0.98f, 0.72f, 0.25f);
        private static readonly Color ToolColor = new Color(0.78f, 0.6f, 0.95f);
        private static readonly Color ToolChangeColor = new Color(0.98f, 0.45f, 0.4f);
        private static readonly Color HandoverColor = new Color(0.35f, 0.85f, 0.9f);
        private static readonly Color RouteColor = new Color(1f, 0.92f, 0.35f, 0.95f);

        private readonly Dictionary<string, StationAnchor> _anchors = new Dictionary<string, StationAnchor>();
        private readonly Dictionary<string, StationWidget> _stations = new Dictionary<string, StationWidget>();
        private readonly Dictionary<VehicleView, VehicleBar> _vehicleBars = new Dictionary<VehicleView, VehicleBar>();
        private readonly List<VehicleView> _stale = new List<VehicleView>();
        private readonly List<Vector3> _routePoints = new List<Vector3>();
        private VisualElement _root;
        private VisualElement _routeLayer;
        private Camera _camera;

        private string Api => fleetView != null ? fleetView.ApiBaseUrl : "http://localhost:5229";

        private void OnEnable()
        {
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

            _anchors.Clear();
            foreach (var anchor in FindObjectsByType<StationAnchor>(FindObjectsInactive.Exclude))
            {
                _anchors[anchor.StationCode] = anchor;
                // 還沒收到後端狀態前先當作沒有料。
                anchor.ShowContents(false, false);
            }

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

                yield return new WaitForSecondsRealtime(refreshSeconds);
            }
        }

        private void Apply(StationStatusDto status)
        {
            if (!_anchors.TryGetValue(status.Code, out var anchor))
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
        /// 路徑來自車輛狀態（模擬器回報），這裡只負責畫，不自己規劃路線。
        /// </summary>
        private void DrawRoutes(MeshGenerationContext context)
        {
            if (_camera == null || fleetView == null || _root.panel == null)
            {
                return;
            }

            var painter = context.painter2D;
            painter.strokeColor = RouteColor;
            painter.fillColor = RouteColor;
            painter.lineWidth = 3f;
            painter.lineCap = LineCap.Round;

            var period = dashLength + gapLength;
            // 位移隨時間增加，虛線就往路徑的前方（目的地）移動。
            var shift = Time.unscaledTime * dashFlowSpeed % period;

            foreach (var vehicle in fleetView.Vehicles)
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

                painter.BeginPath();
                var travelled = 0f;
                for (var i = 0; i < _routePoints.Count - 1; i++)
                {
                    var from = _routePoints[i];
                    var to = _routePoints[i + 1];
                    var length = Vector3.Distance(from, to);
                    if (length < 0.001f)
                    {
                        continue;
                    }

                    // 沿這一段走，依「到起點的累計距離」決定哪些區間是實線。跨過轉角時圖樣不中斷。
                    var position = 0f;
                    while (position < length)
                    {
                        var phase = Mathf.Repeat(travelled + position - shift, period);
                        if (phase < dashLength)
                        {
                            var end = Mathf.Min(position + dashLength - phase, length);
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
            position.y = routeHeight;
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
            if (fleetView == null)
            {
                return;
            }

            foreach (var vehicle in fleetView.Vehicles)
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
                    bar.Row.Set(state.Activity == VehicleActivity.Loading ? "上料" : "下料", (float)state.ActivityProgress, $"{percent}%");
                }

                Place(bar.Root, vehicle.transform.position + Vector3.up * vehicleBarHeight, handover);
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
            s.backgroundColor = BackColor;
            s.color = Color.white;
            s.fontSize = 12;
            s.paddingTop = s.paddingBottom = 3;
            s.paddingLeft = s.paddingRight = 6;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 4;
        }

        /// <summary>車輛上方的上下料進度條。</summary>
        private sealed class VehicleBar
        {
            public VehicleBar()
            {
                Root = new VisualElement { pickingMode = PickingMode.Ignore };
                Frame(Root);
                Root.Add(Row.Root);
            }

            public VisualElement Root { get; }

            public BarRow Row { get; } = new BarRow(string.Empty, HandoverColor, 80);
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
                track.style.backgroundColor = TrackColor;
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

            private readonly BarRow _inbound = new BarRow("備料", InboundColor, TrackWidth);
            private readonly BarRow _outbound = new BarRow("成品", OutboundColor, TrackWidth);
            private readonly BarRow _machining = new BarRow("加工", MachiningColor, TrackWidth);
            private readonly BarRow _tool = new BarRow("刀具", ToolColor, TrackWidth);
            private readonly Button _toolToggle;
            private readonly Label _summary = new Label { pickingMode = PickingMode.Ignore };
            private bool _toolExpanded;
            private bool _isMachine;

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
                _toolToggle.style.color = new Color(0.08f, 0.09f, 0.11f);
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
                    _machining.Set("換刀", (float)tool.ChangeProgress, $"{Mathf.RoundToInt((float)tool.ChangeProgress * 100f)}%", ToolChangeColor);
                }
                else if (status.Machining != null)
                {
                    var m = status.Machining;
                    _machining.Set("加工", Ratio(m.PiecesCompleted, m.Quantity), $"{m.PiecesCompleted}/{m.Quantity}", MachiningColor);
                }
                else
                {
                    _machining.Set("加工", 0f, "待機", MachiningColor);
                }

                if (tool != null)
                {
                    _tool.Set("刀具", Ratio(tool.PiecesUsed, tool.LifePieces), $"{tool.PiecesUsed}/{tool.LifePieces}");
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
                _toolToggle.text = _toolExpanded ? "刀具 ▲" : "刀具 ▼";
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
