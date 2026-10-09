# OpenFleet 後端

- P1（通訊相容性）：.NET 後端、設備模擬器與 MQTT Broker 可靠收送 JSON，斷線後自動恢復。
- P2（最小物料閉環）：批次從原料倉經粗加工、精加工送到暫存點，批次、搬運、加工狀態都存進 PostgreSQL 並可追蹤。

- P3（多車整合，進行中）：內外兩條不限方向的車道、路段預約與死結處理，兩台大型車同時派工。

實作紀錄與決策見 [document/P1實作紀錄.md](../document/P1實作紀錄.md)、[document/P2實作紀錄.md](../document/P2實作紀錄.md)、[document/P3實作紀錄.md](../document/P3實作紀錄.md)。Unity 端見 [unity/README.md](../unity/README.md)。

## 專案結構

| 專案 | 目標框架 | 責任 |
| --- | --- | --- |
| `src/OpenFleet.Contracts` | netstandard2.1 | MQTT／HTTP 共用 JSON 契約與 Topic 命名；不依賴任何套件，Unity 6 可直接引用 |
| `src/OpenFleet.Messaging` | net10.0 | MQTTnet 5 連線（自動重連、重新訂閱）、訊息分派、JSON 設定 |
| `src/OpenFleet.Api` | net10.0 | ASP.NET Core + EF Core。`Fleet`：車輛即時狀態；`Production`：批次、設定、加工；`Transport`：搬運任務、派工、Outbox／Inbox；`Traffic`：路網、路徑規劃、路段預約與放行；`Data`：DbContext、Entity、Migration |
| `src/OpenFleet.Simulator` | net10.0 | Worker Service：模擬 AGV 依後端的放行行駛、交接並回報 |
| `tests/OpenFleet.Tests` | net10.0 | 單元測試＋整合測試（內嵌 MQTT broker；PostgreSQL 由 Testcontainers 啟動，需要 Docker） |

## 執行

```bash
# 1. 啟動 Mosquitto（1883 = MQTT/TCP，9001 = MQTT/WebSocket）與 PostgreSQL（5432，只綁定本機）
docker compose -f docker/compose.yaml up -d

# 2. 後端（http://localhost:5229）與模擬器，各開一個終端機。後端啟動時會自動套用 Migration 並建立示範資料。
dotnet run --project backend/src/OpenFleet.Api --launch-profile http
dotnet run --project backend/src/OpenFleet.Simulator

# 3. 測試（Docker 要開著）
dotnet test backend/OpenFleet.slnx
```

手動操作可用 `src/OpenFleet.Api/OpenFleet.Api.http`（VS／Rider／VS Code REST Client），或：

```bash
curl localhost:5229/health
curl localhost:5229/api/vehicles
curl -X POST localhost:5229/api/lots -H "Content-Type: application/json" -d "{}"   # 建立一個批次，回傳 id
curl localhost:5229/api/lots/{id}                           # 追蹤：位置異動、加工紀錄、搬運任務
curl localhost:5229/api/transport-tasks
docker exec openfleet-mosquitto mosquitto_sub -t 'openfleet/v1/demo/#' -v   # 監看所有訊息
docker exec -it openfleet-postgres psql -U openfleet -c 'select "LotNumber","Status","StationCode","VehicleId" from "Lots"'
docker restart openfleet-mosquitto                         # 驗證自動重連
```

修改 Entity 之後新增 Migration：

```bash
cd backend
dotnet tool restore
dotnet ef migrations add <名稱> --project src/OpenFleet.Api -o Data/Migrations
```

## HTTP API

| 方法與路徑 | 用途 |
| --- | --- |
| `GET /health` | MQTT 是否連線 |
| `GET /api/vehicles`、`GET /api/vehicles/{id}` | 車輛即時狀態（記憶體） |
| `POST /api/vehicles/{id}/ping` | P1 通訊驗證指令 |
| `GET /api/stations` | 站點與交接點座標 |
| `GET /api/stations/status` | 每站的備料區、成品區件數與容量、加工進度、刀具狀態（顯示端輪詢用） |
| `GET /api/settings`、`PUT /api/settings` | 示範設定：批量、上下料每件秒數、換刀時間、各站每件加工秒數與刀具壽命。PUT 是整份取代，不合規則回 400 |
| `POST /api/lots` | 模擬收料：在原料倉建立批次並建立第一趟搬運。可帶 `{"quantity": n}` |
| `GET /api/lots`、`GET /api/lots/{id}` | 批次清單；單一批次的完整追蹤 |
| `GET /api/transport-tasks`、`GET /api/transport-tasks/{id}`、`.../{id}/events` | 搬運任務、派工狀態與狀態歷史 |
| `GET /api/simulation`、`PUT /api/simulation` | 測試用的模擬時間倍率（`{"timeScale": 10}`，1～20）。行駛、上下料、加工、換刀一起加快；只存在記憶體，後端重啟回到 1 |
| `POST /api/demo/reset` | 清除所有批次與搬運資料、刀具計數歸零（設定、站點、車輛保留）。只供本機示範 |

