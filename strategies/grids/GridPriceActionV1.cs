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

// GridPriceActionV1 -- Price action grid -- NinjaTrader 8 strategy (UNPROVEN, sim / playback only)
//
//  Instead of equal spacing, the buy levels are the nearest swing LOWS below price and the sell levels the nearest swing
//  HIGHS above it (pivots with Swing Strength bars on each side). Each level exits at the next level toward the middle between
//  the nearest support and resistance. Structure Mode FollowStructure: in an uptrend (higher swing highs AND higher swing
//  lows) only the buy side is used, in a downtrend only the sell side, otherwise both. The grid is rebuilt every Rebuild Bars
//  when flat. NOT implemented: the breakout variant (buy stops above / sell stops below) from the description.
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
    public enum GridPriceActionV1Mode { Both, FollowStructure }

    public class GridPriceActionV1 : Strategy
    {
        private const string LogTag = "[GridPriceActionV1]";

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


        // The account (not just this strategy) must be flat before a grid / cycle starts. This replaces
        // StartBehavior.WaitUntilFlat, which made NinjaTrader try to cancel live orders at start and fail in Playback.
        private bool AccountFlat()
        {
            try { return PositionAccount == null || PositionAccount.MarketPosition == MarketPosition.Flat; }
            catch { return true; }
        }

        private int Warmup { get { return Lookback + SwingStrength + 5; } }
        private double Rnd(double v) { return Instrument.MasterInstrument.RoundToTickSize(v); }
        // Printing / drawing is skipped while NinjaTrader replays history: it makes chart loading very slow.
        private void Log(string m) { if (State == State.Realtime) Print(LogTag + " " + Time[0] + " " + m); }

        protected override void OnStateChange()
        {
            if (DebugLog && State != State.SetDefaults) Print(LogTag + " state -> " + State + (CurrentBars != null && CurrentBars.Length > 0 ? ", bars " + CurrentBars[0] : ""));
            if (State == State.SetDefaults)
            {
                Description = "Price action grid: grid levels at recent swing lows (buy) and swing highs (sell); optionally only with the swing structure (unproven).";
                Name        = "GridPriceActionV1";
                Calculate   = Calculate.OnPriceChange;
                IsUnmanaged = true;
                IsOverlay   = true;
                StartBehavior = StartBehavior.ImmediatelySubmit;
                BarsRequiredToTrade = 30;
                SwingStrength = 3; Lookback = 150; NumLevels = 3; MinGapTicks = 20; QtyPerLevel = 1;
                StructureMode = GridPriceActionV1Mode.FollowStructure; RebuildBars = 50;
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
            }
            else if (State == State.DataLoaded)
            {

                if (Account != null && !AllowNonSimAccount && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                    && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
                {
                    tradingAllowed = false;
                    blockReason = "account " + Account.Name + " is not a Sim / Playback account -- live trading blocked";
                    Print(LogTag + " " + blockReason);
                }
                Print(LogTag + " " + Instrument.FullName + " " + BarsPeriod + ": trading " + EnableTrading);
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
            if (BarsInProgress != 0 || CurrentBar < Math.Max(BarsRequiredToTrade, Warmup)) return;
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
            if (!gridOn && !paused && flat && s == 0 && !dayStopped && inSession && newBar && AccountFlat()) TryStart();
            if (newBar && gridOn) StrategyBar();
            if (newBar) DrawStats(cyc);
        }

        // ---- strategy specific --------------------------------------------------------------------------------
        private bool AllowStart()
        {
            return true;
        }

        private bool BuildGrid()
        {
            int k = SwingStrength;
            int lb = Math.Min(Lookback, CurrentBar - k - 1);
            var highs = new List<double>(); var lows = new List<double>();        // newest first
            for (int i = k + 1; i <= lb; i++)
            {
                bool ph = true, pl = true;
                for (int j = 1; j <= k; j++)
                {
                    if (High[i] <= High[i - j] || High[i] <= High[i + j]) ph = false;
                    if (Low[i] >= Low[i - j] || Low[i] >= Low[i + j]) pl = false;
                }
                if (ph) highs.Add(High[i]);
                if (pl) lows.Add(Low[i]);
            }
            bool up = highs.Count >= 2 && lows.Count >= 2 && highs[0] > highs[1] && lows[0] > lows[1];
            bool down = highs.Count >= 2 && lows.Count >= 2 && highs[0] < highs[1] && lows[0] < lows[1];
            double px = Close[0], gap = MinGapTicks * TickSize;
            var sup = Thin(lows.Where(v => v < px).Distinct().OrderByDescending(v => v).ToList(), NumLevels, gap);
            var res = Thin(highs.Where(v => v > px).Distinct().OrderBy(v => v).ToList(), NumLevels, gap);
            if (StructureMode == GridPriceActionV1Mode.FollowStructure)
            {
                if (up) res.Clear();
                else if (down) sup.Clear();
            }
            if (sup.Count == 0 && res.Count == 0) return false;
            double mid;
            if (sup.Count > 0 && res.Count > 0)
            {
                if (res[0] - sup[0] < 2 * gap) return false;
                mid = (sup[0] + res[0]) / 2.0;
            }
            else mid = sup.Count > 0 ? px + gap : px - gap;
            gridDir = up ? 1 : down ? -1 : 0;
            return BuildSides(mid, sup, QtyPerLevel, mid, res, QtyPerLevel);
        }

        private bool RebuildDue()
        {
            return barsSinceBuild >= RebuildBars;
        }

        private void StrategyBar()
        {

        }

        private void ExtraGuard(double cyc)
        {

        }

        private List<double> Thin(List<double> src, int n, double gap)
        {
            var o = new List<double>();
            foreach (double v in src)
            {
                if (o.Count >= n) break;
                if (o.Count == 0 || Math.Abs(v - o[o.Count - 1]) >= gap) o.Add(v);
            }
            return o;
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
            if (s > 0 && N > 0 && px <= Lb[N] - BreakTicks * TickSize) { Flatten("range break (long side)", true); return; }
            if (s < 0 && M > 0 && px >= Ls[M] + BreakTicks * TickSize) { Flatten("range break (short side)", true); return; }
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
                dnOrd = SubmitOrderUnmanaged(0, s < 0 ? OrderAction.BuyToCover : OrderAction.Buy, OrderType.Limit, q, Rnd(px), 0, oco, "GD" + ordCounter);
            if (UpOrder(out q, out px) && q > 0)
                upOrd = SubmitOrderUnmanaged(0, s > 0 ? OrderAction.Sell : OrderAction.SellShort, OrderType.Limit, q, Rnd(px), 0, oco, "GU" + ordCounter);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filledQty,
                                              double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (orderState == OrderState.Rejected && order.Name != null && (order.Name.StartsWith("GD") || order.Name.StartsWith("GU")))
            {
                Print(LogTag + " order " + order.Name + " REJECTED: " + error + " " + comment);
                if (gridOn) Flatten("order rejected", true);
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
                + "\nUNPROVEN -- sim only. Commissions not included.";
            Draw.TextFixed(this, "GRIDstats", text, TextPosition.TopLeft, Brushes.White,
                new SimpleFont("Arial", 11), Brushes.Transparent, Brushes.Black, 70);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Swing Strength (bars each side)", Description = "", Order = 1, GroupName = "1. Grid")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(20, 2000)]
        [Display(Name = "Lookback (bars)", Description = "", Order = 2, GroupName = "1. Grid")]
        public int Lookback { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Levels per Side", Description = "", Order = 3, GroupName = "1. Grid")]
        public int NumLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5000)]
        [Display(Name = "Min Gap between Levels (ticks)", Description = "", Order = 4, GroupName = "1. Grid")]
        public int MinGapTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Contracts per Level", Description = "", Order = 5, GroupName = "1. Grid")]
        public int QtyPerLevel { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Structure Mode", Description = "Both = buy supports and sell resistances; FollowStructure = only with the swing trend.", Order = 6, GroupName = "1. Grid")]
        public GridPriceActionV1Mode StructureMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Rebuild Bars", Description = "", Order = 7, GroupName = "1. Grid")]
        public int RebuildBars { get; set; }


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
        #endregion
    }
}
