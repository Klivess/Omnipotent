# OmniTrader API Reference for Frontend

This document is a strict API reference for frontend integration.

It includes, per route:
- HTTP method + path
- permission level
- inputs (query params)
- expected output shape
- example request and response

---

## Authentication

OmniTrader routes are served via `KliveAPI`.

Use header:

```http
Authorization: <profile-password>
```

Permissions used by OmniTrader routes:
- `Guest` for read/analytics/backtest
- `Klives` for deploy/undeploy operations

---

## Route Index

### Guest Routes
- `GET /omniTrader/status`
- `GET /omniTrader/strategies/available`
- `GET /omniTrader/strategies/deployed`
- `GET /omniTrader/simulator/active-persistent`
- `GET /omniTrader/analytics/live/all`
- `GET /omniTrader/analytics/live/byDeployment`
- `GET /omniTrader/analytics/persisted/all`
- `GET /omniTrader/analytics/persisted/byStrategy`
- `GET /omniTrader/analytics/strategyInsight`
- `POST /omniTrader/backtest/run`

### Klives Routes
- `POST /omniTrader/simulator/deploy`
- `POST /omniTrader/simulator/undeploy`
- `POST /omniTrader/simulator/undeployAll`

---

## 1) `GET /omniTrader/status`

### Permission
`Guest`

### Inputs
None.

### Expected Output
```json
{
  "Service": "OmniTrader",
  "DeployedCount": 2,
  "ActiveDeploymentIds": ["guid-1", "guid-2"],
  "Uptime": "00:12:10.0123456",
  "ManagerUptime": "02:11:03.9912345"
}
```

### Example Request
```http
GET /omniTrader/status
```

---

## 2) `GET /omniTrader/strategies/available`

### Permission
`Guest`

### Inputs
None.

### Expected Output
Array of strategy metadata.

```json
[
  {
    "StrategyName": "FlowSignal Trader Strategy",
    "ClassName": "FlowSignalTraderStrategy",
    "Description": "Listens to signals from FlowSignal application, and places orders."
  },
  {
    "StrategyName": "IBS Mean Reversion Strategy",
    "ClassName": "IBSMeanReversionStrategy",
    "Description": "A mean reversion strategy using Internal Bar Strength (IBS)..."
  }
]
```

### Example Request
```http
GET /omniTrader/strategies/available
```

---

## 3) `GET /omniTrader/strategies/deployed`

### Permission
`Guest`

### Inputs
None.

### Expected Output
Array of currently live deployed strategies.

```json
[
  {
    "DeploymentId": "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a",
    "StrategyName": "FlowSignal Trader Strategy",
    "FinalEquity": 10244.22,
    "TotalTrades": 18,
    "TotalPnLPercent": 2.44,
    "WinRate": 61.11,
    "TotalFeesPaid": 23.11
  }
]
```

### Example Request
```http
GET /omniTrader/strategies/deployed
```

---

## 4) `GET /omniTrader/simulator/active-persistent`

### Permission
`Guest`

### Inputs
None.

### Expected Output
Array of strategies persisted as active deployment registrations (auto-redeploy list).

```json
[
  {
    "StrategyName": "FlowSignal Trader Strategy",
    "StrategyKey": "flowsignal trader strategy",
    "Symbol": "BTCUSDT",
    "Interval": "OneMinute",
    "Settings": {
      "InitialQuoteBalance": 10000.0,
      "InitialBaseBalance": 0.0,
      "FeeFraction": 0.001,
      "SlippageFraction": 0.0005
    },
    "LastUpdatedUtc": "2026-01-11T14:13:02.1200000Z"
  }
]
```

### Example Request
```http
GET /omniTrader/simulator/active-persistent
```

---

## 5) `GET /omniTrader/analytics/live/all`

### Permission
`Guest`

### Inputs
None.

### Expected Output
Dictionary keyed by deployment ID with `OmniBacktestResult`-shape payloads.

```json
{
  "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a": {
    "InitialEquity": 10000.0,
    "FinalEquity": 10244.22,
    "FinalQuoteBalance": 10110.0,
    "FinalBaseBalance": 0.01,
    "TotalTrades": 18,
    "WinningTrades": 11,
    "LosingTrades": 7,
    "WinRate": 61.11,
    "TotalPnL": 244.22,
    "TotalPnLPercent": 2.44,
    "TotalFeesPaid": 23.11
  }
}
```

