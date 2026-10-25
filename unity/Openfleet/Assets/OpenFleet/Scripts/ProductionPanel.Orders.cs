using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Colors = OpenFleet.Unity.PresentationController.Colors;

namespace OpenFleet.Unity
{
    /// <summary>
    /// 操作面板的下單與訂單（P5）：建立客戶訂單（可以好幾行，每一行是整組或單一零件，可以填交期），
    /// 看每張訂單每一行每種零件的進度、工單與補做工單、出貨單與料框的位置，負責人同意分批出貨，
    /// 以及負責人把訂單設成急單（P6）。訂單的狀態都在後端；這裡只讀取與送出請求。
    /// </summary>
    public sealed partial class ProductionPanel
    {
        private const int OrdersShown = 8;

        /// <summary>交期預設幾天後：一組沖模從下單到完成約三週到一個月（使用者 2026-10-22）。0 表示不填交期。</summary>
        private const int DefaultDueDays = 28;

        /// <summary>品項的選項：整組，或只做其中一種零件（對應品項代碼的字尾）。</summary>
        private static readonly string[] Kinds = { "整組", "只做上沖", "只做中模", "只做下沖" };
        private static readonly string[] KindParts = { null, ProductParts.UpperPunch, ProductParts.Die, ProductParts.LowerPunch };

        private readonly List<PunchSpecDto> _specs = new List<PunchSpecDto>();
        private readonly List<CreateSalesOrderLineRequest> _pendingLines = new List<CreateSalesOrderLineRequest>();
        private readonly Dictionary<Guid, OrderView> _orderViews = new Dictionary<Guid, OrderView>();
        private TextField _customer;
        private DropdownField _spec;
        private DropdownField _kind;
        private SliderInt _orderQuantity;
        private SliderInt _dueDays;
        private VisualElement _pendingRows;
        private TextField _approver;
        private VisualElement _orderRows;
        private string _orderListShown = string.Empty;

        /// <summary>
        /// 這一張要送出的訂單的 Idempotency-Key：明細有變才換新的。送出失敗（例如逾時）再按一次用同一個鍵，
        /// 後端已經建立過的話會回傳同一張，不會多一張。
        /// </summary>
        private string _orderKey;

        private void BuildOrderForm(VisualElement content)
        {
            content.Add(Heading("下單"));
            content.Add(Hint("一張訂單可以有好幾行，每一行是整組沖模（上沖、中模、下沖各一）或單一零件。整張訂單做完才一起出貨。" +
                "交期是幾天後，0 表示不填；排程時急單最先，其次交期早的。"));

            _customer = Text("客戶", "示範客戶");
            // 同一個 Idempotency-Key 只能送同樣的內容：客戶或交期改了就換新的鍵。
            _customer.RegisterValueChangedCallback(_ => _orderKey = null);
            content.Add(_customer);

            _spec = new DropdownField("規格", new List<string> { "（讀取中）" }, 0);
            StyleField(_spec);
            content.Add(_spec);

            _kind = new DropdownField("品項", new List<string>(Kinds), 0);
            StyleField(_kind);
            content.Add(_kind);

            _orderQuantity = NumberField("數量（組／支）", 1, 300);
            _orderQuantity.value = 30;
            content.Add(_orderQuantity);

            _dueDays = NumberField("交期（天後）", 0, 60);
            _dueDays.value = DefaultDueDays;
            _dueDays.RegisterValueChangedCallback(_ => _orderKey = null);
            content.Add(_dueDays);

            var buttons = Row();
            buttons.Add(ActionButton("加入明細", AddPendingLine));
            buttons.Add(ActionButton("送出訂單", () => StartCoroutine(SubmitOrder())));
            content.Add(buttons);

            _pendingRows = new VisualElement();
            content.Add(_pendingRows);
            ShowPendingLines();
        }

        private void BuildOrderList(VisualElement content)
        {
            content.Add(Heading("訂單"));
            content.Add(Hint("整張訂單做完才出貨；負責人同意的話可以先出已經湊成完整的組（只能分一次），剩下的到齊再出。" +
                "負責人也可以把訂單設成急單，排在所有訂單前面（各站排隊、裝爐、分配圓棒、派車都先處理）。"));
            _approver = Text("負責人", "廠長");
            content.Add(_approver);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.maxHeight = 360;
            scroll.style.marginTop = 6;
            _orderRows = scroll.contentContainer;
            _orderRows.Add(Hint("還沒有訂單。"));
            content.Add(scroll);
        }

