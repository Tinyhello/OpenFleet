# OpenFleet Unity 顯示端

- P1：Windows 桌面版透過 MQTT 收到後端的車輛狀態、斷線後自動重連，並把背景執行緒收到的訊息切回主執行緒更新場景。
- P2：新增操作面板（UI Toolkit），可以修改示範設定、建立批次並查看批次目前的狀態與位置。

## 環境

| 項目 | 內容 |
| --- | --- |
| Unity | 6000.5.7f1、URP 17.5 |
| 目標平台 | Windows 桌面版（Mono） |
| MQTT client | MQTTnet 4.3.7 + MQTTnet.Extensions.ManagedClient 4.3.7（netstandard2.1 DLL，放在 `Assets/OpenFleet/Plugins`） |
| JSON | `com.unity.nuget.newtonsoft-json` 3.2.1，規則與後端相同（camelCase、enum 小寫字串） |
| 共用契約 | `OpenFleet.Contracts.dll`，由 `backend/src/OpenFleet.Contracts` 建置 |

## 檔案

| 位置 | 用途 |
| --- | --- |
| `Assets/OpenFleet/Scripts/FleetMqttClient.cs` | 連線 Broker、訂閱 `state/vehicles/+`；背景執行緒解析 JSON 後放進佇列，由 `Update` 在主執行緒發出事件 |
| `Assets/OpenFleet/Scripts/FleetView.cs` | 每次連上 Broker 就取一次 HTTP 快照；依 `vehicleType` 選用 AGV Prefab；簡易面板可送 Ping、跟隨車輛 |
| `Assets/OpenFleet/Scripts/VehicleView.cs` | 單台車的顯示（掛在 Prefab 上）：依 `stateEpoch` + `version` 丟棄舊狀態，位置插值、燈條與警示燈依連線狀態變色 |
| `Assets/OpenFleet/Scripts/FactoryCameraController.cs` | 攝影機：右鍵旋轉、中鍵／WASD 平移、滾輪縮放、F 重設、跟隨車輛 |
| `Assets/OpenFleet/Scripts/ProductionPanel.cs` | P2 操作面板（UI Toolkit，介面用程式建立）：示範設定、建立批次、批次清單。只呼叫後端 HTTP API |
| `Assets/OpenFleet/Scripts/StatusOverlay.cs` | 場景物件上的進度條（UI Toolkit，螢幕座標）：機台的備料、成品、加工與可收合的刀具列；車輛的上下料進度 |
| `Assets/OpenFleet/Scripts/StationAnchor.cs` | 場景中的站點與後端站點代碼的對應；進度條的位置、備料區／成品區有料時要顯示的物件 |
| `Assets/OpenFleet/Scripts/Billboard.cs` | 文字標籤面向攝影機 |
| `Assets/OpenFleet/Editor/FactorySceneBuilder.cs` | 選單 **OpenFleet → Build Factory Scene**：產生材質、AGV Prefab 與 `Factory.unity`（會覆寫） |
| `Assets/OpenFleet/Editor/PlayerBuilder.cs` | 選單 **OpenFleet → Build Windows Player**：重建場景並建置 `Builds/Windows/OpenFleet.exe` |
| `Assets/OpenFleet/UI/` | UI Toolkit 的 PanelSettings 與主題（由 Build Factory Scene 產生） |
| `Assets/OpenFleet/Editor/FactoryLayout.cs` | 暫定佈局：站點位置、環狀走道（須與模擬器 `Simulator:Route` 相同） |
| `Assets/OpenFleet/Editor/StationModels.cs`、`VehicleModels.cs` | 用基本幾何組裝的站點與 AGV 模型 |
| `Assets/OpenFleet/Editor/Palette.cs` | 所有材質顏色 |
| `Assets/OpenFleet/Scenes/Factory.unity` | 工廠場景（由選單產生，不要手動修改） |
| `Assets/OpenFleet/Scripts/FleetJson.cs` | Newtonsoft 設定，對應後端 `JsonDefaults` |
| `Assets/OpenFleet/Scenes/FleetP1.unity` | 舊的 P1 驗證場景，已由 `Factory.unity` 取代；沒有 Prefab 參考，執行會出錯，待刪除 |
| `sync-contracts.ps1` | 修改契約後重新建置並複製 DLL 到 Unity |

## 執行

1. 啟動 Broker、後端與模擬器（見 [backend/README.md](../backend/README.md)）。後端要用 `--launch-profile http`（`http://localhost:5229`）。
2. 在 Unity 開啟 `Assets/OpenFleet/Scenes/Factory.unity` 按 Play，或 build Windows 版後執行。
3. 左上面板顯示每台車的車型、連線狀態、`version` 與最近一次指令的回報；車上燈條綠色為 online、灰色為 offline。
4. 右上是操作面板：
   - **示範設定**（預設收合，點標題展開）：用滑桿（或輸入框）調整批量、上下料每件秒數、換刀時間、各站的每件加工秒數與刀具壽命，按「套用」存進後端。超出範圍時後端會拒絕，原因顯示在面板上。
   - **建立批次**：在原料倉建立一個批次，`agv-01` 會把它送到粗加工、精加工，最後送到淬火／回火站入口暫存。清單每秒更新一次。
   - **清除示範資料**：刪除所有批次與搬運紀錄，設定保留。

5. 場景上的顯示：
   - **車輛**：標籤顯示「空車」或「載 N 件」，載貨時車上有料箱；上料、下料時車輛上方有進度條。載貨越多車速越慢。
   - **機台**（粗加工、精加工）：下方有備料、成品、加工三列進度條；前方右側的台子是備料區、左側是成品區，有料時台上才有料箱。按「刀具 ▼」展開刀具已加工件數；換刀時加工列會變成紅色的「換刀」。
   - **原料倉、暫存點**：有料時顯示件數。

中文輸入法開啟時，數字鍵會被輸入法攔截，輸入框打不進數字；請用滑桿，或先切到英數模式。

修改模型、顏色或佈局後，執行選單 **OpenFleet → Build Factory Scene** 重建場景。

## 場景內容

- 暫定佈局（每道工序一站）：下排由左到右為原料倉、粗加工、精加工、淬火／回火、電鍍；上排由右到左為拋光、品管、包裝、出貨、刀具庫。走道內側是充電維護區與載具存放區。
- 每站前方的青色方框是交接點。走道只畫邊線，沒有方向箭頭，因為交通規則尚未決定。
- 車輛平常停在充電維護區的車位，有搬運任務才沿走道移動；沒有任何交通規則，車輛之間不會避讓。
- 進度條是固定大小的螢幕元件，畫面拉遠時會顯得比機台大；加工進度逐件跳動，不是平滑移動。
- AGV：大型車（橘色頂升平台）、小型車（白色車身、刀把架）、未知車型（紫色，表示後端設定缺漏）。

連線設定（host、port、factoryId、API 位址）在場景中 `OpenFleet` 物件的 Inspector 調整。

注意：Editor 不在前景時，Play Mode 幾乎不會更新畫面。要在背景持續驗證，請改用 build 出來的 Player（已開啟 Run In Background）。

## 快照與 MQTT 的對齊

- 訂閱在先、HTTP 快照在後；兩者重疊的部分由 `version` 比對去掉，不會漏掉中間的更新。
- `version` 只存在後端記憶體，後端重啟就從 0 重新計算。因此狀態另外帶 `stateEpoch`（後端每次啟動產生新值）：epoch 相同才比較 version，不同就直接採用新狀態。
