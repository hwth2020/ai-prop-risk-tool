#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// GridTrendFollowingV1 -- Trend-following grid -- NinjaTrader 8 strategy (UNPROVEN, sim / playback only)
//
//  Long only while EMA(Fast) > EMA(Slow) (12 / 144 by default). Buy levels are built DOWNWARD from the current price; every
//  level exits one step higher. When everything is closed and price has moved Recenter Ticks above the grid, the grid is
//  rebuilt at the new price, so it keeps tracking the uptrend. Exits: Trend Exit flattens when the trend flips, Profit Lock
//  flattens at a cycle profit, Max Loss / Break Ticks limit the damage. Allow Short mirrors it in downtrends (off by default).
//  Main risk: a wrong trend read means the dip-buying grid fills against the move.
//
//  How the grid works (shared by the "ladder" grids in this folder; UNMANAGED orders, NET position):
//   - Entry levels sit below the anchor (buy levels, Lb[1..N]) and/or above it (sell levels, Ls[1..M]).
//   - Every level has its own exit: the NEXT level toward the anchor (level 1 exits at the anchor / exit price), so each
//     round trip earns about one grid step. Only two limit orders (OCO) are working at any time: the next entry and the
//     next exit. When one fills the grid state moves by one level and the pair is replaced.
//   - Because NinjaTrader nets positions per instrument, buy and sell levels never hold opposite positions at once.
//   - Guards: max loss per grid cycle, range break (price beyond the outermost level + Break Ticks) -> flatten and pause,
//     daily loss limit, optional session window.
//  Backtests (Strategy Analyzer) only see one level per bar unless you use tick data -- use Tick Replay or 1 tick series.
//  Partial fills are not handled specially; use whole-contract quantities. Commissions are NOT in the on-chart P&L.
//  Safety: Enable Trading can be switched off; in real time only Sim / Playback accounts unless allowed. UNPROVEN.

namespace NinjaTrader.NinjaScript.Strategies
{

    public class GridTrendFollowingV1 : Strategy
    {
        private const string LogTag = "[GridTrendFollowingV1]";

        // grid: [0] = exit anchor of the side, [1..N] / [1..M] = entry levels, quantities in Qb / Qs
        private double[] Lb = new double[1], Ls = new double[1];
        private int[] Qb = new int[1], Qs = new int[1];
        private int N, M;
        private int s;                          // grid state: +k = k buy levels open (long), -k = k sell levels open (short)
        private bool gridOn, paused, startedOnce, flattening, tradingAllowed = true, dayStopped;
        private int pauseLeft, ordCounter, barsSinceBuild, cycles, wins, gridDir;
        private Order dnOrd, upOrd;
        private int accPos;                     // own position accounting (works in backtest and real time)
        private double accAvg, realized, cycleBase, dayBase, cyclePeak;
        private DateTime dayDate = DateTime.MinValue;
        private string blockReason = "", lastNote = "idle";
        private int subsThisBar;
        private bool visualInit;
        private EMA emaF, emaS;

        // The account (not just this strategy) must be flat before a grid / cycle starts. This replaces
        // StartBehavior.WaitUntilFlat, which made NinjaTrader try to cancel live orders at start and fail in Playback.
        private bool AccountFlat()
        {
            try { return PositionAccount == null || PositionAccount.MarketPosition == MarketPosition.Flat; }
            catch { return true; }
        }

        private int Warmup { get { return SlowPeriod + 2; } }
        private double Rnd(double v) { return Instrument.MasterInstrument.RoundToTickSize(v); }
        // Printing / drawing is skipped while NinjaTrader replays history: it makes chart loading very slow.
        private void Log(string m) { if (State == State.Realtime) Print(LogTag + " " + Time[0] + " " + m); }

