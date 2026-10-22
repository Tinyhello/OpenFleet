# OpenFleet Unity 顯示端

- P1：Windows 桌面版透過 MQTT 收到後端的車輛狀態、斷線後自動重連，並把背景執行緒收到的訊息切回主執行緒更新場景。
- P2：新增操作面板（UI Toolkit），可以修改示範設定、建立批次並查看批次目前的狀態與位置。
- P3：兩條車道與路徑虛線；面板顯示缺車耽誤的時間；Prefab 與 UI 設定改由程式從 Resources 載入，場景裡的腳本不在 Inspector 掛參考。

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
| `Assets/OpenFleet/Scripts/PresentationController.cs` | 表演用的控制器：從 Resources 載入 AGV Prefab 與 UI 設定、建立車輛與兩個 UI 面板、找到場景中的站點與攝影機；畫面上的顏色集中在它的 `Colors` |
| `Assets/OpenFleet/Scripts/FleetView.cs` | 每次連上 Broker 就取一次 HTTP 快照，把車輛狀態交給對應的車輛物件；簡易面板可送 Ping、跟隨車輛 |
| `Assets/OpenFleet/Scripts/VehicleView.cs` | 單台車的顯示（掛在 Prefab 上）：依 `stateEpoch` + `version` 丟棄舊狀態，位置插值、燈條與警示燈依連線狀態變色。燈條、標籤、貨物依子物件名稱找到 |
| `Assets/OpenFleet/Scripts/FactoryCameraController.cs` | 攝影機：右鍵旋轉、中鍵／WASD 平移、滾輪縮放、F 重設、跟隨車輛 |
| `Assets/OpenFleet/Scripts/ProductionPanel.cs` | 操作面板（UI Toolkit，介面用程式建立）：示範設定、測試模式、缺車耽誤、建立批次、批次清單。只呼叫後端 HTTP API |
| `Assets/OpenFleet/Scripts/StatusOverlay.cs` | 場景物件上的進度條（UI Toolkit，螢幕座標）：機台的備料、成品、加工與可收合的刀具列；車輛與站點交接的進度；車輛前往目的地的流動虛線 |
| `Assets/OpenFleet/Scripts/StationAnchor.cs` | 場景中的站點與後端站點代碼的對應（物件名稱＝站點代碼，執行時由控制器加上）；進度條的位置、備料區／成品區有料時要顯示的物件 |
| `Assets/OpenFleet/Scripts/Billboard.cs` | 文字標籤面向攝影機 |
| `Assets/OpenFleet/Editor/FactorySceneBuilder.cs` | 選單 **OpenFleet → Build Factory Scene**：產生材質、`Resources` 底下的 AGV Prefab 與 UI 設定、`Factory.unity`（會覆寫） |
| `Assets/OpenFleet/Editor/PlayerBuilder.cs` | 選單 **OpenFleet → Build Windows Player**：重建場景並建置 `Builds/Windows/OpenFleet.exe`（Development build）。**Build Windows Player (Release)** 輸出到同一個位置，畫面右下角沒有 Development Build 字樣，錄影、展示用 |
| `Assets/OpenFleet/Resources/Prefabs/` | AGV Prefab（`AGV_Large`、`AGV_Small`、`AGV_Unknown`），執行時以 `Resources.Load` 載入 |
| `Assets/OpenFleet/Resources/UI/` | UI Toolkit 的 PanelSettings 與主題（由 Build Factory Scene 產生），執行時以 `Resources.Load` 載入 |
| `Assets/OpenFleet/Editor/FactoryLayout.cs` | 暫定佈局：站點位置、內外兩條車道（須與後端的示範路網 `DemoRoadNetwork` 相同） |
| `Assets/OpenFleet/Editor/StationModels.cs`、`VehicleModels.cs` | 站點與 AGV：擺放 Blender 模型，並產生執行時會切換的物件（料箱、狀態燈、標籤） |
| `Assets/OpenFleet/Models/*.fbx`、`Editor/ModelImport.cs` | Blender 匯出的模型與匯入設定；材質依名稱換成 `Palette` 的材質 |
| `Blender/` | 模型的原始檔（`Models/*.blend`）與產生模型的腳本（`scripts/*.py`、`ofkit.py`）；不在 `Assets` 底下，Unity 不會匯入 |
| `Assets/OpenFleet/Editor/Palette.cs` | 模型的材質顏色（建場景時使用）；執行時會變化的顏色在 `PresentationController.Colors` |
| `Assets/OpenFleet/Scenes/Factory.unity` | 工廠場景（由選單產生，不要手動修改） |
| `Assets/OpenFleet/Scripts/FleetJson.cs` | Newtonsoft 設定，對應後端 `JsonDefaults` |
| `sync-contracts.ps1` | 修改契約後重新建置並複製 DLL 到 Unity |