## 示範設定

以下都是暫用的示範值，不是定案的業務規則（見 P2實作紀錄第 3 節）。

| 設定 | 位置 | 內容 |
| --- | --- | --- |
| 車輛清單 | 模擬器 `Simulator:VehicleIds` | `agv-01`、`agv-02`、`agv-03` |
| 車型 | 後端 `Fleet:VehicleTypes` | `agv-01`、`agv-02` 為 `Large`，`agv-03` 為 `Small`；啟動時寫入 `Vehicles` 資料表 |
| 派工車輛 | 後端 `Fleet:DispatchVehicleIds` | `agv-01`、`agv-02`（兩台大型車） |
| 待命車位 | 後端 `Fleet:Parking` | 充電維護區的 3 個車位；沒有任務的車會被引導回去 |
| 起始位置 | 模擬器 `Simulator:StartPositions` | 各車的充電車位 |
| 路網 | 資料庫 `RouteNodes`／`RouteEdges`（`DemoRoadNetwork` 建立） | 內外兩條車道、車道間的連接、站點與車位的支線；須與 Unity `FactoryLayout` 的車道位置相同 |
| 交通控制 | 後端 `Traffic:*` | 每 0.2 秒一輪、前方預約 2 個節點、被擋 4 秒後改道 |
| 車速 | 模擬器 `Simulator:Speed`、`FullLoadSpeed`、`LoadCapacity` | 空車 2 m/s；載貨依件數線性遞減，滿載 30 件時 1 m/s |
| 批量 | 資料庫，由 Unity 面板或 `PUT /api/settings` 修改 | 30 件 |
| 上下料時間 | 同上 | 每件 5 秒；一趟的取料、卸料各花「件數 × 5 秒」，由後端寫進搬運指令 |
| 加工 | 同上 | 每件 2 秒，逐件計數，整批做完才放行 |
| 刀具 | 同上 | 粗加工每 500 件、精加工每 250 件換刀；換刀 20 秒，期間機台暫停 |
| 站點與交接點 | 資料庫（`DatabaseInitializer` 建立） | 與 Unity `FactoryLayout` 的暫定佈局相同 |

## 資料流

```text
HTTP POST /api/lots ─> Api ── 同一筆交易 ──> PostgreSQL（批次、搬運任務）
                        │
        派工迴圈：派工＋任務狀態＋Outbox 同一筆交易
                        │
        Outbox 發送 ─ commands (QoS1) ─> Mosquitto ─> Simulator（行駛、交接）
                                                          │
Api <─ events (QoS1，Inbox 去重＋業務變更同一筆交易) ──────┤
Api <─ telemetry (QoS0，序號) ────────────────────────────┘
 │
 └─ state/vehicles/{id} (QoS1, retained) ─> Unity／其他顯示端
```

| Topic | 方向 | QoS | Retained |
| --- | --- | --- | --- |
| `openfleet/v1/{factory}/vehicles/{id}/commands` | 後端 → 模擬器 | 1 | 否（避免重連收到過期指令） |
| `openfleet/v1/{factory}/vehicles/{id}/events` | 模擬器 → 後端 | 1 | 否 |
| `openfleet/v1/{factory}/vehicles/{id}/telemetry` | 模擬器 → 後端 | 0 | 否 |
| `openfleet/v1/{factory}/vehicles/{id}/clearance` | 後端 → 模擬器 | 1 | 否（放行：路徑與可以開到哪裡） |
| `openfleet/v1/{factory}/state/vehicles/{id}` | 後端 → 顯示端 | 1 | 是 |
| `openfleet/v1/{factory}/state/simulation` | 後端 → 模擬器 | 1 | 是（模擬時間倍率） |

| 指令 | 設備回報 |
| --- | --- |
| `ping` | `accepted`、`completed`（立即） |
| `transport`（帶 `assignmentId`、`pickup`、`destination`、`loadQuantity`、`loadCode`、`handoverSeconds`） | `accepted` → 到取料點交接後 `loaded` → 到卸料點交接後 `completed`；忙碌或過期時 `rejected` |

車輛怎麼開過去不在指令裡：後端另外用 `clearance` 告訴每台車路徑與「可以開到第幾個點」，車開到已放行的最後一點就停下等下一次放行。回充電車位也是用放行引導，沒有對應的指令。

遙測除了位置與車速，還帶 `activity`（`idle`／`moving`／`loading`／`unloading`）、`activityProgress`（上下料進度 0～1）、`loadQuantity`、`loadCode` 與 `route`（接下來要依序經過的點，最後一點是目的地）；後端原樣放進車輛狀態發給顯示端。