### Example Request
```http
GET /omniTrader/analytics/live/all
```

---

## 6) `GET /omniTrader/analytics/live/byDeployment`

### Permission
`Guest`

### Inputs (query)
| Name | Type | Required | Example |
|---|---|---:|---|
| `deploymentId` | `Guid` | Yes | `4d13a822-b3cf-430f-a8d0-bdbeb6f1141a` |

### Expected Output
Single `OmniBacktestResult` payload for the deployment.

### Example Request
```http
GET /omniTrader/analytics/live/byDeployment?deploymentId=4d13a822-b3cf-430f-a8d0-bdbeb6f1141a
```

### Example Error
```http
400 BadRequest
"Invalid or missing deploymentId"
```

---

## 7) `GET /omniTrader/analytics/persisted/all`

### Permission
`Guest`

### Inputs
None.

### Expected Output
Dictionary keyed by strategy name with cumulative persisted analytics (`OmniBacktestResult` shape).

```json
{
  "FlowSignal Trader Strategy": {
    "InitialEquity": 10000.0,
    "FinalEquity": 11820.5,
    "TotalTrades": 124,
    "WinningTrades": 77,
    "LosingTrades": 47,
    "WinRate": 62.1,
    "TotalPnLPercent": 18.2,
    "TotalFeesPaid": 202.4
  }
}
```

### Example Request
```http
GET /omniTrader/analytics/persisted/all
```

---

## 8) `GET /omniTrader/analytics/persisted/byStrategy`

### Permission
`Guest`

### Inputs (query)
| Name | Type | Required | Example |
|---|---|---:|---|
| `strategyName` | `string` | Yes | `FlowSignal Trader Strategy` |

### Expected Output
Single strategy cumulative persisted analytics (`OmniBacktestResult` shape).

### Example Request
```http
GET /omniTrader/analytics/persisted/byStrategy?strategyName=FlowSignal%20Trader%20Strategy
```

### Example Error
```http
400 BadRequest
"Missing strategyName"
```

---

## 9) `GET /omniTrader/analytics/strategyInsight`

### Permission
`Guest`

### Inputs (query)
| Name | Type | Required | Example |
|---|---|---:|---|
| `strategyName` | `string` | Yes | `FlowSignal Trader Strategy` |

### Expected Output
Rich insight object containing live + persisted + historical context.

```json
{
  "StrategyName": "FlowSignal Trader Strategy",
  "StrategyKey": "flowsignal trader strategy",
  "IsCurrentlyDeployed": true,
  "ActiveDeploymentId": "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a",
  "LiveSnapshot": {
    "FinalEquity": 10244.22,
    "TotalTrades": 18,
    "WinRate": 61.11
  },
  "PersistedSnapshot": {
    "FinalEquity": 11820.5,
    "TotalTrades": 124,
    "WinRate": 62.1
  },
  "TotalSessions": 9,
  "TotalBacktests": 14,
  "RecentSessions": [
    {
      "DeploymentId": "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a",
      "Symbol": "BTCUSDT",
      "Interval": "OneMinute",
      "StartTimeUtc": "2026-01-11T12:00:00Z",
      "EndTimeUtc": "2026-01-11T13:00:00Z",
      "CandlesProcessed": 60,
      "NewTrades": 3,
      "FinalQuoteBalance": 10055.1,
      "FinalBaseBalance": 0.0,
      "FinalEquity": 10055.1
    }
  ],
  "RecentBacktests": [
    {
      "RunAtUtc": "2026-01-11T13:21:00Z",
      "Symbol": "BTC",
      "Currency": "USD",
      "Interval": "OneHour",
      "CandleCount": 1000,
      "Settings": {
        "InitialQuoteBalance": 10000.0,
        "InitialBaseBalance": 0.0,
        "FeeFraction": 0.001,
        "SlippageFraction": 0.0005
      },
      "Result": {
        "FinalEquity": 11331.5,
        "TotalTrades": 33,
        "WinRate": 57.57
      }
    }
  ]
}
```

### Example Request
```http
GET /omniTrader/analytics/strategyInsight?strategyName=FlowSignal%20Trader%20Strategy
```

---

## 10) `POST /omniTrader/backtest/run`

### Permission
`Guest`

