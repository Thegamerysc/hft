# Runtime architecture

```text
NinjaTrader C# net48 AddOn
        │ versioned binary events, loopback TCP
        ▼
C# .NET 10 MarketHost / Core
 book → causal features → completed bars / event rules → signals
        │ direct signal messages
        ▼
One C# ExecutionWorker per profile/account
        │ native command/report stream
        ▼
Thin MQL5 EA in a dedicated Exness MT5 demo terminal

MarketHost/workers → independent bounded recorder → gzip / Parquet
MarketHost        → pushed snapshots             → Blazor dashboard
```

There is no file polling between source events, signals and execution. Disk files provide configuration, offline replay and recovery. The small synchronous durable intention journal is an explicit execution-recovery requirement; bulk research recording is independent.

The ten-project solution separates contracts, transport, Core, execution, recording, hosts, research, Web and tests. NinjaTrader remains net48-compatible. The retail MT5 integration uses supported MQL5 native APIs; a C#-only broker API is not assumed.

Market prices use scaled integers. Arrival sequence, epochs, source times, local receipt times and quality flags survive recording. Nanosecond integer representation does not imply nanosecond provider accuracy. Quote/depth size is displayed liquidity; Trade size is executed quantity. Cached snapshots do not manufacture live freshness.

The original A0 controls use fixed 20-point SL, a 1.16R target and .20 lot per opposing leg. A survivor occupies its concurrency slot. There is no normal A0 early close. The directional JD1 experiment has a five-minute maximum hold and a separate optional persistent flow-fade exit, disabled by default.

Whole trade prints complete volume/dollar bars without splitting or reuse. Thresholds depend only on the preceding 20 complete cash-session totals and a versioned calendar. Source gaps, dropped events, absent history and uncertain executions cannot create successful training labels.

Account workers isolate broker quotes/basis, actual fills, reservations and state. Execution intents are durable before sending; uncertain orders are reconciled rather than resent. Partial pairs and protection errors are separately identified. Shadow fills are simulated and remain distinguishable from native broker transactions.

The joint family is experimental. No validated native model ships. Historical proxy feature definitions and native features require parity checks before model deployment. Previously inspected research dates cannot become an untouched final test. New NQ results require NQ data; US500 results are not transferred by changing a dropdown.

This is a research/demo release. Genuine full-session feed quality, native transaction recovery and provider-to-fill latency need forward validation on the receiving PC. A tiny synthetic kernel benchmark is not an end-to-end execution latency claim.