所有訊息都包在 `MessageEnvelope<T>`：`schemaVersion`、`messageId`、`producerId`、`correlationId`、`occurredAt`、`payload`。JSON 欄位 camelCase，enum 為小寫字串。

## P1 已驗證的行為

- 指令往返：`POST /ping` → 模擬器回報 `accepted`、`completed` → HTTP 可查到結果。
- 重複投遞：同一 `(producerId, messageId)` 只改變一次狀態；模擬器對重送的同一 `commandId` 回傳原本的回報，而不是再執行一次。
- 遙測亂序：同一 Session 內序號沒有前進就丟棄；設備重啟會產生新 Session，舊 Session 遲到的封包也會被丟棄。
- 斷線：Broker 停止時 `/health` 回報 `mqttConnected: false`、`POST /ping` 回 503、車輛逾時轉為 `offline`；Broker 恢復後自動重連、重新訂閱，並補發完整狀態快照。

## P2 已驗證的行為

- 物料閉環：批次從原料倉經粗加工、精加工送到暫存點；每次位置異動、每道加工、每趟搬運都有紀錄。
- 交易一致性：派工、任務狀態、狀態歷史與待發送指令同一筆交易寫入（Outbox）。
- 不重複占車：同一台車、同一個任務同時只有一筆有效派工，由資料庫的部分唯一索引保證。
- 樂觀並行：兩個工作者改同一筆資料時，後寫的一方失敗（PostgreSQL `xmin`），不會覆蓋。
- 冪等：重送的事件只有一次業務效果（Inbox 主鍵＋同一筆交易）；重送的指令設備不會重複執行。
- 不合法的回報（順序不對、不是這台車的派工）被忽略並記錄。
- 指令逾時：派工標記為待確認，仍占用車輛，不改派給另一台車；遲到的接受回報可以繼續流程。
- 滿站等待：目的站待加工料容量不足時任務不派出（加工中還沒做的件數也算在內）；完工件沒被搬走時機台不開始下一批。
- 逐件加工與換刀：一件一件計數，到達刀具壽命時機台暫停換刀，換完計數歸零並留下紀錄。
- 重啟：加工進度與換刀的時間點都存在資料庫，後端重啟後一次補算，結果和持續執行相同。
- 車速與上下料：載貨越多車速越慢；上下料時間依件數計算，進度隨遙測回報。

## 刻意留到之後的部分

- 車輛位置與連線狀態仍只在記憶體；後端重啟後由遙測重建，並換新的 `stateEpoch`。
- 斷線期間的訊息不補收（CleanSession）；Unity 每次連上 Broker 都重新取 HTTP 快照，並以 `stateEpoch` + `version` 對齊。
- 沒有拆批、載具、刀具配送與庫存預留；搬運失敗後沒有恢復流程（見 P2實作紀錄第 7、9 節）。
- 路段預約只存在記憶體，後端重啟後由車輛位置重建；交通的已知限制見 P3實作紀錄第 5 節。
- Mosquitto 允許匿名、未加密；PostgreSQL 帳密寫在設定檔；HTTP API 沒有驗證。只能在本機開發使用。

## 對照 Unity 經驗的學習點

| 程式位置 | .NET 概念 | Unity 對照 |
| --- | --- | --- |
| `MessagingServiceCollectionExtensions` | DI、Singleton、同一實例註冊成兩個服務 | 場景中唯一的 Manager 物件 |
| `MqttConnection.ExecuteAsync` | `BackgroundService`、`CancellationToken`、重連迴圈 | 常駐 Coroutine，但這裡是真的非同步 I/O、不在主執行緒 |
| `IMqttMessageHandler` 的 publisher 參數 | 避免建構子循環相依 | 避免兩個 MonoBehaviour 在 Awake 互抓 |
| `StateChangeQueue`（`Channel<T>`） | 生產者／消費者、背壓策略 | Unity 端「MQTT 執行緒 → 主執行緒」佇列是同一個概念 |
| `FleetStateStore` | `lock`、回傳複本避免共享可變狀態 | 多個元件同時改同一份資料 |
| `VehicleEventProcessor`（Inbox）、`SimulatedFleet.HandleCommand` | 冪等性 | 網路遊戲的重送封包處理 |
| `FleetStateStoreTests` | `TimeProvider`／`FakeTimeProvider` 控制時間 | 以 `Time.deltaTime` 注入做可測試邏輯 |
| `EndToEndTests` | `WebApplicationFactory`、整合測試 | PlayMode 測試 |

P2 新增的學習對照（DI 生命週期、領域模型、交易、並行控制、Outbox／Inbox）見 [P2實作紀錄.md](../document/P2實作紀錄.md) 第 8 節。
