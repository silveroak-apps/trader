# Cryptobot-Trader

The Cryptobot is a system with multiple bots working together.

This solution is built in F# and contains the trader bot that executes buy/sell trades

## Build and run

Pre-requisites:

- Dotnet SDK 5.x

To build and run - from the solution directory (i.e where the .sln file is located):

```shell
   > dotnet build
   > dotnet run
```

# Projects TODO

- Takeover position bot
- Unify bots in memory (so that event analyser + trader are in the same memory space - or don't depend on db atleast)
- KLine based fast backtester (how can we make it work for events)
  - If we can generate events based on a fake clock: using the old Coin analyser to replicate all TV alerts
- UI for building strategies
- Coin Analyser improvements to raise events
  