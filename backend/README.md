# OpenFleet 後端（P1：通訊相容性）

P1 目標：先驗證 .NET 後端、設備模擬器與 MQTT Broker 能可靠收送 JSON，並在斷線後自動恢復。Unity 端（Unity 6+）接上同一組 Topic 與契約，見 [unity/README.md](../unity/README.md)。

## 專案結構

| 專案 | 目標框架 | 責任 |
| --- | --- | --- |
| `src/OpenFleet.Contracts` | netstandard2.1 | MQTT／HTTP 共用 JSON 契約與 Topic 命名；不依賴任何套件，Unity 6 可直接引用 |
| `src/OpenFleet.Messaging` | net10.0 | MQTTnet 5 連線（自動重連、重新訂閱）、訊息分派、JSON 設定 |
| `src/OpenFleet.Api` | net10.0 | ASP.NET Core：車輛狀態、事件去重、離線判定、狀態推播、HTTP API |
| `src/OpenFleet.Simulator` | net10.0 | Worker Service：模擬 AGV 遙測並回應指令 |
| `tests/OpenFleet.Tests` | net10.0 | 單元測試＋端對端測試（程式內嵌 broker，不需要 Docker） |

## 執行

```bash
# 1. 啟動 Mosquitto（1883 = MQTT/TCP，9001 = MQTT/WebSocket）
docker compose -f docker/compose.yaml up -d

# 2. 後端（http://localhost:5229）與模擬器，各開一個終端機
dotnet run --project backend/src/OpenFleet.Api
dotnet run --project backend/src/OpenFleet.Simulator

# 3. 測試
dotnet test backend/OpenFleet.slnx
```

手動操作可用 `src/OpenFleet.Api/OpenFleet.Api.http`（VS／Rider／VS Code REST Client），或：

```bash
curl localhost:5229/health
curl localhost:5229/api/vehicles
curl -X POST localhost:5229/api/vehicles/agv-01/ping      # 之後 GET agv-01，lastCommandEvent 應為 completed
docker exec openfleet-mosquitto mosquitto_sub -t 'openfleet/v1/demo/#' -v   # 監看所有訊息
docker restart openfleet-mosquitto                         # 驗證自動重連
```

## 示範設定

| 設定 | 位置 | 內容 |
| --- | --- | --- |
| 車輛清單 | 模擬器 `Simulator:VehicleIds` | `agv-01`、`agv-02`、`agv-03` |
| 車型 | 後端 `Fleet:VehicleTypes` | `agv-01`、`agv-02` 為 `Large`，`agv-03` 為 `Small`；沒列出的車輛是 `Unknown` |
| 行駛路線 | 模擬器 `Simulator:Route` | 環狀走道中心線（封閉折線），須與 Unity `FactoryLayout.AisleLoop` 相同 |

## 資料流

```text
HTTP POST /ping ─> Api ─ commands (QoS1) ─> Mosquitto ─> Simulator
                                                        │
Api <─ events (QoS1, 去重) / telemetry (QoS0, 序號) ─────┘
 │
 └─ state/vehicles/{id} (QoS1, retained) ─> Unity／其他顯示端
```

| Topic | 方向 | QoS | Retained |
| --- | --- | --- | --- |
| `openfleet/v1/{factory}/vehicles/{id}/commands` | 後端 → 模擬器 | 1 | 否（避免重連收到過期指令） |
| `openfleet/v1/{factory}/vehicles/{id}/events` | 模擬器 → 後端 | 1 | 否 |
| `openfleet/v1/{factory}/vehicles/{id}/telemetry` | 模擬器 → 後端 | 0 | 否 |
| `openfleet/v1/{factory}/state/vehicles/{id}` | 後端 → 顯示端 | 1 | 是 |

所有訊息都包在 `MessageEnvelope<T>`：`schemaVersion`、`messageId`、`producerId`、`correlationId`、`occurredAt`、`payload`。JSON 欄位 camelCase，enum 為小寫字串。

## P1 已驗證的行為

- 指令往返：`POST /ping` → 模擬器回報 `accepted`、`completed` → HTTP 可查到結果。
- 重複投遞：同一 `(producerId, messageId)` 只改變一次狀態；模擬器對重送的同一 `commandId` 回傳原本的回報，而不是再執行一次。
- 遙測亂序：同一 Session 內序號沒有前進就丟棄；設備重啟會產生新 Session，舊 Session 遲到的封包也會被丟棄。
- 斷線：Broker 停止時 `/health` 回報 `mqttConnected: false`、`POST /ping` 回 503、車輛逾時轉為 `offline`；Broker 恢復後自動重連、重新訂閱，並補發完整狀態快照。

## 刻意留到之後的部分

- 狀態只存在記憶體；P2 改用 EF Core + PostgreSQL，Inbox／Outbox 與業務變更放在同一筆交易。
- 斷線期間的訊息不補收（CleanSession）；Unity 每次連上 Broker 都重新取 HTTP 快照，並以 `stateEpoch` + `version` 對齊（後端重啟時 `stateEpoch` 會換新值，`version` 從頭計算）。
- Mosquitto 目前允許匿名、未加密，只能在本機開發使用。

## 對照 Unity 經驗的學習點

| 程式位置 | .NET 概念 | Unity 對照 |
| --- | --- | --- |
| `MessagingServiceCollectionExtensions` | DI、Singleton、同一實例註冊成兩個服務 | 場景中唯一的 Manager 物件 |
| `MqttConnection.ExecuteAsync` | `BackgroundService`、`CancellationToken`、重連迴圈 | 常駐 Coroutine，但這裡是真的非同步 I/O、不在主執行緒 |
| `IMqttMessageHandler` 的 publisher 參數 | 避免建構子循環相依 | 避免兩個 MonoBehaviour 在 Awake 互抓 |
| `StateChangeQueue`（`Channel<T>`） | 生產者／消費者、背壓策略 | Unity 端「MQTT 執行緒 → 主執行緒」佇列是同一個概念 |
| `FleetStateStore` | `lock`、回傳複本避免共享可變狀態 | 多個元件同時改同一份資料 |
| `InboxDeduplicator`、`SimulatedFleet.GetOrCreateReplies` | 冪等性 | 網路遊戲的重送封包處理 |
| `FleetStateStoreTests` | `TimeProvider`／`FakeTimeProvider` 控制時間 | 以 `Time.deltaTime` 注入做可測試邏輯 |
| `EndToEndTests` | `WebApplicationFactory`、整合測試 | PlayMode 測試 |
