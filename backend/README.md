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
| `GET /api/stations/status` | 每站的備料區、成品區件數與容量、加工進度、刀具狀態；整爐工序的站另有開爐門檻、這一爐的階段與剩餘時間（顯示端輪詢用） |
| `GET /api/settings`、`PUT /api/settings` | 示範設定：批量、機台取放每件秒數、AGV 交接一框的秒數、換刀時間、叫刀門檻、是否配送刀具、機床換線時間；測試模式的開關與倍率（各站、AGV 交接、空車車速）；逐件工序的每件加工秒數與刀具壽命；整爐工序的開爐下限、一爐上限（電鍍依桿徑）與時間；品管的不良率、反工上限與反工重鍍的下限；貨車間隔。PUT 是整份取代，不合規則回 400 |
| `POST /api/lots` | 模擬收料：在原料倉建立一個批次。可帶 `{"quantity": 組數, "rodDiameterMm": 24 或 30, "partCodes": [...]}`；一組是上沖、下沖、中模各一（`partCodes` 可以只做其中幾種），每一種零件依料框容量分成多框，每一框各建立一趟搬運。回應是每一框 |
| `GET /api/lots`、`GET /api/lots/{id}` | 料框清單（同一批次的框有相同的 `batchNumber`）；單一框的完整追蹤 |
| `GET /api/transport-tasks`、`GET /api/transport-tasks/{id}`、`.../{id}/events` | 搬運任務、派工狀態與狀態歷史。`needsAttention` 為 true 表示搬運失敗、等人工處理 |
| `POST /api/transport-tasks/{id}/resolve` | 人工處理失敗的搬運：`{"resolution": "returnToSource"}`（已放回取料站，系統重新派一趟）或 `"deliverToDestination"`（已由人工送達）。車輛在線且任務還在進行時回 400 |
| `GET /api/inspection` | 品管的累計結果：檢驗、合格、反工、報廢的支數，以及表面／鍍層／尺寸三種不良原因的支數 |
| `GET /api/shipping` | 出貨：下一班貨車還要多久、已包裝在等同零件的框到齊的支數、在出貨站等貨車的箱數與支數（其中整批到齊的箱數）、累計出貨與最近 20 筆出貨紀錄 |
| `GET /api/shortage` | 缺車統計（車都在忙）：此刻叫不到車的任務數、任務等車的累計，以及各工序機台因缺車停下來的時間（等不到料、成品沒被搬走、等刀）。車型全部離線是重大事故，另外列在 `offline…` 欄位，不算進缺車。秒數是實際時間 |
| `GET /api/shortage/waits`、`GET /api/shortage/delays` | 最近 100 筆缺車等待區間（每個任務）與機台耽誤區間（每台機台） |
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
| 批量 | 資料庫，由 Unity 面板或 `PUT /api/settings` 修改 | 30 組（三種零件各 30） |
| 機台取放 | 同上 | 每件 5 秒：加工前取一件、加工後取出，各一次。每件的週期＝5 ＋ 加工時間 ＋ 5 |
| AGV 交接 | 同上 | 一框 30 秒，和件數無關；取料、卸料各一次，由後端寫進搬運指令 |
| 加工 | 同上 | 每件 2 秒，逐件計數，整批做完才放行 |
| 刀具 | 同上 | 粗加工每 500 件、精加工每 250 件換刀；換刀 90 秒，期間機台暫停 |
| 刀具配送 | 同上 | 刀具剩 50 件時叫小型車（`agv-03`）從刀具庫送一把新刀到機邊；到壽命時新刀已到就換刀，還沒到就停機等刀。`toolDeliveryEnabled` 設成 false 時視為新刀永遠在機邊 |
| 站點與交接點 | 資料庫（`DatabaseInitializer` 建立） | 與 Unity `FactoryLayout` 的暫定佈局相同。佈局不是面板能改的設定：啟動時站點的交接點與路網都會換成程式裡目前的示範佈局 |

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

| 指令 | 設備回報 |
| --- | --- |
| `ping` | `accepted`、`completed`（立即） |
| `transport`（帶 `assignmentId`、`pickup`、`destination`、`loadQuantity`、`loadCode`、`handoverSeconds`） | `accepted` → 到取料點交接後 `loaded` → 到卸料點交接後 `completed`；忙碌或過期時 `rejected` |

車輛怎麼開過去不在指令裡：後端另外用 `clearance` 告訴每台車路徑與「可以開到第幾個點」，車開到已放行的最後一點就停下等下一次放行。回充電車位也是用放行引導，沒有對應的指令。

遙測除了位置與車速，還帶 `activity`（`idle`／`moving`／`loading`／`unloading`）、`activityProgress`（交接進度 0～1）、`loadQuantity`、`loadCode` 與 `route`（接下來要依序經過的點，最後一點是目的地）；後端原樣放進車輛狀態發給顯示端。

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
- 車速與交接：載貨越多車速越慢；AGV 與站點交接一框的時間固定，進度隨遙測回報。

## 刻意留到之後的部分

- 車輛位置與連線狀態仍只在記憶體；後端重啟後由遙測重建，並換新的 `stateEpoch`。
- 斷線期間的訊息不補收（CleanSession）；Unity 每次連上 Broker 都重新取 HTTP 快照，並以 `stateEpoch` + `version` 對齊。
- 沒有拆批、載具、刀具配送與庫存預留；搬運失敗後沒有恢復流程（見 P2實作紀錄第 7、8 節）。
- 路段預約只存在記憶體，後端重啟後由車輛位置重建；交通的已知限制見 P3實作紀錄第 5 節。
- Mosquitto 允許匿名、未加密；PostgreSQL 帳密寫在設定檔；HTTP API 沒有驗證。只能在本機開發使用。
