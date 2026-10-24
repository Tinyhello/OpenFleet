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
    /// 操作面板的「排程」頁（P6）：讀 GET /api/schedule，列出每張訂單預估什麼時候包完、出貨、趕不趕得上交期，
    /// 每台機台接下來做哪幾框，每座爐接下來開哪幾爐。預估由後端推算，這裡只顯示。
    /// 預設看正常速度的預估（交期只跟它比）；測試模式開著時可以切到加速的預估，切換時整頁一起換色（使用者 2026-10-23）。
    /// </summary>
    public sealed partial class ProductionPanel
    {
        private const int BinsShownPerMachine = 3;
        private const int RunsShownPerFurnace = 2;

        private readonly List<Label> _scheduleHeadings = new List<Label>();
        private readonly List<Label> _scheduleTexts = new List<Label>();
        private VisualElement _operationsPage;
        private VisualElement _schedulePage;
        private Button _operationsTab;
        private Button _scheduleTab;
        private Button _normalMode;
        private Button _acceleratedMode;
        private Label _scheduleHint;
        private Label _scheduleAssumptions;
        private Label _scheduleOrders;
        private Label _scheduleMachines;
        private Label _scheduleFurnaces;
        private ScheduleDto _schedule;
        private bool _scheduleVisible;
        private bool _showAccelerated;

        private void BuildSchedulePage(VisualElement page)
        {
            var s = page.style;
            s.paddingTop = s.paddingBottom = 6;
            s.paddingLeft = s.paddingRight = 8;
            s.borderTopWidth = s.borderBottomWidth = s.borderLeftWidth = s.borderRightWidth = 1;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 4;

            var modes = Row();
            modes.style.marginTop = 0;
            _normalMode = ActionButton("正常速度", () => SetScheduleMode(false));
            modes.Add(_normalMode);
            _acceleratedMode = ActionButton("加速（測試模式）", () => SetScheduleMode(true));
            modes.Add(_acceleratedMode);
            page.Add(modes);

            _scheduleHint = Hint("尚未讀到排程。");
            _scheduleHint.style.marginTop = 4;
            page.Add(_scheduleHint);

            _scheduleOrders = AddScheduleSection(page, "訂單");
            _scheduleMachines = AddScheduleSection(page, "機台");
            _scheduleFurnaces = AddScheduleSection(page, "爐");

            _scheduleAssumptions = Hint("預估假設品管全部合格、隨時有空車（搬運一趟＝交接兩次加上路程，車速 1 m/s）、不算分批出貨；" +
                "「趕得上」是預估出貨的時間在交期那一天結束之前。");
            _scheduleAssumptions.style.marginTop = 8;
            page.Add(_scheduleAssumptions);

            ApplySchedulePalette();
        }

        private Label AddScheduleSection(VisualElement page, string title)
        {
            var heading = Heading(title);
            _scheduleHeadings.Add(heading);
            page.Add(heading);

            var text = new Label();
            text.style.fontSize = 13;
            text.style.whiteSpace = WhiteSpace.Normal;
            text.enableRichText = true;
            _scheduleTexts.Add(text);
            page.Add(text);
            return text;
        }

        /// <summary>切換「操作」與「排程」兩頁；切到排程時馬上讀一次，之後每秒更新。</summary>
        private void ShowPage(bool schedule)
        {
            _scheduleVisible = schedule;
            _operationsPage.style.display = schedule ? DisplayStyle.None : DisplayStyle.Flex;
            _schedulePage.style.display = schedule ? DisplayStyle.Flex : DisplayStyle.None;
            Highlight(_operationsTab, !schedule, Colors.Selected);
            Highlight(_scheduleTab, schedule, Colors.Selected);
            if (_panelScroll != null)
            {
                _panelScroll.scrollOffset = Vector2.zero;
            }

            if (schedule && isActiveAndEnabled)
            {
                StartCoroutine(RefreshSchedule());
            }
        }

        private void SetScheduleMode(bool accelerated)
        {
            _showAccelerated = accelerated;
            ApplySchedulePalette();
            ShowSchedule();
        }

        /// <summary>整頁的顏色一起換：底色、外框、標題、文字、按鈕的選取色，以及內容裡的急單、趕得上、趕不上。</summary>
        private void ApplySchedulePalette()
        {
            var palette = Palette;
            var s = _schedulePage.style;
            s.backgroundColor = palette.Back;
            s.borderTopColor = s.borderBottomColor = s.borderLeftColor = s.borderRightColor = palette.Border;
            foreach (var heading in _scheduleHeadings)
            {
                heading.style.color = palette.Heading;
            }

            foreach (var text in _scheduleTexts)
            {
                text.style.color = palette.Text;
            }

            _scheduleHint.style.color = palette.Muted;
            _scheduleAssumptions.style.color = palette.Muted;
            Highlight(_normalMode, !_showAccelerated, Colors.Selected);
            Highlight(_acceleratedMode, _showAccelerated, palette.Border);
        }

        private PresentationController.SchedulePalette Palette => _showAccelerated ? Colors.ScheduleAccelerated : Colors.ScheduleNormal;

        private static void Highlight(Button button, bool selected, Color color)
        {
            button.style.backgroundColor = selected ? color : new StyleColor(StyleKeyword.Null);
            button.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
        }

        private IEnumerator RefreshSchedule()
        {
            using (var request = UnityWebRequest.Get($"{Api}/api/schedule"))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _scheduleHint.text = $"讀取排程失敗：{request.error}";
                    yield break;
                }

                _schedule = FleetJson.Deserialize<ScheduleDto>(request.downloadHandler.text);
            }

            ShowSchedule();
        }

        private void ShowSchedule()
        {
            if (_schedule == null)
            {
                return;
            }

            // 測試模式關掉之後就沒有加速的預估，回到正常速度。
            _acceleratedMode.SetEnabled(_schedule.Accelerated != null);
            if (_showAccelerated && _schedule.Accelerated == null)
            {
                _showAccelerated = false;
                ApplySchedulePalette();
            }

            var accelerated = _showAccelerated;
            var forecast = accelerated ? _schedule.Accelerated : _schedule.Normal;
            _scheduleHint.text = accelerated
                ? "加速：照目前的測試倍率推算，看示範大約多久做完；不跟交期比。"
                : "正常速度：不加速推算，交期只跟這一份比。" + (_schedule.TestMode ? "測試模式開著，可以切到「加速」看示範大約多久做完。" : string.Empty);
            _scheduleOrders.text = OrdersForecastText(forecast, accelerated);
            _scheduleMachines.text = MachinesForecastText(forecast, accelerated);
            _scheduleFurnaces.text = FurnacesForecastText(forecast, accelerated);
        }

        private string OrdersForecastText(ScheduleForecastDto forecast, bool accelerated)
        {
            if (forecast.Orders.Count == 0)
            {
                return "沒有還沒出貨的訂單。";
            }

            var palette = Palette;
            var text = new StringBuilder();
            foreach (var order in forecast.Orders)
            {
                // 訂單之間空一行，比較好分辨。
                text.Append(text.Length > 0 ? "\n\n" : string.Empty);
                if (order.IsRush)
                {
                    text.Append(Paint("【急單】", palette.Rush));
                }

                text.Append($"{order.Number}　{order.Customer}\n");
                if (order.Blocker != ForecastBlocker.None)
                {
                    text.Append(Paint($"　算不出來：{BlockerText(order.Blocker, order.BlockedLotNumbers.Count)}", palette.Blocked));
                }
                else if (order.Finished)
                {
                    text.Append($"　已包裝完，出貨 {When(order.ShipAt, accelerated)}");
                }
                else
                {
                    text.Append($"　包完 {When(order.FinishAt, accelerated)}，出貨 {When(order.ShipAt, accelerated)}");
                }

                text.Append('\n');
                if (!order.DueDate.HasValue)
                {
                    text.Append(Paint("　交期：未指定", palette.Muted));
                }
                else if (accelerated)
                {
                    text.Append(Paint($"　交期 {order.DueDate.Value:MM/dd}（加速時不比較）", palette.Muted));
                }
                else
                {
                    text.Append($"　交期 {order.DueDate.Value:MM/dd}：");
                    text.Append(order.OnTime == true ? Paint("趕得上", palette.OnTime)
                        : order.OnTime == false ? Paint("趕不上", palette.Late)
                        : Paint("算不出來", palette.Blocked));
                }
            }

            return text.ToString();
        }

        private string MachinesForecastText(ScheduleForecastDto forecast, bool accelerated)
        {
            var palette = Palette;
            var machines = new List<MachineScheduleDto>(forecast.Machines);
            // 依製程的順序排（切料、粗加工……包裝）；讀不到設定時照後端的順序。
            machines.Sort((a, b) => (a.StepSequence ?? int.MaxValue).CompareTo(b.StepSequence ?? int.MaxValue));
            var text = new StringBuilder();
            foreach (var machine in machines)
            {
                text.Append(text.Length > 0 ? "\n" : string.Empty);
                if (machine.Bins.Count == 0)
                {
                    text.Append($"{machine.Name}　").Append(Paint("沒有排到的框", palette.Muted));
                    continue;
                }

                var items = new List<string>();
                for (var i = 0; i < machine.Bins.Count && i < BinsShownPerMachine; i++)
                {
                    var bin = machine.Bins[i];
                    var label = ShortLot(bin.LotNumber) + (bin.Running ? "（做中）" : bin.Changeover ? "（先換線）" : string.Empty);
                    items.Add(bin.IsRush ? Paint(label, palette.Rush) : label);
                }

                text.Append($"{machine.Name}　{string.Join(" → ", items)}");
                if (machine.Bins.Count > BinsShownPerMachine)
                {
                    text.Append($" …共 {machine.Bins.Count} 框");
                }

                text.Append(Paint($"\n　做到 {When(machine.Bins[machine.Bins.Count - 1].EndAt, accelerated)}", palette.Muted));
            }

            return text.ToString();
        }

        private string FurnacesForecastText(ScheduleForecastDto forecast, bool accelerated)
        {
            var palette = Palette;
            var text = new StringBuilder();
            foreach (var furnace in forecast.Furnaces)
            {
                text.Append(text.Length > 0 ? "\n" : string.Empty).Append(furnace.Name);
                if (furnace.Runs.Count == 0 && furnace.StrandedPieces == 0)
                {
                    text.Append("　").Append(Paint("沒有排到的爐", palette.Muted));
                    continue;
                }

                for (var i = 0; i < furnace.Runs.Count && i < RunsShownPerFurnace; i++)
                {
                    var run = furnace.Runs[i];
                    text.Append($"\n　{(run.Running ? "進行中" : i == 0 ? "下一爐" : "再下一爐")} {run.Pieces} 支，{Span(run, accelerated)}");
                }

                if (furnace.Runs.Count > RunsShownPerFurnace)
                {
                    text.Append($"\n　…共 {furnace.Runs.Count} 爐，最後一爐 {When(furnace.Runs[furnace.Runs.Count - 1].EndAt, accelerated)} 出爐");
                }

                if (furnace.StrandedPieces > 0)
                {
                    text.Append("\n").Append(Paint($"　爐前湊不滿一爐：{furnace.StrandedPieces} 支（{furnace.StrandedLotNumbers.Count} 框）", palette.Blocked));
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// 正常速度顯示成本機的日期與時間；加速顯示成從現在起還要多久（以後端算預估的時間為準，不受兩邊時鐘差影響）。
        /// </summary>
        private string When(DateTimeOffset? at, bool accelerated)
        {
            if (!at.HasValue)
            {
                return "—";
            }

            if (!accelerated)
            {
                return at.Value.ToLocalTime().ToString("MM/dd HH:mm");
            }

            var seconds = (at.Value - _schedule.GeneratedAt).TotalSeconds;
            return seconds <= 0 ? "現在" : $"約 {Seconds(seconds)}後";
        }

        /// <summary>一爐的時間：正常速度是開始～結束；加速是還要多久開始、多久出爐。</summary>
        private string Span(ScheduledBatchRunDto run, bool accelerated)
        {
            if (!accelerated)
            {
                var start = run.StartAt.ToLocalTime();
                var end = run.EndAt.ToLocalTime();
                return $"{start:MM/dd HH:mm}～{(end.Date == start.Date ? end.ToString("HH:mm") : end.ToString("MM/dd HH:mm"))}";
            }

            return run.Running ? $"{When(run.EndAt, true)}出爐" : $"{When(run.StartAt, true)}開爐，{When(run.EndAt, true)}出爐";
        }

        private static string Seconds(double seconds)
        {
            var total = Mathf.Max(1, Mathf.RoundToInt((float)seconds));
            return total < 120 ? $"{total} 秒" : $"{total / 60} 分 {total % 60} 秒";
        }

        /// <summary>框號去掉開頭的「WO-日期-」，例如 WO-20261023-A61933-1-U01 → A61933-1-U01。</summary>
        private static string ShortLot(string lotNumber)
        {
            var parts = lotNumber.Split('-');
            return parts.Length > 3 ? string.Join("-", parts, 2, parts.Length - 2) : lotNumber;
        }

        private static string BlockerText(ForecastBlocker blocker, int bins)
        {
            switch (blocker)
            {
                case ForecastBlocker.WaitingForMaterial: return $"等料，{bins} 框的圓棒不夠";
                case ForecastBlocker.WaitingForFurnaceFill: return $"等湊爐，{bins} 框在爐前湊不到開爐的支數，要等別的訂單一起開爐";
                default: return blocker.ToString();
            }
        }

        private static string Paint(string text, Color color) => $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{text}</color>";
    }
}
