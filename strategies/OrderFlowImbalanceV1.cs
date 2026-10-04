#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// OrderFlowImbalanceV1 -- trades WITH a very large one-sided imbalance in the NQ order flow, on MNQ.
//
//  The rule is the frozen, pre-registered one from renko_ml/imbalance_lab.py (NQ trades Jun 11 - Oct 1 2026):
//   - footprint of the NQ order flow in blocks of 500 trades (= a 500-tick bar), levels of 8 ticks (2 points)
//   - BUY signal the moment a level's buying (at the ask) reaches >= Min Imbalance Volume (300) contracts AND
//     >= Ratio (5) x the selling (at the bid) one level BELOW (diagonal), that weaker side having >= 2 contracts,
//     while the block's delta is positive. SELL mirrored. Each level fires once per block.
//   - enter at market WITH the imbalance; stop 40 ticks, target 60 ticks; out at market after 60 minutes
//   - one position at a time
//  Test result: RTH Jun-Aug +4.38 pts/trade after 1.25 pts costs (40 trades, t 2.3), Sep +1.25 (16 trades, not
//  significant); OVERNIGHT Jun-Aug -2.60 pts/trade (26 trades, below random). Treat it as UNPROVEN -- sim only.
//  Session = AllDay trades overnight too (an untested hypothesis); the News Blackout skips 08:20-08:45 ET.
//
//  Reverse Signals (new): when ON, the trade direction is flipped -- a BUY signal enters SHORT, a SELL signal
//  enters LONG. Stop / target / time exit are applied to the position actually taken. UNTESTED -- the frozen rule
//  above is the non-reversed one.
//
//  Setup: put it on any MNQ chart (1 Minute is fine). It subscribes to the Signal Instrument ("Auto" = the full-size
//  contract matching the chart's instrument and expiry, e.g. MNQ 12-26 -> NQ 12-26) for the order flow and trades the chart's instrument. Needs REAL-TIME data for NQ (a "Delayed" feed delays the
//  signals). Chart time zone must be US Eastern for the session / news times. History is not traded.
//  Safety: Enable Trading OFF by default (signals only logged); refuses non-"Sim" accounts unless allowed.
//  Log: Documents\NinjaTrader 8\OrderFlowImbalanceV1_log.csv (every signal, entry and exit).

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum OfiSessionMode
    {
        RthOnly,
        AllDay
    }

    public enum OfiBlockSource
    {
        ChartBars,      // one footprint per chart bar (the signal instrument built with the chart's bar type)
        FixedTrades     // one footprint per Block Size trades (the tested 500-trade blocks)
    }

    public class OrderFlowImbalanceV1 : Strategy
    {
        private const string LogTag = "[OFI-V1]";
        private const string SigLong = "OFI_L", SigShort = "OFI_S";

        // footprint of the current block of the signal instrument's trades
        private readonly Dictionary<long, long> blockBid = new Dictionary<long, long>();
        private readonly Dictionary<long, long> blockAsk = new Dictionary<long, long>();
        private readonly HashSet<long> firedBuy = new HashSet<long>(), firedSell = new HashSet<long>();
        private readonly HashSet<long> qualBuy = new HashSet<long>(), qualSell = new HashSet<long>();   // imbalanced levels this block
        private long blockDelta;
        private int blockTrades;
        private DateTime lastTradeTime = DateTime.MinValue;
        private double sigBid, sigAsk, sigLastPrice;
        private int sigLastSide;
        private double sigTickSize;

        private DateTime entryTime = DateTime.MinValue;
        private double entryPrice;
        private bool inTradeLong, entryPending;
        private bool tradingAllowed = true;
        private string blockReason = "";
        private double dailyPnL;
        private DateTime pnlDate = DateTime.MinValue;
        private int signalsToday, signalsTotal, trades, wins;
        private double netPts;
        private string lastSignal = "none yet";
        private bool statsDirty = true, errorLogged;
        private string logPath;
        private string signalName;
        private int lastSigBar = int.MinValue;

        // "Auto" (or blank): the full-size contract of the chart's instrument with the SAME expiry --
        // MNQ 12-26 -> NQ 12-26, NQ 09-26 -> NQ 09-26, MES 12-26 -> ES 12-26. Anything else is used as typed.
        private string ResolveSignalInstrument()
        {
            string s = (SignalInstrument ?? "").Trim();
            if (s.Length > 0 && !s.Equals("Auto", StringComparison.OrdinalIgnoreCase))
                return s;
            string master = Instrument.MasterInstrument.Name;
            switch (master.ToUpperInvariant())
            {
                case "MNQ": master = "NQ"; break;
                case "MES": master = "ES"; break;
                case "M2K": master = "RTY"; break;
                case "MYM": master = "YM"; break;
            }
            string full = Instrument.FullName;               // e.g. "MNQ 12-26"
            int sp = full.IndexOf(' ');
            return sp > 0 ? master + full.Substring(sp) : master;
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Trades with very large one-sided NQ order-flow imbalances on MNQ (pre-registered rule, unproven -- sim).";
                Name         = "OrderFlowImbalanceV1";
                Calculate    = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                // Rejections are handled in OnOrderUpdate (a rejected stop / target -> flatten at market and carry on)
                // instead of NinjaTrader terminating the strategy.
                RealtimeErrorHandling        = RealtimeErrorHandling.IgnoreAllErrors;
                StartBehavior                = StartBehavior.WaitUntilFlat;

                SignalInstrument   = "Auto";
                BlockTrades        = 500;
                LevelTicks         = 8;
                MinImbalanceVolume = 300;
                ImbalanceRatio     = 5.0;
                MinWeakSide        = 2;
                MinStackedLevels   = 1;
                BlockSource        = OfiBlockSource.ChartBars;
                ReverseSignals     = false;
                StopTicks          = 40;
                TargetTicks        = 60;
                TimeExitMinutes    = 60;
                Session            = OfiSessionMode.RthOnly;
                RthStart           = 930;
                RthEnd             = 1600;
                NewsBlackout       = true;
                Contracts          = 1;
                DailyLossLimit     = 500;
                EnableTrading      = false;
                AllowNonSimAccount = false;
            }
            else if (State == State.Configure)
            {
                // The signal instrument's trades arrive through OnMarketData (BarsInProgress 1).
                signalName = ResolveSignalInstrument();
                if (BlockSource == OfiBlockSource.ChartBars)
                    AddDataSeries(signalName, BarsPeriod);                    // same bar type / size as the chart
                else
                    AddDataSeries(signalName, BarsPeriodType.Tick, BlockTrades);
            }
            else if (State == State.DataLoaded)
            {
                sigTickSize = BarsArray[1].Instrument.MasterInstrument.TickSize;
                logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "OrderFlowImbalanceV1_log.csv");
                // Market Replay (Playback connection) is simulated too: allow "Playback..." accounts as well as "Sim...".
                if (Account != null && !AllowNonSimAccount && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                    && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
                {
                    tradingAllowed = false;
                    blockReason = $"account {Account.Name} is not a Sim account -- trading blocked";
                    Print($"{LogTag} {blockReason}");
                }
                Print($"{LogTag} signal {signalName} -> trading {Instrument.FullName}; blocks = {(BlockSource == OfiBlockSource.ChartBars ? "chart bars (" + BarsPeriod + ")" : BlockTrades + " trades")}, levels {LevelTicks} ticks, "
                    + $">= {MinImbalanceVolume} contracts at >= {ImbalanceRatio}x (weak >= {MinWeakSide}), stack >= {MinStackedLevels}; stop {StopTicks} / target {TargetTicks} ticks, "
                    + $"{TimeExitMinutes} min; session {Session}, news blackout {NewsBlackout}; reverse {ReverseSignals}; EnableTrading {EnableTrading}; account {(Account != null ? Account.Name : "?")}");
            }
            else if (State == State.Terminated)
            {
                // Disabled / removed while holding a position: NinjaTrader may cancel the stop and target and leave the
                // position open on the account. Make that impossible to miss.
                try
                {
                    if (Position != null && Position.MarketPosition != MarketPosition.Flat && logPath != null)
                    {
                        string side = Position.MarketPosition == MarketPosition.Long ? "LONG" : "SHORT";
                        Print($"{LogTag} WARNING: stopped while {side} {Position.Quantity} @ {Position.AveragePrice:F2}. "
                            + "Check the account's Positions and Orders tabs -- the stop / target may have been cancelled.");
                        Log("TERMINATED-WITH-OPEN-POSITION", side, Position.AveragePrice, 0, 0,
                            $"qty {Position.Quantity}; check the account for an unprotected position");
                    }
                }
                catch { }
            }
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 1) return;
            if (statsDirty) DrawStats();
        }

        // ---- the signal instrument's trades, one by one ----------------------------------------------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            try
            {
                if (BarsInProgress != 1 || State != State.Realtime) return;
                if (e.MarketDataType == MarketDataType.Bid) { sigBid = e.Price; return; }
                if (e.MarketDataType == MarketDataType.Ask) { sigAsk = e.Price; return; }
                if (e.MarketDataType != MarketDataType.Last) return;

                DateTime t = e.Time;
                if (t.Date != pnlDate) { pnlDate = t.Date; dailyPnL = 0; signalsToday = 0; statsDirty = true; }

                // time exit (checked on every signal-instrument trade)
                if (Position.MarketPosition != MarketPosition.Flat && entryTime != DateTime.MinValue
                    && (t - entryTime).TotalMinutes >= TimeExitMinutes)
                {
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong(0, Position.Quantity, "OFI time exit", SigLong);
                    else ExitShort(0, Position.Quantity, "OFI time exit", SigShort);
                }

                if (BlockSource == OfiBlockSource.ChartBars)
                {
                    // new block whenever the signal instrument's bar (built like the chart's bars) starts a new bar
                    int cb = CurrentBars[1];
                    if (cb != lastSigBar)
                    {
                        if (lastSigBar != int.MinValue) ResetBlock();
                        lastSigBar = cb;
                    }
                }
                // new block every BlockTrades trades, or after a long gap (session break)
                else if (blockTrades >= BlockTrades || (lastTradeTime != DateTime.MinValue && (t - lastTradeTime).TotalMinutes > 30))
                    ResetBlock();
                lastTradeTime = t;
                blockTrades++;

                double bid = e.Bid > 0 ? e.Bid : sigBid, ask = e.Ask > 0 ? e.Ask : sigAsk;
                int side;
                if (ask > 0 && e.Price >= ask)       side = 1;
                else if (bid > 0 && e.Price <= bid)  side = -1;
                else if (e.Price > sigLastPrice)     side = 1;
                else if (e.Price < sigLastPrice)     side = -1;
                else                                 side = sigLastSide;
                sigLastPrice = e.Price;
                if (side != 0) sigLastSide = side;
                if (side == 0) return;

                long level = (long)Math.Floor(Math.Round(e.Price / sigTickSize) / LevelTicks);
                long vol = e.Volume;
                if (side > 0)
                {
                    long a = Get(blockAsk, level) + vol;
                    blockAsk[level] = a;
                    blockDelta += vol;
                    long weak = Get(blockBid, level - 1);                     // diagonal: ask(p) vs bid(p - 1)
                    if (a >= MinImbalanceVolume && weak >= MinWeakSide && a >= ImbalanceRatio * weak && !firedBuy.Contains(level))
                    {
                        qualBuy.Add(level);
                        long lo, hi;
                        int run = Stack(qualBuy, level, out lo, out hi);
                        if (run >= MinStackedLevels)
                        {
                            for (long k = lo; k <= hi; k++) firedBuy.Add(k);      // the whole stack fires once
                            if (blockDelta > 0) Signal(1, e.Price, a, weak, t, run);
                        }
                    }
                }
                else
                {
                    long b = Get(blockBid, level) + vol;
                    blockBid[level] = b;
                    blockDelta -= vol;
                    long weak = Get(blockAsk, level + 1);                     // diagonal: bid(p) vs ask(p + 1)
                    if (b >= MinImbalanceVolume && weak >= MinWeakSide && b >= ImbalanceRatio * weak && !firedSell.Contains(level))
                    {
                        qualSell.Add(level);
                        long lo, hi;
                        int run = Stack(qualSell, level, out lo, out hi);
                        if (run >= MinStackedLevels)
                        {
                            for (long k = lo; k <= hi; k++) firedSell.Add(k);
                            if (blockDelta < 0) Signal(-1, e.Price, b, weak, t, run);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!errorLogged) Print($"{LogTag} error: {ex.Message} (further errors suppressed)");
                errorLogged = true;
            }
        }

        private static long Get(Dictionary<long, long> d, long k)
        {
            long v;
            return d.TryGetValue(k, out v) ? v : 0;
        }

        // Number of adjacent imbalanced levels (same side, this block) around `level`, and the stack's bounds.
        private static int Stack(HashSet<long> qual, long level, out long lo, out long hi)
        {
            lo = level; hi = level;
            while (qual.Contains(lo - 1)) lo--;
            while (qual.Contains(hi + 1)) hi++;
            return (int)(hi - lo + 1);
        }

        private void ResetBlock()
        {
            blockBid.Clear(); blockAsk.Clear(); firedBuy.Clear(); firedSell.Clear();
            qualBuy.Clear(); qualSell.Clear();
            blockDelta = 0; blockTrades = 0;
        }

        private void Signal(int dir, double price, long strong, long weak, DateTime t, int stacked)
        {
            signalsToday++; signalsTotal++; statsDirty = true;
            string side = dir > 0 ? "LONG" : "SHORT";                        // what the imbalance says
            int tradeDir = ReverseSignals ? -dir : dir;                      // what we actually trade
            string tradeSide = tradeDir > 0 ? "LONG" : "SHORT";
            string rev = ReverseSignals ? $" -> reversed to {tradeSide}" : "";
            int hm = t.Hour * 100 + t.Minute;
            bool rth = hm >= RthStart && hm < RthEnd;
            bool news = NewsBlackout && hm >= 820 && hm < 845;
            string skip = null;
            if (Session == OfiSessionMode.RthOnly && !rth)                 skip = "outside RTH";
            else if (news)                                                skip = "news blackout 08:20-08:45";
            else if (Position.MarketPosition != MarketPosition.Flat || entryPending) skip = "already in a position";
            else if (!tradingAllowed)                                     skip = blockReason;
            else if (dailyPnL <= -DailyLossLimit)                         skip = "daily loss limit";
            lastSignal = $"{t:HH:mm:ss} {side}{rev} @ {price:F2}  ({strong} vs {weak}, stack {stacked}, delta {blockDelta:+0;-0})" + (skip != null ? $" -- SKIP: {skip}" : "");
            Log("SIGNAL", side, price, 0, 0, $"strong {strong} weak {weak} stack {stacked} delta {blockDelta}" + (ReverseSignals ? $"; reversed to {tradeSide}" : "") + (skip != null ? $"; skip: {skip}" : ""));
            if (skip != null) return;
            if (!EnableTrading)
            {
                Print($"{LogTag} {t:HH:mm:ss} SHADOW {side}{rev} @ {price:F2} ({strong} vs {weak}) -- Enable Trading is off");
                return;
            }
            string sig = tradeDir > 0 ? SigLong : SigShort;
            SetStopLoss(sig, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(sig, CalculationMode.Ticks, TargetTicks);
            entryPending = true;
            if (tradeDir > 0) EnterLong(0, Contracts, sig);
            else EnterShort(0, Contracts, sig);
            Print($"{LogTag} {t:HH:mm:ss} ENTER {tradeSide} x{Contracts} on {side} imbalance {strong} vs {weak} at {price:F2} ({signalName}){(ReverseSignals ? " [REVERSED]" : "")}");
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filledQty,
                                              double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if ((order.Name == SigLong || order.Name == SigShort)
                && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                entryPending = false;
                if (orderState == OrderState.Rejected) Log("REJECTED", order.Name == SigLong ? "LONG" : "SHORT", 0, 0, 0, $"{error} {comment}");
                return;
            }

            // A protective order was rejected -- typically "buy stop can't be placed below the market" when price
            // already ran through the stop level before the stop reached the exchange. The position would be
            // unprotected, so close it at market right away.
            if (orderState == OrderState.Rejected && (order.Name == "Stop loss" || order.Name == "Profit target"))
            {
                string side = Position.MarketPosition == MarketPosition.Long ? "LONG" : Position.MarketPosition == MarketPosition.Short ? "SHORT" : "FLAT";
                Print($"{LogTag} {order.Name} REJECTED ({comment}) -- position {side}, closing at market");
                Log("PROTECT-REJECTED", side, 0, 0, 0, $"{order.Name}: {error} {comment}");
                if (Position.MarketPosition == MarketPosition.Long)  ExitLong(0, Position.Quantity, "OFI protect exit", SigLong);
                if (Position.MarketPosition == MarketPosition.Short) ExitShort(0, Position.Quantity, "OFI protect exit", SigShort);
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                string name = execution.Order.Name;
                if (name == SigLong || name == SigShort)
                {
                    if (execution.Order.OrderState == OrderState.Filled)
                    {
                        entryPending = false;
                        entryTime = lastTradeTime != DateTime.MinValue ? lastTradeTime : time;
                        entryPrice = execution.Order.AverageFillPrice;
                        inTradeLong = name == SigLong;
                        Log("ENTRY", inTradeLong ? "LONG" : "SHORT", entryPrice, 0, 0, ReverseSignals ? "reversed" : "");
                    }
                    return;
                }
                if (entryTime == DateTime.MinValue) return;
                double pts = (price - entryPrice) * (inTradeLong ? 1 : -1);
                trades++; if (pts > 0) wins++;
                netPts += pts;
                dailyPnL += pts * Instrument.MasterInstrument.PointValue * quantity;
                Log("EXIT", inTradeLong ? "LONG" : "SHORT", entryPrice, price, pts, name);
                entryTime = DateTime.MinValue;
                statsDirty = true;
            }
            catch (Exception ex) { Print($"{LogTag} execution error: {ex.Message}"); }
        }

        private void Log(string ev, string side, double p1, double p2, double pts, string note)
        {
            try
            {
                if (!File.Exists(logPath))
                    File.AppendAllText(logPath, "wallclock,event,side,price1,price2,pts,note,session,account,instrument\n");
                File.AppendAllText(logPath, string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ev, side,
                    p1.ToString("F2", CultureInfo.InvariantCulture), p2.ToString("F2", CultureInfo.InvariantCulture),
                    pts.ToString("F2", CultureInfo.InvariantCulture), "\"" + note.Replace("\"", "'") + "\"",
                    Session, Account != null ? Account.Name : "", Instrument.FullName) + "\n");
            }
            catch { }
        }

        private void DrawStats()
        {
            statsDirty = false;
            string text = $"OrderFlowImbalanceV1  |  {(EnableTrading ? (tradingAllowed ? "TRADING" : "BLOCKED") : "SHADOW (Enable Trading off)")}  |  "
                + $"{Session}{(NewsBlackout ? " + news blackout" : "")}{(ReverseSignals ? "  |  REVERSED" : "")}  |  signal {signalName}"
                + $"\nrule: >= {MinImbalanceVolume} at >= {ImbalanceRatio}x, stop {StopTicks} / target {TargetTicks} ticks, {TimeExitMinutes} min"
                + $"\nsignals today {signalsToday}, total {signalsTotal}  |  last: {lastSignal}"
                + $"\ntrades {trades}, wins {wins}, net {netPts:+0.0;-0.0} pts ({(trades > 0 ? netPts / trades : 0):+0.00;-0.00}/trade before commission)  |  today ${dailyPnL:F0}"
                + "\nUNPROVEN: judge after ~100 trades (RTH tested; overnight was negative in testing)";
            Draw.TextFixed(this, "OFIstats", text, TextPosition.TopLeft, Brushes.White,
                new SimpleFont("Arial", 11), Brushes.Transparent, Brushes.Black, 70);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Signal Instrument", Description = "Auto = the full-size contract of the chart's instrument and expiry (MNQ 12-26 -> NQ 12-26). Or type one, e.g. NQ 12-26.", Order = 1, GroupName = "1. Signal")]
        public string SignalInstrument { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Block Source", Description = "ChartBars = one footprint per chart bar (signal instrument built with the chart's bar type, e.g. NQ 60 Range). FixedTrades = Block Size trades (tested: 500).", Order = 0, GroupName = "1. Signal")]
        public OfiBlockSource BlockSource { get; set; }

        [NinjaScriptProperty]
        [Range(50, 100000)]
        [Display(Name = "Block Size (trades)", Description = "Used only when Block Source = FixedTrades. Tested: 500.", Order = 2, GroupName = "1. Signal")]
        public int BlockTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Level Size (ticks)", Description = "Tested: 8 (2 points).", Order = 3, GroupName = "1. Signal")]
        public int LevelTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100000)]
        [Display(Name = "Min Imbalance Volume", Description = "Tested: 300 NQ contracts.", Order = 4, GroupName = "1. Signal")]
        public int MinImbalanceVolume { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 100.0)]
        [Display(Name = "Imbalance Ratio (x)", Description = "Tested: 5.", Order = 5, GroupName = "1. Signal")]
        public double ImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Min Weak Side", Description = "Tested: 2.", Order = 6, GroupName = "1. Signal")]
        public int MinWeakSide { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Min Stacked Levels", Description = "Adjacent levels (same side, same block) that must ALL be imbalanced before a signal. 1 = single level (the tested rule). >1 is untested.", Order = 7, GroupName = "1. Signal")]
        public int MinStackedLevels { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reverse Signals", Description = "ON = flip the direction: a BUY signal enters SHORT, a SELL signal enters LONG. Stop / target / time exit apply to the position actually taken. OFF = the tested rule (trade WITH the imbalance).", Order = 8, GroupName = "1. Signal")]
        public bool ReverseSignals { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Stop (ticks)", Description = "Tested: 40.", Order = 1, GroupName = "2. Trade")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Target (ticks)", Description = "Tested: 60.", Order = 2, GroupName = "2. Trade")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Time Exit (minutes)", Description = "Tested: 60.", Order = 3, GroupName = "2. Trade")]
        public int TimeExitMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session", Description = "RthOnly = tested. AllDay also trades overnight (untested; overnight was negative).", Order = 4, GroupName = "2. Trade")]
        public OfiSessionMode Session { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "RTH Start (HHMM, chart time)", Order = 5, GroupName = "2. Trade")]
        public int RthStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "RTH End (HHMM, chart time)", Order = 6, GroupName = "2. Trade")]
        public int RthEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Blackout 08:20-08:45", Order = 7, GroupName = "2. Trade")]
        public bool NewsBlackout { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Contracts", Order = 8, GroupName = "2. Trade")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit ($)", Order = 1, GroupName = "3. Safety")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading", Description = "Off = signals are only logged.", Order = 2, GroupName = "3. Safety")]
        public bool EnableTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Non-Sim Account", Description = "Keep OFF.", Order = 3, GroupName = "3. Safety")]
        public bool AllowNonSimAccount { get; set; }
        #endregion
    }
}
