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

// GridAntiMartingaleV1 -- Anti-martingale grid -- NinjaTrader 8 strategy (UNPROVEN, sim / playback only)
//
//  Opposite of the martingale: the entries of a cycle are equal size (Level Size Multiplier 1) and a losing cycle does NOT
//  increase size. Instead the loss is added to a 'loss bank' and the NEXT take profit is widened by bank / (tick value x
//  contracts) ticks (capped by Max Take Profit), so one later win repays it and adds the normal profit. Size is increased only
//  after wins: Add Contracts per Win Streak adds that many contracts per winning cycle in a row, reset by a loss.
//  Risk: after losses the widened target may never be reached and the account sits negative -- the Time Stop / Max Loss end the cycle.
//
//  How the basket works (UNMANAGED orders): one cycle at a time in ONE direction.
//   1. Market entry of the cycle's first order (level 1).
//   2. Limit orders for levels 2..Levels, each Step Ticks (x Step Growth) further against the position.
//   3. One take-profit limit for the WHOLE position at the average price + TP ticks; it is moved every time a level fills.
//   4. The cycle ends at the take profit, at Max Loss ($), at the optional Time Stop (bars), or at the session end.
//  Danger: a basket can pile up size in a trend; Max Total Contracts, Max Loss and the Time Stop exist for that.
//  Backtests only see one fill per bar without tick data. Partial fills are not handled specially; use whole contracts.
//  Safety: Enable Trading can be switched off; in real time only Sim / Playback accounts unless allowed. UNPROVEN.

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum GridAntiMartingaleDirection { Long, Short, FollowEma }

    public class GridAntiMartingaleV1 : Strategy
    {
        private const string LogTag = "[GridAntiMartingaleV1]";

        private EMA emaF, emaS;
        private Order tpOrd;
        private readonly List<Order> lvlOrds = new List<Order>();
        private int dirSign, levelsFilled, cycleBars, pauseLeft, cycles, wins, lossStreak, winStreak, cycleBaseQty;
        private bool inCycle, paused, closing, pauseOnEnd, dayStopped, tradingAllowed = true;
        private double entry1, lossBank, cycleBase, dayBase, curTpTicks;
        private int accPos;
        private double accAvg, realized;
        private DateTime dayDate = DateTime.MinValue;
        private string blockReason = "", lastNote = "idle";

        // The account (not just this strategy) must be flat before a grid / cycle starts. This replaces
        // StartBehavior.WaitUntilFlat, which made NinjaTrader try to cancel live orders at start and fail in Playback.
        private bool AccountFlat()
        {
            try { return PositionAccount == null || PositionAccount.MarketPosition == MarketPosition.Flat; }
            catch { return true; }
        }

        private double Rnd(double v) { return Instrument.MasterInstrument.RoundToTickSize(v); }
        // Printing / drawing is skipped while NinjaTrader replays history: it makes chart loading very slow.
        private void Log(string m) { if (State == State.Realtime) Print(LogTag + " " + Time[0] + " " + m); }

        protected override void OnStateChange()
        {
            if (DebugLog && State != State.SetDefaults) Print(LogTag + " state -> " + State + (CurrentBars != null && CurrentBars.Length > 0 ? ", bars " + CurrentBars[0] : ""));
            if (State == State.SetDefaults)
            {
                Description = "Anti-martingale grid: constant-size basket whose take profit is widened after losses; size grows only on winning streaks (unproven).";
                Name        = "GridAntiMartingaleV1";
                Calculate   = Calculate.OnPriceChange;
                IsUnmanaged = true;
                IsOverlay   = true;
                StartBehavior = StartBehavior.ImmediatelySubmit;
                BarsRequiredToTrade = 30;
                RealtimeErrorHandling = RealtimeErrorHandling.IgnoreAllErrors;   // rejections are handled in OnOrderUpdate
                Direction = GridAntiMartingaleDirection.FollowEma; FastPeriod = 12; SlowPeriod = 144;
                Levels = 4; StepTicks = 40; StepGrowth = 1.0;
                BaseQty = 1; LevelMult = 1.0; CycleLossMult = 1.0; MaxLossStreak = 20; AddQtyPerWin = 1; MaxQtyPerOrder = 5; MaxTotalContracts = 12;
                TpTicks = 40; RecoupLosses = true; MaxTpTicks = 300;
                MaxLossDollars = 1500; DailyLossLimit = 2500; TimeStopBars = 90;
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
                emaF = EMA(Close, FastPeriod);
                emaS = EMA(Close, SlowPeriod);
                ml = UseMl ? new BachelierModel(MlHorizonBars, TickSize) : null;
                if (Account != null && !AllowNonSimAccount && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                    && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
                {
                    tradingAllowed = false;
                    blockReason = "account " + Account.Name + " is not a Sim / Playback account -- live trading blocked";
                    Print(LogTag + " " + blockReason);
                }
                Print(LogTag + " build 7 | StartBehavior=" + StartBehavior + " | TradeHistory=" + TradeHistory + " | " + Instrument.FullName + " " + BarsPeriod + ": direction " + Direction + ", up to " + Levels + " levels, trading " + EnableTrading);
            }
            else if (State == State.Realtime)
            {
                // Orders placed while NinjaTrader replayed history become LIVE orders now. Swap the stored references,
                // otherwise changing / cancelling them later disables the strategy ("modify a historical order that has transitioned to live").
                try
                {
                    if (tpOrd != null) tpOrd = GetRealtimeOrder(tpOrd);
                    for (int i = 0; i < lvlOrds.Count; i++)
                        if (lvlOrds[i] != null) lvlOrds[i] = GetRealtimeOrder(lvlOrds[i]);
                    lvlOrds.RemoveAll(o => o == null);
                }
                catch (Exception ex) { Print(LogTag + " realtime order transition: " + ex.Message); tpOrd = null; lvlOrds.Clear(); }
            }
            else if (State == State.Terminated)
            {
                try
                {
                    if (Position != null && Position.MarketPosition != MarketPosition.Flat)
                        Print(LogTag + " WARNING: stopped with an open position (" + Position.MarketPosition + " " + Position.Quantity
                            + ") -- check the Positions and Orders tabs; the take profit may have been cancelled.");
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
            if (BarsInProgress != 0 || CurrentBar < Math.Max(BarsRequiredToTrade, SlowPeriod + 2)) return;
            if (State == State.Historical && !TradeHistory) return;      // no historical orders: they cannot be carried over to live cleanly
            DateTime t = Time[0];
            if (t.Date != dayDate) { dayDate = t.Date; dayBase = realized; dayStopped = false; }
            bool newBar = IsFirstTickOfBar;
            if (newBar && paused && --pauseLeft <= 0) paused = false;

            bool flat = Position.MarketPosition == MarketPosition.Flat;
            double cyc = inCycle ? (realized - cycleBase) + Unrealized() : 0;

            if (!EnableTrading || (State == State.Realtime && !tradingAllowed)) { if (newBar) DrawStats(cyc); return; }

            int hm = t.Hour * 100 + t.Minute;
            bool inSession = !UseSession || (hm >= StartTime && hm < EndTime);
            if (UseSession && hm >= EndTime && inCycle) CloseCycle("session end", false);
            double dayPnl = (realized - dayBase) + Unrealized();
            if (DailyLossLimit > 0 && dayPnl <= -DailyLossLimit && !dayStopped) { dayStopped = true; if (inCycle) CloseCycle("daily loss limit", false); }

            if (inCycle)
            {
                if (newBar) cycleBars++;
                if (!closing)
                {
                    if (MaxLossDollars > 0 && cyc <= -MaxLossDollars) CloseCycle("max loss", true);
                    else if (TimeStopBars > 0 && cycleBars >= TimeStopBars) CloseCycle("time stop", true);
                }
            }
            else if (flat && !paused && !dayStopped && inSession && newBar && AccountFlat() && MlAllowsStart()) TryStart();

            if (newBar) DrawStats(cyc);
        }

        private int CurrentBaseQty()
        {
            double q = BaseQty * Math.Pow(CycleLossMult, lossStreak) + winStreak * AddQtyPerWin;
            return Math.Max(1, Math.Min((int)Math.Round(q), MaxQtyPerOrder));
        }

        private int LevelQty(int k)
        {
            return Math.Max(1, Math.Min((int)Math.Round(cycleBaseQty * Math.Pow(LevelMult, k - 1)), MaxQtyPerOrder));
        }

        private void TryStart()
        {
            int d = Direction == GridAntiMartingaleDirection.Long ? 1 : Direction == GridAntiMartingaleDirection.Short ? -1 : (emaF[0] > emaS[0] ? 1 : emaF[0] < emaS[0] ? -1 : 0);
            if (d == 0) return;
            dirSign = d;
            cycleBaseQty = CurrentBaseQty();
            inCycle = true; closing = false; pauseOnEnd = false; levelsFilled = 0; cycleBars = 0; cycleBase = realized;
            tpOrd = null; lvlOrds.Clear();
            SubmitOrderUnmanaged(0, d > 0 ? OrderAction.Buy : OrderAction.SellShort, OrderType.Market, cycleBaseQty, 0, 0, "", "B1");
            lastNote = "cycle started " + (d > 0 ? "LONG" : "SHORT") + " x" + cycleBaseQty + " (loss streak " + lossStreak + ", win streak " + winStreak + ", loss bank $" + lossBank.ToString("F0") + ")";
            Log(lastNote);
        }

        private void PlaceLevels()
        {
            double cum = 0;
            int total = cycleBaseQty;
            for (int k = 2; k <= Levels; k++)
            {
                cum += StepFor(StepTicks * TickSize) * Math.Pow(StepGrowth, k - 2);
                int q = LevelQty(k);
                if (total + q > MaxTotalContracts) break;
                total += q;
                Order od = SubmitOrderUnmanaged(0, dirSign > 0 ? OrderAction.Buy : OrderAction.SellShort, OrderType.Limit, q,
                    Rnd(entry1 - dirSign * cum), 0, "", "B" + k);
                if (od != null) lvlOrds.Add(od);
            }
        }

        // one take profit for the whole position, at the average price
        private void UpdateTp()
        {
            if (accPos == 0) return;
            double tickValue = Instrument.MasterInstrument.PointValue * TickSize;
            double ticks = TpTicks * (StepFor(StepTicks * TickSize) / (StepTicks * TickSize));
            if (RecoupLosses && lossBank > 0) ticks += lossBank / (tickValue * Math.Abs(accPos));
            ticks = Math.Min(ticks, MaxTpTicks);
            curTpTicks = ticks;
            double px = Rnd(accAvg + dirSign * ticks * TickSize);
            int q = Math.Abs(accPos);
            if (tpOrd != null && IsLive(tpOrd)) ChangeOrder(tpOrd, q, px, 0);
            else tpOrd = SubmitOrderUnmanaged(0, dirSign > 0 ? OrderAction.Sell : OrderAction.BuyToCover, OrderType.Limit, q, px, 0, "", "BTP");
        }

        private static bool IsLive(Order o)
        {
            return o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.PartFilled
                || o.OrderState == OrderState.Submitted || o.OrderState == OrderState.ChangePending || o.OrderState == OrderState.ChangeSubmitted;
        }

        private void CancelLevels()
        {
            foreach (Order od in lvlOrds) if (od != null && IsLive(od)) CancelOrder(od);
            lvlOrds.Clear();
        }

        private void CloseCycle(string reason, bool pause)
        {
            if (closing) return;
            closing = true; pauseOnEnd = pause;
            CancelLevels();
            if (tpOrd != null && IsLive(tpOrd)) CancelOrder(tpOrd);
            if (Position.MarketPosition == MarketPosition.Long)
                SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, Position.Quantity, 0, 0, "", "BX");
            else if (Position.MarketPosition == MarketPosition.Short)
                SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, Position.Quantity, 0, 0, "", "BX");
            lastNote = "closing cycle: " + reason;
            Log("CLOSE -- " + reason);
            if (Position.MarketPosition == MarketPosition.Flat && accPos == 0) EndCycle();
        }

        private void EndCycle()
        {
            double res = realized - cycleBase;
            cycles++;
            if (res > 0) { wins++; winStreak++; lossStreak = 0; lossBank = Math.Max(0, lossBank - res); }
            else { lossStreak++; winStreak = 0; lossBank += -res; if (lossStreak > MaxLossStreak) lossStreak = 0; }
            CancelLevels();
            if (tpOrd != null && IsLive(tpOrd)) CancelOrder(tpOrd);
            tpOrd = null;
            inCycle = false; closing = false;
            if (pauseOnEnd) { paused = true; pauseLeft = PauseBars; }
            lastNote = "cycle ended " + (res >= 0 ? "+" : "") + res.ToString("F0") + "$ (levels filled " + (levelsFilled + 1) + ")";
            Log(lastNote);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filledQty,
                                              double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order.Name == "BTP") tpOrd = order;
            if (orderState == OrderState.Rejected && order.Name != null)
            {
                Print(LogTag + " REJECTED " + order.Name + " " + order.OrderAction + " x" + quantity + " " + order.OrderType + " @ " + limitPrice
                    + " | error " + error + " | " + comment + " | own position " + accPos
                    + ", strategy position " + Position.MarketPosition + " " + Position.Quantity + " | account " + (Account != null ? Account.Name : "?"));
                if (order.Name == "B1") { inCycle = false; closing = false; }
                else if (order.Name == "BTP" && inCycle) CloseCycle("take profit order rejected", true);
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
                if (o.OrderState != OrderState.Filled) return;
                string n = o.Name ?? "";
                if (n == "B1") { entry1 = o.AverageFillPrice; PlaceLevels(); UpdateTp(); }
                else if (n.Length >= 2 && n[0] == 'B' && char.IsDigit(n[1])) { levelsFilled++; UpdateTp(); }
                if (accPos == 0 && inCycle) EndCycle();
            }
            catch (Exception ex) { Print(LogTag + " execution error: " + ex.Message); }
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
                int closingQty = Math.Min(rem, Math.Abs(accPos));
                realized += (price - accAvg) * Math.Sign(accPos) * closingQty * pv;
                accPos += sg * closingQty; rem -= closingQty;
                if (accPos == 0) accAvg = 0;
                if (rem > 0) { accAvg = price; accPos = sg * rem; }
            }
        }

        private double Unrealized()
        {
            return accPos == 0 ? 0 : (Close[0] - accAvg) * accPos * Instrument.MasterInstrument.PointValue;
        }

        private void DrawStats(double cyc)
        {
            if (State != State.Realtime) return;
            string text = Name + "  |  " + (!EnableTrading ? "OFF" : (State == State.Realtime && !tradingAllowed) ? "BLOCKED" : inCycle ? "IN CYCLE" : paused ? "PAUSED" : "WAITING")
                + "\n" + (inCycle ? (dirSign > 0 ? "LONG" : "SHORT") + " " + accPos + " @ " + accAvg.ToString("F2") + ", levels filled " + (levelsFilled + 1) + "/" + Levels
                    + ", TP " + curTpTicks.ToString("F0") + " ticks, cycle $" + cyc.ToString("F0") : "flat")
                + "\nloss streak " + lossStreak + ", win streak " + winStreak + ", loss bank $" + lossBank.ToString("F0") + "  |  realized $" + realized.ToString("F0")
                + "\ncycles " + cycles + " (" + wins + " won)  |  last: " + lastNote
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
        [Display(Name = "Direction", Description = "Long, Short, or FollowEma (long when fast EMA > slow EMA, short when below).", Order = 1, GroupName = "1. Basket")]
        public GridAntiMartingaleDirection Direction { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Fast EMA (FollowEma)", Description = "", Order = 2, GroupName = "1. Basket")]
        public int FastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(2, 1000)]
        [Display(Name = "Slow EMA (FollowEma)", Description = "", Order = 3, GroupName = "1. Basket")]
        public int SlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Levels (max entries per cycle)", Description = "", Order = 4, GroupName = "1. Basket")]
        public int Levels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Step (ticks)", Description = "Distance between entries (against the position).", Order = 5, GroupName = "1. Basket")]
        public int StepTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Step Growth (x per level)", Description = "1 = equal spacing; 1.5 widens each step by 50 percent.", Order = 6, GroupName = "1. Basket")]
        public double StepGrowth { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Base Contracts", Description = "", Order = 1, GroupName = "2. Sizing")]
        public int BaseQty { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Level Size Multiplier", Description = "Size of each next level in a cycle = previous x this (2 = double down; 1 = equal).", Order = 2, GroupName = "2. Sizing")]
        public double LevelMult { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 5.0)]
        [Display(Name = "Size Multiplier after a Losing Cycle", Description = "Next cycle's base size = base x this ^ losing streak (2 = classic martingale).", Order = 3, GroupName = "2. Sizing")]
        public double CycleLossMult { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Loss Streak", Description = "After this many losing cycles in a row the size resets to the base.", Order = 4, GroupName = "2. Sizing")]
        public int MaxLossStreak { get; set; }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Add Contracts per Win Streak", Description = "Anti-martingale sizing: +N contracts for each winning cycle in a row (resets on a loss).", Order = 5, GroupName = "2. Sizing")]
        public int AddQtyPerWin { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Max Contracts per Order", Description = "", Order = 6, GroupName = "2. Sizing")]
        public int MaxQtyPerOrder { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Max Total Contracts", Description = "Levels that would exceed this are not placed.", Order = 7, GroupName = "2. Sizing")]
        public int MaxTotalContracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Take Profit (ticks from average)", Description = "", Order = 1, GroupName = "3. Exit")]
        public int TpTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Recoup Losses in TP", Description = "After a losing cycle the next take profit is widened so one win also repays the loss bank.", Order = 2, GroupName = "3. Exit")]
        public bool RecoupLosses { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Max Take Profit (ticks)", Description = "Cap for the widened take profit.", Order = 3, GroupName = "3. Exit")]
        public int MaxTpTicks { get; set; }


        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Max Loss per Cycle ($, 0 = off)", Description = "Close the whole basket at this loss.", Order = 1, GroupName = "9. Risk")]
        public double MaxLossDollars { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit ($, 0 = off)", Order = 2, GroupName = "9. Risk")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Time Stop (bars, 0 = off)", Description = "Close the basket after this many bars instead of waiting for a price stop.", Order = 3, GroupName = "9. Risk")]
        public int TimeStopBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Pause Bars After Stop", Order = 4, GroupName = "9. Risk")]
        public int PauseBars { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Use Session Window", Description = "Only start cycles between Start and End Time (chart time) and close at End Time.", Order = 5, GroupName = "9. Risk")]
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
