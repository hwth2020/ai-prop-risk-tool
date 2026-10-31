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
- Not compiled here: expect to fix small API differences on first compile.
- Strategy Analyzer on bar data fills at most one level per bar; use tick data / Tick Replay for realistic grids.
- Partial fills are not handled specially; keep whole-contract quantities. Commission is not in the on-chart P&L.
- `GridPriceActionV1` does not implement the breakout (buy-stop above / sell-stop below) variant.
- Grids and especially the martingale lose big in a strong trend: the loss limits matter more than the entry rules.
