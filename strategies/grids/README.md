# Grid strategies for NinjaTrader 8

Ten separate, self-contained NinjaScript strategies (copy any one `.cs` file to
`Documents\NinjaTrader 8\bin\Custom\Strategies`, then compile with F5 in the NinjaScript Editor).
All use **unmanaged orders**, trade a **net** position, default to NQ-sized settings (40 ticks = 10 points) and are
**unproven - use Sim / Playback only**. They have not been compiled or backtested by their author.

| File | Idea |
|---|---|
| `GridSymmetricalV1` | Equal buy levels below and sell levels above a start price (or the middle of a fixed range) |
| `GridAsymmetricalV1` | Different number / spacing / size per side to express a directional bias |
| `GridTrendFollowingV1` | Long-only dip-buying grid while EMA 12 > EMA 144; re-centres upward; trend exit, profit lock |
| `GridMeanReversionV1` | Grid around SMA with StdDev-sized step, re-centred as the mean moves |
| `GridMartingaleV1` | Averaging basket, size doubles per level and after losing cycles; caps + time stop |
| `GridAntiMartingaleV1` | Constant-size basket; take profit widened after losses; size grows only on win streaks |
| `GridDynamicV1` | ATR step re-spaced while running, trailing profit lock |
| `GridPriceActionV1` | Levels at swing lows / highs, optionally only with the swing structure |
| `GridFibonacciV1` | Retracement levels of the latest swing, exits at the swing extreme / extension |
| `GridVolatilityV1` | ATR-sized step, starts only when volatility is elevated, capped exposure |

## How the ladder grids work (all except the two martingale files)
Buy levels `Lb[1..N]` sit below an anchor, sell levels `Ls[1..M]` above it. Each level exits one level closer to the anchor,
so a round trip earns about one step. Only two OCO limit orders (next entry, next exit) are working at any time; each fill
moves the grid state by one level. Guards: max loss per cycle, range break (Break Ticks beyond the outermost level), daily loss
limit, optional session window, Sim/Playback-only account guard in real time.

## Known limits
- On a chart or in Playback leave "Trade Historical Bars" OFF (orders placed during history cannot be carried over to live); turn it ON only for Strategy Analyzer backtests.
- Not compiled here: expect to fix small API differences on first compile.
- Strategy Analyzer on bar data fills at most one level per bar; use tick data / Tick Replay for realistic grids.
- Partial fills are not handled specially; keep whole-contract quantities. Commission is not in the on-chart P&L.
- `GridPriceActionV1` does not implement the breakout (buy-stop above / sell-stop below) variant.
- Grids and especially the martingale lose big in a strong trend: the loss limits matter more than the entry rules.

## ML volatility model (build 7, optional, off by default)
Every grid file has a "8. ML (Bachelier)" group. `Use ML Volatility Model` adds an online-learning model that does NOT predict
direction (Bachelier: the next move is unpredictable) but the SIZE of moves: spread grows like sigma x sqrt(t).
- A ridge regression (refit every 25 bars on the last 3000 samples, shrunk toward "next sigma = last 50-bar sigma") predicts the
  RMS bar move of the next `Horizon` bars from volatility at 3 scales, the last move and time of day.
- It measures real tail fatness: z = move / the forecast made before it (P(|z|>2) vs the normal 4.6 %, 99 % z vs 2.58, excess kurtosis).
- Uses: grid step = `Step = sigma x sqrt(horizon) x k` (ticks floor), break guard widened by the measured tails, and new grids are
  blocked in volatility spikes or fat-tailed regimes. Martingale / anti-martingale: step and take profit scale with it.
- It trains on every historical bar (even with Trade Historical Bars off) and needs about 300 bars before it is "Ready"; until then
  the strategy behaves exactly as with the model off. The stats box shows its current readings.
- Prototype check on synthetic volatility-clustered data: ~12 % lower volatility-forecast error than the plain 50-bar baseline.
  It has not been validated on real NQ data -- judge it with the stats box before trusting it.