        private bool _specsReady;

        /// <summary>沖頭規格的清單，下單時選。<paramref name="quiet"/> 為 true 時失敗不顯示錯誤（自動重試用）。</summary>
        private IEnumerator LoadSpecs(bool quiet = false)
        {
            _specsReady = false;
            using (var request = UnityWebRequest.Get($"{Api}/api/catalog/specs"))
            {
                yield return request.SendWebRequest();
                if (quiet ? request.result != UnityWebRequest.Result.Success : !Succeeded(request, "讀取規格"))
                {
                    yield break;
                }

                _specsReady = true;

                var selected = SelectedSpec()?.Code;
                _specs.Clear();
                _specs.AddRange(FleetJson.Deserialize<PunchSpecDto[]>(request.downloadHandler.text));
                // 規格代碼已經包含桿徑（P30、P24），這裡只再加上名稱，避免在窄的面板裡被截斷。
                var choices = _specs.ConvertAll(s => $"{s.Code}　{s.Name}");
                _spec.choices = choices.Count > 0 ? choices : new List<string> { "（沒有規格）" };
                var index = Math.Max(0, _specs.FindIndex(s => s.Code == selected));
                _spec.index = index;
            }
        }

        private PunchSpecDto SelectedSpec()
        {
            return _spec != null && _spec.index >= 0 && _spec.index < _specs.Count ? _specs[_spec.index] : null;
        }

        /// <summary>目前選的規格、品項與數量；還沒讀到規格時是 null。</summary>
        private CreateSalesOrderLineRequest CurrentLine()
        {
            var spec = SelectedSpec();
            if (spec == null)
            {
                return null;
            }

            var part = KindParts[Mathf.Clamp(_kind.index, 0, KindParts.Length - 1)];
            return new CreateSalesOrderLineRequest
            {
                ItemCode = part == null ? spec.Code : $"{spec.Code}-{ProductParts.Letter(part)}",
                Quantity = _orderQuantity.value,
            };
        }

        private void AddPendingLine()
        {
            var line = CurrentLine();
            if (line == null)
            {
                SetStatus("還沒讀到規格，請稍候或按示範設定裡的「重新載入」。", Colors.Error);
                return;
            }

            _pendingLines.Add(line);
            _orderKey = null;
            ShowPendingLines();
        }

        private void ShowPendingLines()
        {
            _pendingRows.Clear();
            if (_pendingLines.Count == 0)
            {
                _pendingRows.Add(Hint("沒有加入明細時，「送出訂單」就用上面選的這一行。"));
                return;
            }

            for (var i = 0; i < _pendingLines.Count; i++)
            {
                var index = i;
                var line = _pendingLines[i];
                var row = Row();
                var label = new Label($"{i + 1}. {line.ItemCode} × {line.Quantity}");
                label.style.flexGrow = 1;
                label.style.fontSize = 13;
                label.style.unityTextAlign = TextAnchor.MiddleLeft;
                row.Add(label);
                var remove = ActionButton("移除", () =>
                {
                    _pendingLines.RemoveAt(index);
                    _orderKey = null;
                    ShowPendingLines();
                });
                remove.style.flexGrow = 0;
                remove.style.width = 56;
                row.Add(remove);
                _pendingRows.Add(row);
            }
        }