## 執行

1. 啟動 Broker、後端與模擬器（見 [backend/README.md](../backend/README.md)）。後端要用 `--launch-profile http`（`http://localhost:5229`）。
2. 在 Unity 開啟 `Assets/OpenFleet/Scenes/Factory.unity` 按 Play，或 build Windows 版後執行。
3. 左上面板顯示每台車的車型、連線狀態、`version` 與最近一次指令的回報；車上燈條綠色為 online、灰色為 offline。
4. 右上是操作面板：
   - **示範設定**（預設收合，點標題展開）：用滑桿（或輸入框）調整批量、機台取放每件秒數、AGV 交接一框的秒數、換刀時間、叫刀門檻、各站的每件加工秒數與刀具壽命，按「套用」存進後端。超出範圍時後端會拒絕，原因顯示在面板上。
   - **測試模式**：一顆開關，按下立刻生效。開啟時每一站依自己的「測試倍率」加快（倍率在示範設定裡每一站各一欄，預設都是 3000 倍），進度條上的時間仍是原本的長度，只是走得比較快。車輛的 AGV 交接（預設 3000 倍）與車速（空車、載貨預設都是 5 倍）也在測試模式下加快。這組預設讓面板預設的 30 組一張單約 2.5 分鐘從下單走到出貨。沒有整體的時間加速。
   - **需要人工處理**（平常不顯示）：搬運失敗、而且系統不能自己重派的任務（物料可能在車上）。到現場確認後按「已放回取料站」或「已送到卸料站」。
   - **重大事故**（平常不顯示）：某個車型全部離線時，以紅字顯示在「缺車耽誤」上方；事故結束後留下累計時間。
   - **品管**：累計檢了幾支、合格／反工／報廢各幾支，以及表面、鍍層、尺寸三種不良的支數。不良率與反工上限在示範設定裡。
   - **出貨**：下一班貨車還要多久、已包裝在等同批次到齊的支數、在出貨站等貨車的箱數、累計出貨。貨車間隔在示範設定裡。
   - **缺車耽誤**：車都在忙、叫不到車時，此刻有幾個任務在等、任務等車的累計，以及各工序機台因此停下來的時間（等料／成品未搬走／等刀）。不含離線事故。時間是實際時間，顯示成「分:秒」；測試模式下機台比車快很多，這裡的數字會偏高。
   - **建立批次 Φ30／Φ24**：在原料倉建立一個批次（數量是示範設定的「批量」，單位是組，最多 300 組）。一組是上沖、下沖、中模各一，三種零件各自分框：Φ30 一框 30 支、Φ24 一框 45 支（中模一律 30 個），AGV 一趟送一框。每一框依序經過粗加工、精加工、淬火、回火、電鍍、拋光、品管、包裝；品管判不良的另外裝框送回拋光或電鍍反工，報廢的自動從原料倉補做。同一種零件的框全部包裝完併成一箱送到出貨站；三種零件的箱子到齊後，貨車下一班來的時候一起載走。清單每秒更新一次，每個批次一段，依零件列出幾框在哪裡。
   - **清除示範資料**：刪除所有批次與搬運紀錄，設定保留。

5. 場景上的顯示：
   - **車輛**：標籤顯示「空車」、「載 N 件」或「載刀具」，大型車載貨時車上有料箱；和站點交接時車輛上方有「取貨／交貨」進度條。載貨越多車速越慢。小型車只送刀具。
   - **路徑**：黃色的流動虛線從車輛沿車道畫到目的地（圓圈）。亮的部分是後端已經放行、車可以開過去的路；暗的部分已經規劃好，但還在等路段預約（例如前面有車）。上料時會預先以暗色顯示接下來要去的卸料點。
   - **機台**（粗加工、精加工）：下方有備料、成品、加工三列進度條；前方右側的台子是備料區、左側是成品區，有料時台上才有料箱。按「刀具 ▼」展開刀具已加工件數；換刀時加工列會變成紅色的「換刀」；機台因為車都在忙而停著時，加工列變成桃紅色的「缺車」（等料／待搬走／等刀）；車輛全部離線造成的停機則是紅色的「離線」；刀具到壽命、新刀還在路上時是橘色的「等刀」。刀具按鈕上會標出「新刀配送中／新刀已到」。
   - **淬火、回火、電鍍**（整爐工序）：三列分別是「集料」（站內累積的支數／開爐下限，滿了才開）、「完成」（做完等搬走的支數）、這一爐的進度與剩餘的時間（原本的工序時間；測試模式下走得比較快）。
   - **粗加工、精加工**接著做的那一框桿徑不同時，加工列會先顯示「換線」與進度。
   - **拋光**：和車床一樣逐件加工，但沒有刀具列。
   - **原料倉、暫存點**：有料時顯示件數。