### Inputs (query)
| Name | Type | Required | Default | Example |
|---|---|---:|---|---|
| `strategyName` | `string` | Yes | - | `IBS Mean Reversion Strategy` |
| `coin` | `string` | No | `BTC` | `ETH` |
| `currency` | `string` | No | `USD` | `USD` |
| `interval` | `string/int` | No | `OneHour` | `OneMinute` or `60` |
| `candles` | `int` | No | `500` | `1000` |
| `initialQuote` | `decimal` | No | `10000` | `25000` |
| `initialBase` | `decimal` | No | `0` | `0` |
| `feeFraction` | `decimal` | No | `0.001` | `0.0008` |
| `slippageFraction` | `decimal` | No | `0.0005` | `0.0004` |

### Expected Output
Backtest execution result object. Backtest is persisted into strategy history.

```json
{
  "strategyName": "IBS Mean Reversion Strategy",
  "coin": "ETH",
  "currency": "USD",
  "interval": "OneHour",
  "candleCount": 1000,
  "result": {
    "InitialEquity": 10000.0,
    "FinalEquity": 10920.0,
    "TotalTrades": 27,
    "WinningTrades": 16,
    "LosingTrades": 11,
    "WinRate": 59.25,
    "TotalPnLPercent": 9.2,
    "TotalFeesPaid": 41.8
  }
}
```

### Example Request
```http
POST /omniTrader/backtest/run?strategyName=IBS%20Mean%20Reversion%20Strategy&coin=ETH&currency=USD&interval=OneHour&candles=1000&initialQuote=10000&initialBase=0&feeFraction=0.001&slippageFraction=0.0005
```

---

## 11) `POST /omniTrader/simulator/deploy`

### Permission
`Klives`

### Inputs (query)
| Name | Type | Required | Default | Example |
|---|---|---:|---|---|
| `strategyName` | `string` | Yes | - | `FlowSignal Trader Strategy` |
| `symbol` | `string` | No | `BTCUSDT` | `ETHUSDT` |
| `interval` | `string/int` | No | `OneMinute` | `FiveMinute` or `5` |
| `initialQuote` | `decimal` | No | `10000` | `20000` |
| `initialBase` | `decimal` | No | `0` | `0` |
| `feeFraction` | `decimal` | No | `0.001` | `0.0008` |
| `slippageFraction` | `decimal` | No | `0.0005` | `0.0004` |

### Expected Output
```json
{
  "Message": "Strategy deployed",
  "DeploymentId": "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a",
  "StrategyName": "FlowSignal Trader Strategy",
  "Symbol": "BTCUSDT",
  "Interval": "OneMinute"
}
```

### Example Request
```http
POST /omniTrader/simulator/deploy?strategyName=FlowSignal%20Trader%20Strategy&symbol=BTCUSDT&interval=OneMinute&initialQuote=10000&feeFraction=0.001&slippageFraction=0.0005
```

---

## 12) `POST /omniTrader/simulator/undeploy`

### Permission
`Klives`

### Inputs (query)
| Name | Type | Required | Example |
|---|---|---:|---|
| `deploymentId` | `Guid` | Yes | `4d13a822-b3cf-430f-a8d0-bdbeb6f1141a` |

### Expected Output (success)
```json
{
  "Message": "Strategy undeployed",
  "DeploymentId": "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a",
  "Success": true
}
```

### Expected Output (not found)
```json
{
  "Message": "Deployment not found",
  "DeploymentId": "4d13a822-b3cf-430f-a8d0-bdbeb6f1141a",
  "Success": false
}
```

### Example Request
```http
POST /omniTrader/simulator/undeploy?deploymentId=4d13a822-b3cf-430f-a8d0-bdbeb6f1141a
```

---

## 13) `POST /omniTrader/simulator/undeployAll`

### Permission
`Klives`

### Inputs
None.

### Expected Output
```json
{
  "Message": "All strategies undeployed"
}
```

### Example Request
```http
POST /omniTrader/simulator/undeployAll
```

---

## Standard Error Cases

### Missing/invalid query params
`400 BadRequest` with message string from route logic.

### Permission/auth failure
Handled by `KliveAPI` permissions with `401 Unauthorized`.

### Not found deployment
`404 NotFound` for undeploy route when `deploymentId` does not exist.

---

## Frontend Implementation Notes

- Treat numeric values as decimal-capable in UI.
- For long-running actions (`backtest/run`, deploy), show progress/loading states.
- Use `strategyInsight` as the main strategy drill-down route.
- Refresh live routes (`status`, `deployed`, `live/all`) on polling interval (5–15s).
- Refresh persisted routes on demand or slower interval (30–60s).