        private IEnumerator SubmitOrder()
        {
            var lines = new List<CreateSalesOrderLineRequest>(_pendingLines);
            if (lines.Count == 0)
            {
                var current = CurrentLine();
                if (current == null)
                {
                    SetStatus("還沒讀到規格，請稍候或按示範設定裡的「重新載入」。", Colors.Error);
                    yield break;
                }

                lines.Add(current);
            }

            if (_orderKey == null)
            {
                _orderKey = Guid.NewGuid().ToString("N");
            }

            // 交期只取日期，不帶時區（帶了 +08:00 的話，時區不同的後端會差一天）。
            var body = new CreateSalesOrderRequest
            {
                Customer = _customer.value,
                DueDate = _dueDays.value > 0 ? DateTime.SpecifyKind(DateTime.Today.AddDays(_dueDays.value), DateTimeKind.Unspecified) : (DateTime?)null,
                Lines = lines,
            };
            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPOST, "/api/sales-orders", FleetJson.Serialize(body)))
            {
                request.SetRequestHeader("Idempotency-Key", _orderKey);
                yield return request.SendWebRequest();
                if (!Succeeded(request, "下單"))
                {
                    yield break;
                }

                var order = FleetJson.Deserialize<SalesOrderDto>(request.downloadHandler.text);
                var workOrders = new List<string>();
                foreach (var line in order.Lines)
                {
                    foreach (var workOrder in line.ProductionOrders)
                    {
                        workOrders.Add(workOrder.Number);
                    }
                }

                SetStatus($"已建立訂單 {order.Number}：{order.Lines.Count} 行，工單 {string.Join("、", workOrders)}。", Colors.Ok);
                _pendingLines.Clear();
                _orderKey = null;
                ShowPendingLines();
                yield return RefreshOrders();
            }
        }

        private IEnumerator ApprovePartial(Guid orderId, string orderNumber)
        {
            var json = FleetJson.Serialize(new PartialShipmentRequest { ApprovedBy = _approver.value });
            using (var request = JsonRequest(UnityWebRequest.kHttpVerbPOST, $"/api/sales-orders/{orderId}/partial-shipment", json))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request, "同意分批"))
                {
                    SetStatus($"{orderNumber}：已同意分批出貨，已經湊成完整的組會先裝箱出貨。", Colors.Ok);
                    yield return RefreshOrders();
                }
            }
        }

        /// <summary>設成急單（記下上面填的負責人）或取消急單。能不能設由後端檢查（例如已經做完的訂單不能設）。</summary>
        private IEnumerator ToggleRush(Guid orderId, string orderNumber, bool isRush)
        {
            var json = isRush ? null : FleetJson.Serialize(new RushRequest { RequestedBy = _approver.value });
            var method = isRush ? UnityWebRequest.kHttpVerbDELETE : UnityWebRequest.kHttpVerbPOST;
            using (var request = JsonRequest(method, $"/api/sales-orders/{orderId}/rush", json))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request, isRush ? "取消急單" : "設為急單"))
                {
                    SetStatus(isRush ? $"{orderNumber}：已取消急單，之後照交期排。" : $"{orderNumber}：已設為急單，之後排在所有訂單前面。", Colors.Ok);
                    yield return RefreshOrders();
                }
            }
        }

        /// <summary>
        /// 最近的訂單與每一框的位置。每張訂單的畫面只建立一次，之後只更新文字：
        /// 每秒重建的話，按鈕會在按下去的瞬間被換掉，展開的料框清單也會收起來。
        /// </summary>
        private IEnumerator RefreshOrders()
        {
            SalesOrderDto[] orders;
            using (var request = UnityWebRequest.Get($"{Api}/api/sales-orders?take={OrdersShown}"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    yield break;
                }

                orders = FleetJson.Deserialize<SalesOrderDto[]>(request.downloadHandler.text);
            }

            LotDto[] lots;
            using (var request = UnityWebRequest.Get($"{Api}/api/lots"))
            {
                yield return request.SendWebRequest();
                lots = request.result == UnityWebRequest.Result.Success
                    ? FleetJson.Deserialize<LotDto[]>(request.downloadHandler.text)
                    : new LotDto[0];
            }

            // 訂單有增減（新下單、清除示範資料）才重排；否則只更新每張的內容。
            var key = string.Join(",", Array.ConvertAll(orders, o => o.Id.ToString()));
            if (key != _orderListShown)
            {
                _orderListShown = key;
                _orderRows.Clear();
                var shown = new HashSet<Guid>();
                foreach (var order in orders)
                {
                    shown.Add(order.Id);
                    if (!_orderViews.TryGetValue(order.Id, out var view))
                    {
                        view = new OrderView(this, order.Id);
                        _orderViews.Add(order.Id, view);
                    }

                    _orderRows.Add(view.Root);
                }

                foreach (var id in new List<Guid>(_orderViews.Keys))
                {
                    if (!shown.Contains(id))
                    {
                        _orderViews.Remove(id);
                    }
                }

                if (orders.Length == 0)
                {
                    _orderRows.Add(Hint("還沒有訂單。"));
                }
            }

            foreach (var order in orders)
            {
                _orderViews[order.Id].Show(order, BinsOf(order, lots));
            }
        }

        /// <summary>這張訂單的框與箱子：工單的框以工單號碼當批次編號，出貨箱是出貨單號，送成品倉的箱子是訂單號碼。</summary>
        private string BinsOf(SalesOrderDto order, LotDto[] lots)
        {
            var batches = new HashSet<string> { order.Number };
            foreach (var line in order.Lines)
            {
                foreach (var workOrder in line.ProductionOrders)
                {
                    batches.Add(workOrder.Number);
                }
            }

            foreach (var delivery in order.Deliveries)
            {
                batches.Add(delivery.Number);
            }

            var text = new StringBuilder();
            var boxes = new List<LotDto>();
            foreach (var part in ProductParts.All)
            {
                var pieces = 0;
                var bins = 0;
                var places = new List<string>();
                var counts = new Dictionary<string, int>();
                foreach (var lot in lots)
                {
                    if (!batches.Contains(lot.BatchNumber) || lot.PartCode != part)
                    {
                        continue;
                    }

                    if (lot.IsBox)
                    {
                        boxes.Add(lot);
                        continue;
                    }

                    // 併進箱子的框已經空了，報廢的框另外補做，都不列。
                    if (lot.Status == LotStatus.Boxed || lot.Status == LotStatus.Scrapped)
                    {
                        continue;
                    }

                    pieces += lot.Quantity;
                    bins++;
                    var place = Describe(lot);
                    if (!counts.ContainsKey(place))
                    {
                        counts[place] = 0;
                        places.Add(place);
                    }

                    counts[place]++;
                }

                if (bins == 0)
                {
                    continue;
                }

                text.Append(text.Length > 0 ? "\n" : string.Empty).Append($"{ProductParts.Name(part)}　{pieces} 支／{bins} 框");
                foreach (var place in places)
                {
                    text.Append($"\n　{counts[place]} 框 {place}");
                }
            }

            foreach (var box in boxes)
            {
                text.Append(text.Length > 0 ? "\n" : string.Empty).Append($"箱 {box.LotNumber}　{ProductParts.Name(box.PartCode)} {box.Quantity} 支：{Describe(box)}");
            }

            return text.Length > 0 ? text.ToString() : "沒有在流動的框。";
        }

        private static string StatusText(SalesOrderStatus status)
        {
            switch (status)
            {
                case SalesOrderStatus.InProduction: return "生產中";
                case SalesOrderStatus.PartiallyShipped: return "已分批出貨";
                case SalesOrderStatus.Completed: return "全部做完，等貨車";
                case SalesOrderStatus.Shipped: return "已出貨";
                default: return status.ToString();
            }
        }

        private static string OrderText(SalesOrderDto order)
        {
            var text = new StringBuilder();
            text.Append(order.DueDate.HasValue ? $"交期 {order.DueDate.Value:yyyy-MM-dd}\n" : "交期：未指定\n");
            if (order.IsRush)
            {
                text.Append($"急單：{order.RushRequestedBy} 設定\n");
            }

            if (order.PartialApprovedBy != null)
            {
                text.Append($"分批出貨：{order.PartialApprovedBy} 已同意\n");
            }

            foreach (var line in order.Lines)
            {
                var unit = line.Kind == ItemKind.Set ? "組" : "支";
                var extra = line.Parts.Count > 0 ? line.Parts[0].Extra : 0;
                text.Append($"{line.LineNumber}. {line.ItemName} × {line.Quantity} {unit}");
                text.Append(extra > 0 ? $"（多做 {extra}）\n" : "\n");
                foreach (var part in line.Parts)
                {
                    text.Append($"　{ProductParts.Name(part.PartCode)}：完成 {part.Finished}／{part.Ordered}");
                    if (part.InProgress > 0)
                    {
                        text.Append($"　製作中 {part.InProgress}");
                    }

                    if (part.Scrapped > 0)
                    {
                        text.Append($"　報廢 {part.Scrapped}");
                    }

                    if (part.Makeup > 0)
                    {
                        text.Append($"　補做 {part.Makeup}");
                    }

                    if (part.Shipped > 0)
                    {
                        text.Append($"　已出貨 {part.Shipped}");
                    }

                    if (part.Surplus > 0)
                    {
                        text.Append($"　進成品倉 {part.Surplus}");
                    }

                    text.Append('\n');
                }

                var makeups = new List<string>();
                var main = string.Empty;
                foreach (var workOrder in line.ProductionOrders)
                {
                    if (workOrder.Kind == ProductionOrderKind.Main)
                    {
                        main = workOrder.Number;
                    }
                    else
                    {
                        makeups.Add($"{workOrder.Number.Substring(workOrder.Number.LastIndexOf('-') + 1)} {ProductParts.Name(workOrder.PartCode)} ×{workOrder.Quantity}");
                    }
                }

                text.Append($"　工單 {main}");
                text.Append(makeups.Count > 0 ? $"；補做 {string.Join("、", makeups)}\n" : "\n");
            }

            foreach (var delivery in order.Deliveries)
            {
                var pieces = 0;
                foreach (var line in delivery.Lines)
                {
                    pieces += line.Quantity;
                }

                text.Append($"出貨單 {delivery.Number}{(delivery.IsPartial ? "（分批）" : string.Empty)}：{delivery.Lines.Count} 箱 {pieces} 支，");
                text.Append(delivery.ShippedAt.HasValue ? "已出貨\n" : "等貨車\n");
            }

            return text.ToString().TrimEnd('\n');
        }

        private static TextField Text(string label, string value)
        {
            var field = new TextField(label) { value = value };
            StyleField(field);
            var input = field.Q(className: TextField.inputUssClassName);
            if (input != null)
            {
                input.style.color = Colors.InputText;
            }

            return field;
        }

        private static void StyleField(VisualElement field)
        {
            field.style.marginBottom = 3;
            var label = field.Q<Label>();
            if (label != null)
            {
                label.style.color = Color.white;
                label.style.minWidth = 110;
            }
        }

        /// <summary>一張訂單在面板上的區塊：只建立一次，之後只更新文字與按鈕是否顯示。</summary>
        private sealed class OrderView
        {
            private readonly Label _header;
            private readonly Label _details;
            private readonly Button _approve;
            private readonly Button _rush;
            private readonly Foldout _bins;
            private readonly Label _binText;
            private string _orderNumber = string.Empty;
            private bool _isRush;

            public OrderView(ProductionPanel panel, Guid orderId)
            {
                Root = new VisualElement();
                Root.style.marginBottom = 8;
                Root.style.paddingBottom = 6;
                Root.style.borderBottomWidth = 1;
                Root.style.borderBottomColor = new Color(1f, 1f, 1f, 0.12f);

                _header = new Label();
                _header.style.unityFontStyleAndWeight = FontStyle.Bold;
                _header.style.whiteSpace = WhiteSpace.Normal;
                Root.Add(_header);

                _details = new Label();
                _details.style.fontSize = 12;
                _details.style.whiteSpace = WhiteSpace.Normal;
                Root.Add(_details);

                var buttons = Row();
                _approve = ActionButton("同意分批出貨", () => panel.StartCoroutine(panel.ApprovePartial(orderId, _orderNumber)));
                buttons.Add(_approve);
                _rush = ActionButton("設為急單", () => panel.StartCoroutine(panel.ToggleRush(orderId, _orderNumber, _isRush)));
                buttons.Add(_rush);
                Root.Add(buttons);

                // 料框位置預設收合；展開與否在更新時保留。
                _bins = new Foldout { text = "料框位置", value = false };
                _bins.style.fontSize = 12;
                _binText = new Label();
                _binText.style.fontSize = 12;
                _binText.style.whiteSpace = WhiteSpace.Normal;
                _bins.Add(_binText);
                Root.Add(_bins);
            }

            public VisualElement Root { get; }

            public void Show(SalesOrderDto order, string bins)
            {
                _orderNumber = order.Number;
                _isRush = order.IsRush;
                var shipped = order.Status == SalesOrderStatus.Shipped;
                _header.text = $"{order.Number}　{order.Customer}　{StatusText(order.Status)}{(order.IsRush ? "　【急單】" : string.Empty)}";
                _header.style.color = shipped ? Colors.Muted : order.IsRush ? Colors.Rush : Color.white;
                _details.text = OrderText(order);
                _details.style.color = shipped ? Colors.Muted : Color.white;
                _binText.text = bins;

                // 還沒同意過、也還沒開始出貨、還在生產中的訂單才能同意分批；能不能出由後端再檢查一次。
                var approvable = order.PartialApprovedBy == null && order.Deliveries.Count == 0 && order.Status == SalesOrderStatus.InProduction;
                _approve.style.display = approvable ? DisplayStyle.Flex : DisplayStyle.None;

                // 還有東西在做的訂單才能設成急單或取消。
                var producing = order.Status == SalesOrderStatus.InProduction || order.Status == SalesOrderStatus.PartiallyShipped;
                _rush.style.display = producing ? DisplayStyle.Flex : DisplayStyle.None;
                _rush.text = order.IsRush ? "取消急單" : "設為急單";
            }
        }
    }
}
