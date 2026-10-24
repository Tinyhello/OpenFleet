using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using OpenFleet.Contracts;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenFleet.Unity
{
    /// <summary>沒有回傳內容的呼叫（刪除）用的型別。</summary>
    public sealed class Nothing
    {
    }

    /// <summary>
    /// 一次佈局 API 呼叫的結果。2xx 時 <see cref="Value"/> 有值；失敗時 <see cref="Message"/> 是可以直接顯示的原因。
    /// 套用佈局（POST /api/layouts/{id}/apply）失敗時另有兩種內容：400 帶檢查結果（<see cref="Validation"/>）、
    /// 409 帶在製品的說明（<see cref="Busy"/>），呼叫端依此決定要不要問「清空示範資料並套用」。
    /// </summary>
    public sealed class LayoutResponse<T>
    {
        /// <summary>HTTP 狀態碼；連不上後端時是 0。</summary>
        public long Status { get; set; }

        public bool Ok => Status >= 200 && Status < 300;

        /// <summary>連線失敗（後端沒開、逾時）。</summary>
        public bool Unreachable => Status == 0;

        public T Value { get; set; }

        public LayoutValidationDto Validation { get; set; }

        public LayoutBusyDto Busy { get; set; }

        public string Message { get; set; }
    }

    /// <summary>
    /// 佈局的 HTTP 呼叫（P7 規劃 5.11 的每個端點）。方法都是 coroutine，結果用 callback 交回；
    /// 呼叫端用自己的 MonoBehaviour 執行（<c>StartCoroutine(client.GetActive(r =&gt; ...))</c>）。
    /// JSON 規則與後端相同（<see cref="FleetJson"/>）。
    /// </summary>
    public sealed class LayoutClient
    {
        private const int TimeoutSeconds = 10;

        private readonly Func<string> _baseUrl;

        /// <param name="baseUrl">後端 API 的位址（例如 http://localhost:5229）；每次呼叫時才取，位址之後改了也跟得上。</param>
        public LayoutClient(Func<string> baseUrl)
        {
            _baseUrl = baseUrl;
        }

        /// <summary>所有佈局（不含文件），預設佈局排第一。</summary>
        public IEnumerator List(Action<LayoutResponse<List<LayoutSummaryDto>>> done) =>
            Send(UnityWebRequest.kHttpVerbGET, "/api/layouts", null, done);

        /// <summary>使用中的佈局；後端還沒有使用中的佈局時回 404。</summary>
        public IEnumerator GetActive(Action<LayoutResponse<LayoutDto>> done) =>
            Send(UnityWebRequest.kHttpVerbGET, "/api/layouts/active", null, done);

        public IEnumerator Get(Guid id, Action<LayoutResponse<LayoutDto>> done) =>
            Send(UnityWebRequest.kHttpVerbGET, $"/api/layouts/{id}", null, done);

        /// <summary>另存新佈局。有錯誤的佈局也可以存（結果的 Validation 列出問題），只有結構錯誤、名稱不合法才回 400。</summary>
        public IEnumerator Create(string name, FactoryLayoutDocument document, Action<LayoutResponse<SaveLayoutResponse>> done) =>
            Send(UnityWebRequest.kHttpVerbPOST, "/api/layouts", new SaveLayoutRequest { Name = name, Document = document }, done);

        /// <summary>覆蓋儲存；預設佈局不能改（回 400）。</summary>
        public IEnumerator Update(Guid id, string name, FactoryLayoutDocument document, Action<LayoutResponse<SaveLayoutResponse>> done) =>
            Send(UnityWebRequest.kHttpVerbPUT, $"/api/layouts/{id}", new SaveLayoutRequest { Name = name, Document = document }, done);

        /// <summary>刪除；使用中與預設的佈局不能刪（回 400）。</summary>
        public IEnumerator Delete(Guid id, Action<LayoutResponse<Nothing>> done) =>
            Send(UnityWebRequest.kHttpVerbDELETE, $"/api/layouts/{id}", null, done);

        /// <summary>只驗證、不存檔；編輯時即時顯示問題，結果和後端套用前的檢查相同。</summary>
        public IEnumerator Validate(FactoryLayoutDocument document, Action<LayoutResponse<LayoutValidationDto>> done) =>
            Send(UnityWebRequest.kHttpVerbPOST, "/api/layouts/validate", document, done);

        /// <summary>
        /// 套用佈局。有在製品而且 <paramref name="resetDemo"/> 是 false 時回 409（Busy 有說明）；
        /// 佈局有錯誤時回 400（Validation 有問題清單）。
        /// </summary>
        public IEnumerator Apply(Guid id, bool resetDemo, Action<LayoutResponse<LayoutDto>> done) =>
            Send(UnityWebRequest.kHttpVerbPOST, $"/api/layouts/{id}/apply?resetDemo={(resetDemo ? "true" : "false")}", null, done);

        private IEnumerator Send<T>(string method, string path, object body, Action<LayoutResponse<T>> done)
        {
            var response = new LayoutResponse<T>();
            using (var request = new UnityWebRequest(_baseUrl() + path, method) { downloadHandler = new DownloadHandlerBuffer(), timeout = TimeoutSeconds })
            {
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(FleetJson.Serialize(body)));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                yield return request.SendWebRequest();

                response.Status = request.result == UnityWebRequest.Result.ConnectionError ? 0 : request.responseCode;
                var text = request.downloadHandler?.text;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    if (typeof(T) == typeof(Nothing))
                    {
                        response.Value = (T)(object)new Nothing();
                    }
                    else if (!string.IsNullOrEmpty(text))
                    {
                        try
                        {
                            response.Value = FleetJson.Deserialize<T>(text);
                        }
                        catch (Exception ex)
                        {
                            // 內容讀不懂：當作失敗，不要交出半成品。
                            response.Status = 500;
                            response.Message = "後端回應的內容讀不懂：" + ex.Message;
                        }
                    }
                }
                else
                {
                    Describe(response, request, text);
                }
            }

            done(response);
        }

        private static void Describe<T>(LayoutResponse<T> response, UnityWebRequest request, string text)
        {
            if (response.Status == 0)
            {
                response.Message = "連不上後端：" + request.error;
                return;
            }

            if (response.Status == 400)
            {
                if (TryParse<LayoutValidationDto>(text, out var validation) && validation.Issues != null && validation.Issues.Count > 0)
                {
                    response.Validation = validation;
                    response.Message = "佈局有錯誤：" + validation.Issues[0].Message;
                    return;
                }

                response.Message = ValidationMessage(text) ?? request.error;
                return;
            }

            if (response.Status == 409 && TryParse<LayoutBusyDto>(text, out var busy) && !string.IsNullOrEmpty(busy.Message))
            {
                response.Busy = busy;
                response.Message = busy.Message;
                return;
            }

            response.Message = (TryParse<ProblemBody>(text, out var problem) ? problem.Detail ?? problem.Title : null) ?? request.error;
        }

        /// <summary>ValidationProblem（400）：把每個欄位的說明接起來。</summary>
        private static string ValidationMessage(string text)
        {
            if (!TryParse<ProblemBody>(text, out var problem))
            {
                return null;
            }

            var messages = new List<string>();
            if (problem.Errors != null)
            {
                foreach (var pair in problem.Errors)
                {
                    messages.AddRange(pair.Value);
                }
            }

            return messages.Count > 0 ? string.Join(" ", messages) : problem.Detail ?? problem.Title;
        }

        private static bool TryParse<T>(string text, out T value) where T : class
        {
            value = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                value = FleetJson.Deserialize<T>(text);
            }
            catch (Exception)
            {
                return false;
            }

            return value != null;
        }

        /// <summary>ASP.NET Core 的 ProblemDetails／ValidationProblem 回應格式（只取要顯示的欄位）。</summary>
        private sealed class ProblemBody
        {
            public string Title { get; set; }
            public string Detail { get; set; }
            public Dictionary<string, string[]> Errors { get; set; }
        }
    }
}