        protected override void OnStateChange()
        {
            if (DebugLog && State != State.SetDefaults) Print(LogTag + " state -> " + State + (CurrentBars != null && CurrentBars.Length > 0 ? ", bars " + CurrentBars[0] : ""));
            if (State == State.SetDefaults)
            {
                Description = "Trend-following grid: long-only buy-the-dip grid while EMA fast > EMA slow; re-centres upward after the grid closes (unproven).";
                Name        = "GridTrendFollowingV1";
                Calculate   = Calculate.OnPriceChange;
                IsUnmanaged = true;
                IsOverlay   = true;
                StartBehavior = StartBehavior.ImmediatelySubmit;
                BarsRequiredToTrade = 30;
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;   // rejections are handled in OnOrderUpdate
                FastPeriod = 12; SlowPeriod = 144; TrendExit = true; AllowShort = false;
                GridStepTicks = 40; Levels = 5; QtyPerLevel = 1; RecenterTicks = 80; ProfitLockDollars = 0;
                MaxLossDollars     = 1500;
                DailyLossLimit     = 2000;
                BreakTicks         = 40;
                PauseBars          = 10;
                UseSession         = false;
                StartTime          = 930;
                EndTime            = 1555;
                EnableTrading      = true;
                AllowNonSimAccount = false;
                DebugLog           = false;
                TradeHistory       = false;
                UseMl              = false;
                MlHorizonBars      = 30;
                MlStepSigmas       = 0.5;
                MlMinStepTicks     = 8;
                MlBreakSigmas      = 1.0;
                MlMaxVolRatio      = 2.0;
                MlMaxTailZ         = 3.5;
            }
            else if (State == State.Configure)
            {
                // Force it: NinjaTrader saves strategy properties with the chart / workspace, so an instance created with an
                // older build could keep StartBehavior.WaitUntilFlat, which tries to cancel live orders on start and fails in Playback.
                StartBehavior = StartBehavior.ImmediatelySubmit;
            }
            else if (State == State.DataLoaded)
            {
                emaF = EMA(Close, FastPeriod); emaS = EMA(Close, SlowPeriod);
                ml = UseMl ? new BachelierModel(MlHorizonBars, TickSize) : null;
                if (Account != null && !AllowNonSimAccount && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                    && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
                {
                    tradingAllowed = false;
                    blockReason = "account " + Account.Name + " is not a Sim / Playback account -- live trading blocked";
                    Print(LogTag + " " + blockReason);
                }
                Print(LogTag + " build 7 | StartBehavior=" + StartBehavior + " | TradeHistory=" + TradeHistory + " | " + Instrument.FullName + " " + BarsPeriod + ": trading " + EnableTrading);
            }
            else if (State == State.Realtime)
            {
                // Orders placed while NinjaTrader replayed history become LIVE orders now. Swap the stored references,
                // otherwise cancelling them later disables the strategy ("modify a historical order that has transitioned to live").
                try
                {
                    if (dnOrd != null) dnOrd = GetRealtimeOrder(dnOrd);
                    if (upOrd != null) upOrd = GetRealtimeOrder(upOrd);
                }
                catch (Exception ex) { Print(LogTag + " realtime order transition: " + ex.Message); dnOrd = upOrd = null; }
            }
            else if (State == State.Terminated)
            {
                try
                {
                    if (Position != null && Position.MarketPosition != MarketPosition.Flat)
                        Print(LogTag + " WARNING: stopped with an open position (" + Position.MarketPosition + " " + Position.Quantity
                            + ") -- the grid orders are cancelled; manage or close it in the Positions tab.");
                }
                catch { }
            }
        }

        protected override void OnBarUpdate()
        {
            if (DebugLog && BarsInProgress == 0 && CurrentBar % 500 == 0) Print(LogTag + " heartbeat: bar " + CurrentBar + " " + Time[0] + " state " + State);
            if (ml != null && BarsInProgress == 0 && IsFirstTickOfBar && CurrentBar >= 3)
            {
                ml.Update(Close[1] - Close[2], Time[1].Hour * 60 + Time[1].Minute);
                if (DebugLog && ml.Ready && CurrentBar % 500 == 0) Print(LogTag + " " + MlText().Trim());
            }
            if (BarsInProgress != 0 || CurrentBar < Math.Max(BarsRequiredToTrade, Warmup)) return;
            if (State == State.Historical && !TradeHistory) return;      // no historical orders: they cannot be carried over to live cleanly
            DateTime t = Time[0];
            if (t.Date != dayDate) { dayDate = t.Date; dayBase = realized; dayStopped = false; }
            bool newBar = IsFirstTickOfBar;
            if (newBar)
            {
                barsSinceBuild++;
                subsThisBar = 0;
                if (paused && --pauseLeft <= 0) paused = false;
            }

            if (State == State.Realtime && !visualInit) { visualInit = true; if (gridOn) DrawGrid(); }
            bool flat = Position.MarketPosition == MarketPosition.Flat;
            if (flat) flattening = false;
            if (flat && s == 0) { cycleBase = realized; cyclePeak = 0; }
            double cyc = (realized - cycleBase) + Unrealized();
            if (cyc > cyclePeak) cyclePeak = cyc;

            if (!EnableTrading || (State == State.Realtime && !tradingAllowed)) { if (newBar) DrawStats(cyc); return; }

            int hm = t.Hour * 100 + t.Minute;
            bool inSession = !UseSession || (hm >= StartTime && hm < EndTime);
            if (UseSession && hm >= EndTime && (gridOn || !flat)) Flatten("session end", false);
            double dayPnl = (realized - dayBase) + Unrealized();
            if (DailyLossLimit > 0 && dayPnl <= -DailyLossLimit && !dayStopped) { dayStopped = true; Flatten("daily loss limit", false); }

            if (gridOn) Guards(cyc);
            if (gridOn && s == 0 && flat && newBar && RebuildDue()) { CancelWorking(); gridOn = false; }
            if (!gridOn && !paused && flat && s == 0 && !dayStopped && inSession && newBar && AccountFlat() && MlAllowsStart()) TryStart();
            if (newBar && gridOn) StrategyBar();
            if (newBar) DrawStats(cyc);
        }

        // ---- strategy specific --------------------------------------------------------------------------------
        private bool AllowStart()
        {
            return emaF[0] > emaS[0] || (AllowShort && emaF[0] < emaS[0]);
        }

        private bool BuildGrid()
        {
            double step = StepFor(GridStepTicks * TickSize);
            double a = Rnd(Close[0]);
            if (emaF[0] > emaS[0])
            {
                gridDir = 1;
                return BuildSides(a, Ladder(a, step, Levels, -1), QtyPerLevel, a, new List<double>(), 0);
            }
            gridDir = -1;
            return BuildSides(a, new List<double>(), 0, a, Ladder(a, step, Levels, 1), QtyPerLevel);
        }

        private bool RebuildDue()
        {
            if (gridDir > 0) return Close[0] > Lb[0] + RecenterTicks * TickSize || emaF[0] <= emaS[0];
            return Close[0] < Ls[0] - RecenterTicks * TickSize || emaF[0] >= emaS[0];
        }

        private void StrategyBar()
        {

        }

        private void ExtraGuard(double cyc)
        {
            bool trendOk = gridDir > 0 ? emaF[0] > emaS[0] : emaF[0] < emaS[0];
            if (TrendExit && accPos != 0 && !trendOk) { Flatten("trend flipped", true); return; }
            if (ProfitLockDollars > 0 && cyc >= ProfitLockDollars) Flatten("profit locked", false);
        }

        // ---- grid construction helpers --------------------------------------------------------------------------
        private List<double> Ladder(double from, double step, int n, int dir)
        {
            var l = new List<double>();
            for (int i = 1; i <= n; i++) l.Add(from + dir * i * step);
            return l;
        }

        private bool BuildSides(double exitL, List<double> lv, int qL, double exitS, List<double> sv, int qS)
        {
            N = Math.Min(lv.Count, 40); M = Math.Min(sv.Count, 40);
            Lb = new double[N + 1]; Qb = new int[N + 1]; Ls = new double[M + 1]; Qs = new int[M + 1];
            Lb[0] = Rnd(exitL); Ls[0] = Rnd(exitS);
            for (int i = 0; i < N; i++) { Lb[i + 1] = Rnd(lv[i]); Qb[i + 1] = qL; }
            for (int i = 0; i < M; i++) { Ls[i + 1] = Rnd(sv[i]); Qs[i + 1] = qS; }
            TrimPassed(Close[0]);
            return N + M > 0;
        }

        // Levels the price has already passed (a buy level at/above the price, a sell level at/below it) would fill at
        // once at market. Drop them; the next level then exits at the dropped level.
        private void TrimPassed(double price)
        {
            while (N > 0 && Lb[1] >= price)
            {
                for (int i = 0; i < N; i++) Lb[i] = Lb[i + 1];
                for (int i = 1; i < N; i++) Qb[i] = Qb[i + 1];
                N--;
                Array.Resize(ref Lb, N + 1); Array.Resize(ref Qb, N + 1);
            }
            while (M > 0 && Ls[1] <= price)
            {
                for (int i = 0; i < M; i++) Ls[i] = Ls[i + 1];
                for (int i = 1; i < M; i++) Qs[i] = Qs[i + 1];
                M--;
                Array.Resize(ref Ls, M + 1); Array.Resize(ref Qs, M + 1);
            }
        }

        private void TryStart()
        {
            if (!AllowStart()) return;
            if (!BuildGrid()) { lastNote = "no grid could be built"; return; }
            s = 0; gridOn = true; startedOnce = true; barsSinceBuild = 0;
            lastNote = "grid started: " + N + " buy / " + M + " sell levels";
            if (State == State.Realtime) Log(lastNote + " | buy " + LevelText(Lb, N) + " | sell " + LevelText(Ls, M));
            DrawGrid();
            PlaceOrders();
        }

        private string LevelText(double[] a, int n)
        {
            var parts = new List<string>();
            for (int i = 0; i <= n; i++) parts.Add(a[i].ToString("F2", CultureInfo.InvariantCulture));
            return string.Join(",", parts);
        }

        // ---- risk -------------------------------------------------------------------------------------------------
        private void Guards(double cyc)
        {
            double px = Close[0];
            if (MaxLossDollars > 0 && cyc <= -MaxLossDollars) { Flatten("max loss per cycle", true); return; }
            if (s > 0 && N > 0 && px <= Lb[N] - BreakDist()) { Flatten("range break (long side)", true); return; }
            if (s < 0 && M > 0 && px >= Ls[M] + BreakDist()) { Flatten("range break (short side)", true); return; }
            ExtraGuard(cyc);
        }

        private void Flatten(string reason, bool pauseAfter)
        {
            CancelWorking();
            if (!flattening)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                { SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, Position.Quantity, 0, 0, "", "GFLAT"); flattening = true; }
                else if (Position.MarketPosition == MarketPosition.Short)
                { SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, "", "GFLAT"); flattening = true; }
            }
            gridOn = false; s = 0;
            if (pauseAfter) { paused = true; pauseLeft = PauseBars; }
            lastNote = "flattened: " + reason;
            Log("FLATTEN -- " + reason);
        }

        // ---- orders -------------------------------------------------------------------------------------------------
        private static bool IsLive(Order o)
        {
            return o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.PartFilled
                || o.OrderState == OrderState.Submitted || o.OrderState == OrderState.ChangePending || o.OrderState == OrderState.ChangeSubmitted;
        }

        private void CancelWorking()
        {
            if (dnOrd != null && IsLive(dnOrd)) CancelOrder(dnOrd);
            if (upOrd != null && IsLive(upOrd)) CancelOrder(upOrd);
            dnOrd = upOrd = null;
        }

        // next order when price falls one level / rises one level, from the current grid state s
        private bool DownOrder(out int q, out double px)
        {
            q = 0; px = 0;
            if (s >= 0) { if (s + 1 > N) return false; q = Qb[s + 1]; px = Lb[s + 1]; return true; }
            int b = -s; q = Qs[b]; px = Ls[b - 1]; return true;
        }

        private bool UpOrder(out int q, out double px)
        {
            q = 0; px = 0;
            if (s <= 0) { if (-s + 1 > M) return false; q = Qs[-s + 1]; px = Ls[-s + 1]; return true; }
            q = Qb[s]; px = Lb[s - 1]; return true;
        }

        private void PlaceOrders()
        {
            CancelWorking();
            if (!gridOn || paused) return;
            if (++subsThisBar > 40) { Flatten("order storm (runaway order loop)", true); return; }
            if (DebugLog) Print(LogTag + " " + Time[0] + " PlaceOrders state " + s + " price " + Close[0]);
            string oco = "G" + (++ordCounter).ToString(CultureInfo.InvariantCulture) + "_" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture);
            int q; double px;
            if (DownOrder(out q, out px) && q > 0)
                dnOrd = SubmitOrderUnmanaged(0, accPos < 0 ? OrderAction.BuyToCover : OrderAction.Buy, OrderType.Limit, q, Rnd(px), 0, oco, "GD" + ordCounter);
            if (UpOrder(out q, out px) && q > 0)
                upOrd = SubmitOrderUnmanaged(0, accPos > 0 ? OrderAction.Sell : OrderAction.SellShort, OrderType.Limit, q, Rnd(px), 0, oco, "GU" + ordCounter);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filledQty,
                                              double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (orderState == OrderState.Rejected && order.Name != null)
            {
                // Handled here instead of letting NinjaTrader terminate the strategy: print everything needed to see WHY it was rejected.
                Print(LogTag + " REJECTED " + order.Name + " " + order.OrderAction + " x" + quantity + " " + order.OrderType + " @ " + limitPrice
                    + " | error " + error + " | " + comment + " | grid state " + s + ", own position " + accPos
                    + ", strategy position " + Position.MarketPosition + " " + Position.Quantity + " | account " + (Account != null ? Account.Name : "?"));
                if (gridOn && order.Name != "GFLAT") Flatten("order rejected", true);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                Order o = execution.Order;
                if (o == null) return;
                bool buy = o.OrderAction == OrderAction.Buy || o.OrderAction == OrderAction.BuyToCover;
                Book(buy, price, quantity);
                if (o.OrderState != OrderState.Filled || !gridOn) return;
                string n = o.Name ?? "";
                if (n.StartsWith("GD")) { s++; Transition(); }
                else if (n.StartsWith("GU")) { s--; Transition(); }
            }
            catch (Exception ex) { Print(LogTag + " execution error: " + ex.Message); }
        }

        private void Transition()
        {
            if (s == 0 && accPos == 0) { cycles++; if (realized - cycleBase > 0) wins++; }
            PlaceOrders();
        }

        private void Book(bool buy, double price, int qty)
        {
            double pv = Instrument.MasterInstrument.PointValue;
            int sg = buy ? 1 : -1, rem = qty;
            if (accPos == 0 || Math.Sign(accPos) == sg)
            {
                accAvg = (accAvg * Math.Abs(accPos) + price * rem) / (Math.Abs(accPos) + rem);
                accPos += sg * rem;
            }
            else
            {
                int closing = Math.Min(rem, Math.Abs(accPos));
                realized += (price - accAvg) * Math.Sign(accPos) * closing * pv;
                accPos += sg * closing; rem -= closing;
                if (accPos == 0) accAvg = 0;
                if (rem > 0) { accAvg = price; accPos = sg * rem; }
            }
        }

        private double Unrealized()
        {
            return accPos == 0 ? 0 : (Close[0] - accAvg) * accPos * Instrument.MasterInstrument.PointValue;
        }

        // ---- drawing --------------------------------------------------------------------------------------------------
        private void DrawGrid()
        {
            if (State != State.Realtime) return;
            for (int j = 1; j <= 40; j++) { RemoveDrawObject("GRb" + j); RemoveDrawObject("GRs" + j); }
            RemoveDrawObject("GRa");
            for (int j = 1; j <= N; j++) Draw.HorizontalLine(this, "GRb" + j, Lb[j], Brushes.SeaGreen);
            for (int j = 1; j <= M; j++) Draw.HorizontalLine(this, "GRs" + j, Ls[j], Brushes.IndianRed);
            Draw.HorizontalLine(this, "GRa", N > 0 ? Lb[0] : Ls[0], Brushes.DodgerBlue);
        }

        private void DrawStats(double cyc)
        {
            if (State != State.Realtime) return;
            string text = Name + "  |  " + (!EnableTrading ? "OFF" : (State == State.Realtime && !tradingAllowed) ? "BLOCKED" : gridOn ? "GRID ON" : paused ? "PAUSED" : "WAITING")
                + "\nstate " + s + " (" + (s > 0 ? "long " + s + " lvl" : s < 0 ? "short " + (-s) + " lvl" : "flat") + "), position " + accPos
                + "  |  cycle $" + cyc.ToString("F0") + "  |  realized $" + realized.ToString("F0")
                + "\ngrids completed " + cycles + " (" + wins + " profitable)  |  last: " + lastNote
                + MlText() + "\nUNPROVEN -- sim only. Commissions not included.";
            Draw.TextFixed(this, "GRIDstats", text, TextPosition.TopLeft, Brushes.White,
                new SimpleFont("Arial", 11), Brushes.Transparent, Brushes.Black, 70);
        }

        // ======================================================================================================
        //  Bachelier volatility model (optional, UseMl). Not a direction predictor: Bachelier's point is that the next
        //  move is unpredictable, but its SIZE is not -- price spread grows like sigma x sqrt(t). The model learns
        //  sigma online: a ridge regression (refit every 25 bars on the last 3000 samples, shrunk toward "the next
        //  sigma = the last 50-bar sigma") predicts the RMS bar move of the next Horizon bars from recent volatility
        //  at 3 scales, the last move and time of day. It also measures how fat the real tails are (z = move / the
        //  forecast made before it) so guards can be wider than a normal distribution would suggest.
        // ======================================================================================================
        private sealed class BachelierModel
        {
            private const int K = 7;
            private const double Alpha = 5.0;
            private const int Window = 3000;
            private readonly int horizon;
            private readonly double tick;
            private readonly double[] w = new double[K], w0 = new double[K];
            private readonly List<double> rets = new List<double>();
            private readonly Queue<double[]> pendX = new Queue<double[]>();
            private readonly Queue<int> pendIdx = new Queue<int>();
            private readonly List<double[]> sx = new List<double[]>();
            private readonly List<double> sy = new List<double>();
            private readonly double[] zbuf = new double[2000];
            private int zn, zpos, nBars, nUpdates;
            private double lastSigma;
            public double SigmaPerBar, SigmaLong, TailZ99 = 2.576, Frac2, Frac3, Kurt;
            public bool Ready { get { return nUpdates >= 200; } }

            public BachelierModel(int horizonBars, double tickSize)
            {
                horizon = Math.Max(2, horizonBars); tick = tickSize;
                w0[2] = 1.0;                       // prior: next sigma = last 50-bar sigma
                Array.Copy(w0, w, K);
            }

            private double Rms(int n)
            {
                n = Math.Min(n, rets.Count);
                if (n <= 0) return 0;
                double ss = 0;
                for (int i = rets.Count - n; i < rets.Count; i++) ss += rets[i] * rets[i];
                return Math.Sqrt(ss / n);
            }

            private double[] Features(double r, int minuteOfDay)
            {
                double e = 0.25 * tick;
                double s50 = Math.Max(Rms(50), e);
                double[] x = new double[K];
                x[0] = 1.0;
                x[1] = Math.Log(Math.Max(Rms(10), e));
                x[2] = Math.Log(s50);
                x[3] = Math.Log(Math.Max(Rms(200), e));
                x[4] = Math.Min(5.0, Math.Abs(r) / s50);
                x[5] = Math.Sin(2 * Math.PI * minuteOfDay / 1440.0);
                x[6] = Math.Cos(2 * Math.PI * minuteOfDay / 1440.0);
                return x;
            }

            // r = the closed bar's price change in points; minuteOfDay = its time
            public void Update(double r, int minuteOfDay)
            {
                rets.Add(r); nBars++;
                if (rets.Count > 700) rets.RemoveAt(0);

                // calibration: the move versus the forecast that was made BEFORE it
                if (lastSigma > 0 && nUpdates > 0)
                {
                    zbuf[zpos] = r / lastSigma;
                    zpos = (zpos + 1) % zbuf.Length;
                    if (zn < zbuf.Length) zn++;
                }

                // learning: the sample made `horizon` bars ago now has its answer (RMS move of the bars since)
                if (pendIdx.Count > 0 && nBars - pendIdx.Peek() >= horizon)
                {
                    double[] x = pendX.Dequeue(); pendIdx.Dequeue();
                    sx.Add(x); sy.Add(Math.Log(Math.Max(Rms(horizon), 0.25 * tick)));
                    if (sx.Count > Window) { sx.RemoveAt(0); sy.RemoveAt(0); }
                    nUpdates++;
                    if (nUpdates >= 100 && nUpdates % 25 == 0) Fit();
                }

                // forecast for the coming bars
                if (rets.Count >= 60)
                {
                    double[] x = Features(r, minuteOfDay);
                    double s50 = Rms(50), yh = 0;
                    for (int k = 0; k < K; k++) yh += w[k] * x[k];
                    double sh = Math.Exp(Math.Max(-20.0, Math.Min(20.0, yh)));
                    sh = Math.Min(Math.Max(sh, 0.25 * s50), 4.0 * s50);
                    lastSigma = sh; SigmaPerBar = sh; SigmaLong = Rms(600);
                    pendX.Enqueue(x); pendIdx.Enqueue(nBars);
                    if (nBars % 25 == 0 && zn >= 200) Calibrate();
                }
            }

            private void Fit()
            {
                double[,] A = new double[K, K + 1];
                for (int s = 0; s < sx.Count; s++)
                {
                    double[] x = sx[s];
                    for (int i = 0; i < K; i++)
                    {
                        A[i, K] += x[i] * sy[s];
                        for (int j = 0; j < K; j++) A[i, j] += x[i] * x[j];
                    }
                }
                for (int i = 0; i < K; i++) { A[i, i] += Alpha; A[i, K] += Alpha * w0[i]; }
                // Gauss elimination with partial pivoting
                for (int c = 0; c < K; c++)
                {
                    int p = c;
                    for (int r = c + 1; r < K; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[p, c])) p = r;
                    if (Math.Abs(A[p, c]) < 1e-12) return;
                    if (p != c) for (int j = 0; j <= K; j++) { double t = A[c, j]; A[c, j] = A[p, j]; A[p, j] = t; }
                    for (int r = c + 1; r < K; r++)
                    {
                        double f = A[r, c] / A[c, c];
                        for (int j = c; j <= K; j++) A[r, j] -= f * A[c, j];
                    }
                }
                double[] sol = new double[K];
                for (int i = K - 1; i >= 0; i--)
                {
                    double v = A[i, K];
                    for (int j = i + 1; j < K; j++) v -= A[i, j] * sol[j];
                    sol[i] = v / A[i, i];
                }
                for (int i = 0; i < K; i++) if (double.IsNaN(sol[i]) || double.IsInfinity(sol[i])) return;
                Array.Copy(sol, w, K);
            }

            private void Calibrate()
            {
                int n = zn;
                double[] a = new double[n];
                double m2 = 0, m4 = 0; int c2 = 0, c3 = 0;
                for (int i = 0; i < n; i++)
                {
                    double z = zbuf[i];
                    a[i] = Math.Abs(z);
                    m2 += z * z; m4 += z * z * z * z;
                    if (a[i] > 2.0) c2++;
                    if (a[i] > 3.0) c3++;
                }
                Array.Sort(a);
                TailZ99 = a[(int)(0.99 * (n - 1))];
                Frac2 = c2 / (double)n; Frac3 = c3 / (double)n;
                m2 /= n; m4 /= n;
                Kurt = m2 > 0 ? m4 / (m2 * m2) - 3.0 : 0.0;
            }
        }

        private BachelierModel ml;

        private bool MlOn() { return UseMl && ml != null && ml.Ready; }
        private double SigmaH() { return ml.SigmaPerBar * Math.Sqrt(Math.Max(1, MlHorizonBars)); }      // Bachelier: spread ~ sigma x sqrt(t)

        // grid step: ML step (sigma x sqrt(horizon) x MlStepSigmas, snapped to ticks) when the model is on and trained
        private double StepFor(double basePx)
        {
            if (!MlOn()) return basePx;
            double px = MlStepSigmas * SigmaH();
            return Math.Max(MlMinStepTicks * TickSize, Math.Round(px / TickSize) * TickSize);
        }

        // how far beyond the outermost level before the grid is cut: widened by the measured fat tails
        private double BreakDist()
        {
            double d = BreakTicks * TickSize;
            if (!MlOn()) return d;
            return Math.Max(d, MlBreakSigmas * SigmaH() * Math.Max(1.0, ml.TailZ99 / 2.576));
        }

        // regime filter: do not start a grid into a volatility spike or a fat-tailed regime
        private bool MlAllowsStart()
        {
            if (!MlOn()) return true;
            if (ml.SigmaPerBar > MlMaxVolRatio * ml.SigmaLong) return false;
            if (ml.TailZ99 > MlMaxTailZ) return false;
            return true;
        }

        private string MlText()
        {
            if (!UseMl || ml == null) return "";
            if (!ml.Ready) return "\nML vol model: learning (needs about 300 bars of history)";
            return "\nML vol: sigma/bar " + ml.SigmaPerBar.ToString("F2") + " pts, x sqrt(" + MlHorizonBars + ") = " + SigmaH().ToString("F1")
                + " pts | P(|z|>2) " + (ml.Frac2 * 100).ToString("F1") + "% (normal 4.6%), 99% z " + ml.TailZ99.ToString("F2")
                + " (normal 2.58), excess kurtosis " + ml.Kurt.ToString("F1") + (MlAllowsStart() ? "" : "  -> new grids blocked");
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Fast EMA", Description = "", Order = 1, GroupName = "2. Filters")]
        public int FastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(2, 1000)]
        [Display(Name = "Slow EMA", Description = "", Order = 2, GroupName = "2. Filters")]
        public int SlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend Exit", Description = "Flatten when the fast / slow EMA no longer agree with the grid direction.", Order = 3, GroupName = "2. Filters")]
        public bool TrendExit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Short in Downtrend", Description = "Mirror grid (sell rallies) when fast < slow.", Order = 4, GroupName = "2. Filters")]
        public bool AllowShort { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Grid Step (ticks)", Description = "", Order = 1, GroupName = "1. Grid")]
        public int GridStepTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 40)]
        [Display(Name = "Levels", Description = "Number of dip-buying levels.", Order = 2, GroupName = "1. Grid")]
        public int Levels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Contracts per Level", Description = "", Order = 3, GroupName = "1. Grid")]
        public int QtyPerLevel { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Recenter Ticks", Description = "Rebuild the grid when flat and price is this far beyond it in the trend direction.", Order = 4, GroupName = "1. Grid")]
        public int RecenterTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 10000000.0)]
        [Display(Name = "Profit Lock ($, 0 = off)", Description = "Flatten and rebuild when the grid cycle is up this much.", Order = 5, GroupName = "1. Grid")]
        public double ProfitLockDollars { get; set; }


        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max Loss per Grid Cycle ($, 0 = off)", Description = "Flatten and pause when the open grid loses this much.", Order = 1, GroupName = "9. Risk")]
        public double MaxLossDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit ($, 0 = off)", Order = 2, GroupName = "9. Risk")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 5000)]
        [Display(Name = "Break Ticks", Description = "Flatten when price goes this many ticks beyond the outermost open level.", Order = 3, GroupName = "9. Risk")]
        public int BreakTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Pause Bars After Stop", Order = 4, GroupName = "9. Risk")]
        public int PauseBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Session Window", Description = "Only start grids between Start and End Time (chart time) and flatten at End Time.", Order = 5, GroupName = "9. Risk")]
        public bool UseSession { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Start Time (HHMM)", Order = 6, GroupName = "9. Risk")]
        public int StartTime { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "End Time (HHMM)", Order = 7, GroupName = "9. Risk")]
        public int EndTime { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading", Description = "Off = no orders.", Order = 8, GroupName = "9. Risk")]
        public bool EnableTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Non-Sim Account", Description = "Keep OFF.", Order = 9, GroupName = "9. Risk")]
        public bool AllowNonSimAccount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Debug Log", Description = "Write state changes, a heartbeat every 500 bars and every order to the NinjaScript Output window.", Order = 10, GroupName = "9. Risk")]
        public bool DebugLog { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade Historical Bars", Description = "OFF (recommended on a chart / Playback): no orders while NinjaTrader replays history, so no historical orders have to be carried over to live. ON only for Strategy Analyzer backtests.", Order = 11, GroupName = "9. Risk")]
        public bool TradeHistory { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use ML Volatility Model", Description = "Bachelier-style online model: forecasts how far price will move (sigma x sqrt(t)), measures the real fat tails. Sizes the grid step, widens the break guard and blocks new grids in volatility spikes / fat-tailed regimes. OFF = the plain strategy.", Order = 1, GroupName = "8. ML (Bachelier)")]
        public bool UseMl { get; set; }

        [NinjaScriptProperty]
        [Range(2, 500)]
        [Display(Name = "Horizon (bars)", Description = "The model predicts the RMS move over this many bars; spread scales with its square root.", Order = 2, GroupName = "8. ML (Bachelier)")]
        public int MlHorizonBars { get; set; }

        [NinjaScriptProperty]
        [Range(0.05, 10.0)]
        [Display(Name = "Step = sigma x sqrt(horizon) x", Description = "Grid step as a fraction of the predicted horizon spread.", Order = 3, GroupName = "8. ML (Bachelier)")]
        public double MlStepSigmas { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Min Step with ML (ticks)", Order = 4, GroupName = "8. ML (Bachelier)")]
        public int MlMinStepTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 20.0)]
        [Display(Name = "Break Distance (x horizon spread)", Description = "Flatten when price is this many horizon-sigmas (scaled up by measured tail fatness) beyond the outermost level. Never less than Break Ticks.", Order = 5, GroupName = "8. ML (Bachelier)")]
        public double MlBreakSigmas { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "Block Starts if Vol > x Long-run", Description = "No new grid when predicted sigma is above this multiple of the 600-bar sigma.", Order = 6, GroupName = "8. ML (Bachelier)")]
        public double MlMaxVolRatio { get; set; }

        [NinjaScriptProperty]
        [Range(2.0, 20.0)]
        [Display(Name = "Block Starts if 99% Tail z >", Description = "No new grid when the measured 99th percentile of |move / forecast| exceeds this (a normal distribution gives 2.58).", Order = 7, GroupName = "8. ML (Bachelier)")]
        public double MlMaxTailZ { get; set; }
        #endregion
    }
}