錄影或截圖時用 `OpenFleet.exe -hide-panels` 啟動：不顯示左上的車輛面板與右邊的操作面板，機台與車輛上的進度條照常顯示。這時批次要用 API 建立（見 backend/README.md）。

中文輸入法開啟時，數字鍵會被輸入法攔截，輸入框打不進數字；請用滑桿，或先切到英數模式。

修改模型、顏色或佈局後，執行選單 **OpenFleet → Build Factory Scene** 重建場景。

## 場景內容

- 暫定佈局（每道工序一站）：下排靠南牆，由左到右為原料倉、粗加工、精加工、淬火、回火、電鍍；上排靠北牆，由右到左為拋光、雜物倉庫、品管、包裝、成品倉、出貨。走道內側是充電維護區、刀具庫（島區中央，交接點接在內圈車道）與載具存放區。
- 西牆中間是工廠大門，進料與出貨共用：門外南側的棧板是剛到廠的原料，門內北側（靠出貨站與成品倉）是待出貨的成品箱。貨物只是示意，沒有對應的後端資料。
- 成品倉與雜物倉庫只是場景（模型、框線、標示）：沒有交接點，後端沒有對應的站點，AGV 不會去。
- 每站前方的青色方框是交接點，離走道 2.4 公尺，方框不會壓到車道。走道只畫邊線，沒有方向箭頭，因為交通規則尚未決定。
- 環狀走道有內外兩條車道（黃線是走道邊緣，白線是車道分隔），都不限方向；沒有畫箭頭，誰先走由後端的路段預約決定。
- 車輛平常停在充電維護區的車位（五格），有搬運任務才移動。四台大型車會同時工作，遇到前面有車會停下等待或改走另一條車道。
- 進度條是固定大小的螢幕元件，畫面拉遠時會顯得比機台大；加工進度逐件跳動，不是平滑移動。
- AGV：大型車（橘色頂升平台）、小型車（白色車身、刀把架）、未知車型（紫色，表示後端設定缺漏）。

## 物件與顏色的管理

- 場景裡只有 `OpenFleet` 物件掛了腳本（`FleetView`、`FleetMqttClient`、`PresentationController`）和攝影機的 `FactoryCameraController`，都不需要在 Inspector 掛任何參考。
- 執行時由 `PresentationController` 負責：以 `Resources.Load` 載入 AGV Prefab 與 PanelSettings、建立車輛、建立操作面板與進度條兩個 UIDocument、替 `Factory/Stations` 底下的每個站點加上 `StationAnchor`。
- 物件之間靠名稱對應，名稱都定義成常數並由 Build Factory Scene 共用：站點物件名稱＝後端站點代碼；Prefab 裡的 `Status Lights`、`Label`、`Load`；站點裡的 `Inbound Contents`、`Outbound Contents`。
- 要調整畫面上的顏色（連線狀態、進度條、路徑虛線、面板）只改 `PresentationController.Colors`；模型本身的材質顏色在 `Editor/Palette.cs`，改完要重建場景。

Inspector 上只剩純數值的設定：連線設定（host、port、factoryId、API 位址）在 `OpenFleet` 物件，攝影機的視角與速度在 `Main Camera`；都有預設值，不改也能執行。

注意：Editor 不在前景時，Play Mode 幾乎不會更新畫面。要在背景持續驗證，請改用 build 出來的 Player（已開啟 Run In Background）。

## 快照與 MQTT 的對齊

- 訂閱在先、HTTP 快照在後；兩者重疊的部分由 `version` 比對去掉，不會漏掉中間的更新。
- `version` 只存在後端記憶體，後端重啟就從 0 重新計算。因此狀態另外帶 `stateEpoch`（後端每次啟動產生新值）：epoch 相同才比較 version，不同就直接採用新狀態。
