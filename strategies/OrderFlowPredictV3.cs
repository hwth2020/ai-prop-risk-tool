#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// OrderFlowPredictV3 -- every time a bar closes, sends its footprint to the Python server (predict_server.py, port 5611),
// which predicts the NEXT bar's direction from the last "Pattern Bars" bars (model retrained on the last "Training
// Bars" bars). The strategy then buys (p_up >= 0.5 + margin) or sells (p_up <= 0.5 - margin).
//
//  V3 = V2 + "Reverse Signals": when ON, the prediction is traded the other way round (p_up high -> SELL, p_up low -> BUY).
//  Everything else (margin, stop / target / trail, scale-in, ATM, reverse-at-stop) applies to the position actually taken.
//
//  Setup: start C:\Users\hwth0\.claude\projects\orderflow_predict\start_predict_server.bat, then put this on a chart (e.g. NQ or MNQ, 60 Range). The order flow
//  is read from the Signal Instrument ("Auto" = the full-size contract of the chart's instrument and expiry) built
//  with the CHART's bar type, so one footprint = one chart bar. Works in real time and Playback (Market Replay);
//  history is not used -- the server warms up on the bars it sees.
//  Entry: Market at the next trade, or Limit "Limit Offset" ticks better than the bar's close, valid for one bar.
//  Exits: stop / target in ticks (60 / 60 = one 60-tick range bar, 1:1), out after Time Exit minutes.
//  Safety: Enable Trading OFF by default; only Sim / Playback accounts unless allowed.
//  UNPROVEN: offline, these models predicted the next 60-tick NQ range bar ~50 % right (a coin flip).
//  Logs: Documents\NinjaTrader 8\OrderFlowPredictV3_log.csv ; the server also writes orderflow_predict\predict_log.csv.

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum Ofp3Model { Logistic, GradientBoosting, NeuralNet, DQN, Kronos }
    public enum Ofp3EntryMode { Market, Limit }
    public enum Ofp3SessionMode { AllDay, RthOnly }
    public enum Ofp3OrphanAction { Flatten, AlertOnly }

    public class OrderFlowPredictV3 : Strategy
    {
        private const string LogTag = "[OFP-V3]";
        private const string SigLong = "OFP_L", SigShort = "OFP_S";
        private const string SigRevLong = "OFP_RL", SigRevShort = "OFP_RS";     // the one-time reversal after a stop
        private static bool IsEntry(string n) { return n == SigLong || n == SigShort || n == SigRevLong || n == SigRevShort; }
        private string curSig;                       // entry signal of the open (or pending) managed trade
        private bool curIsReversal;
        private int curRevCount;                     // reversals already made in the current chain (0 = original trade)

        // scale-in (pyramiding into winners)
        private const string SigAddPrefix = "OFP_A";            // OFP_A1, OFP_A2, ... one signal per add
        private static bool IsAdd(string n) { return n != null && n.StartsWith(SigAddPrefix); }
        private readonly List<string> activeSigs = new List<string>();   // entry signals currently in the position
        private int addsDone;
        private bool addPending;
        private double lastAddPrice;
        // position-level accounting (works for 1 entry or many adds)
        private double posQty, posCost, tradePts, tradeMoney;
        private double bestProfitTicks, curStopPrice;

        // footprint of the signal instrument's current bar
        private readonly Dictionary<long, long> fpBid = new Dictionary<long, long>();
        private readonly Dictionary<long, long> fpAsk = new Dictionary<long, long>();
        private double fpO, fpH, fpL, fpC;
        private int fpTrades;
        private DateTime fpT0, fpT1;
        private int lastSigBar = int.MinValue;
        private double sigBid, sigAsk, sigLastPrice, sigTickSize;
        private int sigLastSide;
        private string signalName;

        // server
        private TcpClient client;
        private StreamWriter writer;
        private StreamReader reader;
        private DateTime nextConnect = DateTime.MinValue;
        private bool failLogged;
        private string serverStatus = "not connected";
        private readonly List<string> sentBars = new List<string>();     // re-sent after a reconnect so the server keeps its history
        private string currentBarJson;

        // trading state
        private Order entryOrder;
        private int entryBar = -1;
        private DateTime entryTime = DateTime.MinValue, lastTradeTime = DateTime.MinValue;
        private double entryPrice;
        private bool inTradeLong, entryPending;
        private bool tradingAllowed = true;
        private string blockReason = "";
        private double dailyPnL;
        private DateTime pnlDate = DateTime.MinValue;
        private int barsSent, trades, wins;
        private double netPts;
        private string lastPred = "none yet", hitRate = "-";
        private bool statsDirty = true;
        private string logPath;
        private string revNote = "";                 // " [REVERSED from UP]" etc. while Reverse Signals is on

        // panel under Chart Trader
        private NinjaTrader.Gui.Chart.Chart chartWindow;
        private System.Windows.Controls.Grid ctGrid;
        private System.Windows.Controls.RowDefinition ctRow;
        private System.Windows.Controls.Border panelRoot;
        private readonly Dictionary<string, System.Windows.Controls.TextBlock> panelCells = new Dictionary<string, System.Windows.Controls.TextBlock>();
        private DateTime nextPanelRefresh = DateTime.MinValue;
        private double lastP = double.NaN;
        private string lastNTrain = "0", lastBarInfo = "-", lastDecision = "-", lastPredTime = "-";
        private double lastHit = double.NaN;
        private int lastScored;
        private string lastExit = "-";

        // ATM mode: the trade is placed and managed by a Chart-Trader ATM template (its stop / target / qty)
        private string atmId, atmOrderId;
        private volatile bool atmCreated, atmFailed;
        private bool atmFilled, atmCloseSent, orphanActive;
        private int atmDir, atmQty;
        private bool UseAtm { get { return !string.IsNullOrWhiteSpace(AtmTemplate) && !AtmTemplate.Equals("None", StringComparison.OrdinalIgnoreCase); } }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Python server predicts the next bar from the last N bars' footprints; trades the prediction (unproven -- sim / playback).";
                Name         = "OrderFlowPredictV3";
                Calculate    = Calculate.OnEachTick;
                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                RealtimeErrorHandling        = RealtimeErrorHandling.IgnoreAllErrors;   // rejections handled in OnOrderUpdate
                StartBehavior                = StartBehavior.WaitUntilFlat;

                SignalInstrument = "Auto";
                LevelTicks       = 4;
                PatternBars      = 5;
                TrainingBars     = 300;
                RetrainEvery     = 1;
                Model            = Ofp3Model.Logistic;
                ServerHost       = "127.0.0.1";
                ServerPort       = 5611;
                ServerTimeoutMs  = 5000;
                ConfidenceMargin = 0.0;
                ReverseSignals   = false;
                AtmTemplate      = "None";
                OrphanAction     = Ofp3OrphanAction.Flatten;
                ReverseAtStop    = false;
                ReverseTimes     = 1;
                ScaleIn          = false;
                AddEveryTicks    = 24;
                MaxAdds          = 3;
                AddContracts     = 1;
                LetWinnerRun     = true;
                TrailTriggerTicks = 0;
                TrailStopTicks   = 18;
                TrailStepTicks   = 24;
                EntryMode        = Ofp3EntryMode.Market;
                LimitOffsetTicks = 8;
                StopTicks        = 60;
                TargetTicks      = 60;
                TimeExitMinutes  = 60;
                Session          = Ofp3SessionMode.AllDay;
                RthStart         = 930;
                RthEnd           = 1600;
                NewsBlackout     = true;
                Contracts        = 1;
                DailyLossLimit   = 1000;
                EnableTrading    = false;
                AllowNonSimAccount = false;
            }
            else if (State == State.Configure)
            {
                if (ScaleIn)
                {
                    // every add has its own signal name (OFP_A1, OFP_A2 ...): allow one entry per unique name
                    EntryHandling       = EntryHandling.UniqueEntries;
                    EntriesPerDirection = 1;
                }
                signalName = ResolveSignalInstrument();
                AddDataSeries(signalName, BarsPeriod);          // signal instrument with the chart's bar type
            }
            else if (State == State.DataLoaded)
            {
                sigTickSize = BarsArray[1].Instrument.MasterInstrument.TickSize;
                logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "OrderFlowPredictV3_log.csv");
                if (Account != null && !AllowNonSimAccount && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                    && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
                {
                    tradingAllowed = false;
                    blockReason = $"account {Account.Name} is not a Sim / Playback account -- trading blocked";
                    Print($"{LogTag} {blockReason}");
                }
                Print($"{LogTag} signal {signalName} ({BarsPeriod}) -> trading {Instrument.FullName}; model {Model}, pattern {PatternBars} bars, "
                    + $"training on the last {(TrainingBars == 0 ? "ALL" : TrainingBars.ToString())} bars; margin {ConfidenceMargin}; entry {EntryMode}; "
                    + $"stop {StopTicks} / target {TargetTicks} ticks; session {Session}; reverse signals {ReverseSignals}; EnableTrading {EnableTrading}; account {(Account != null ? Account.Name : "?")}");
            }
            else if (State == State.Historical)
            {
                PanelInsert();   // info panel under Chart Trader (charts only)
            }
            else if (State == State.Realtime)
            {
                Connect();
                PanelRefresh();
            }
            else if (State == State.Terminated)
            {
                try
                {
                    if (Position != null && Position.MarketPosition != MarketPosition.Flat && logPath != null)
                    {
                        string side = Position.MarketPosition == MarketPosition.Long ? "LONG" : "SHORT";
                        Print($"{LogTag} WARNING: stopped while {side} {Position.Quantity} @ {Position.AveragePrice:F2}. "
                            + "Check the account's Positions and Orders tabs -- the stop / target may have been cancelled.");
                        Log("TERMINATED-WITH-OPEN-POSITION", side, Position.AveragePrice, 0, 0, $"qty {Position.Quantity}");
                    }
                    if (atmId != null && logPath != null)
                    {
                        Print($"{LogTag} NOTE: stopped while an ATM '{AtmTemplate}' trade / entry was open -- the ATM keeps managing it "
                            + "(its stop and target stay active). Manage or close it from Chart Trader.");
                        Log("TERMINATED-WITH-OPEN-ATM", atmDir > 0 ? "LONG" : "SHORT", entryPrice, 0, 0, $"ATM {AtmTemplate}; it keeps running");
                    }
                }
                catch { }
                PanelRemove();
                Disconnect(true);
            }
        }

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
            string full = Instrument.FullName;
            int sp = full.IndexOf(' ');
            return sp > 0 ? master + full.Substring(sp) : master;
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 0 || CurrentBar < 1) return;
            if (statsDirty) DrawStats();
        }

        // ---- the signal instrument's trades ------------------------------------------------------------------
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            try
            {
                if (BarsInProgress != 1 || State != State.Realtime) return;
                if (e.MarketDataType == MarketDataType.Bid) { sigBid = e.Price; return; }
                if (e.MarketDataType == MarketDataType.Ask) { sigAsk = e.Price; return; }
                if (e.MarketDataType != MarketDataType.Last) return;

                DateTime t = e.Time;
                lastTradeTime = t;
                if (t.Date != pnlDate) { pnlDate = t.Date; dailyPnL = 0; statsDirty = true; }

                if (Position.MarketPosition != MarketPosition.Flat && entryTime != DateTime.MinValue
                    && (t - entryTime).TotalMinutes >= TimeExitMinutes)
                {
                    // "" = from all entries (the original and every add)
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong(0, Position.Quantity, "OFP time exit", "");
                    else ExitShort(0, Position.Quantity, "OFP time exit", "");
                }
                if (Position.MarketPosition != MarketPosition.Flat && curSig != null && TrailTriggerTicks > 0) Trail();
                if (ScaleIn && Position.MarketPosition != MarketPosition.Flat && curSig != null) TryAdd();
                if (atmId != null) UpdateAtm(t);
                if (orphanActive && PositionAccount.MarketPosition == MarketPosition.Flat)
                {
                    orphanActive = false;
                    lastDecision = "orphan position is closed -- trading resumes";
                    Log("ORPHAN-CLOSED", "-", 0, 0, 0, "");
                }

                // a new bar of the signal series: the previous one is complete -> predict the next one
                int cb = CurrentBars[1];
                if (cb != lastSigBar)
                {
                    if (lastSigBar != int.MinValue && fpTrades > 0)
                        BarClosed(t);
                    lastSigBar = cb;
                    ResetFootprint();
                }

                double bid = e.Bid > 0 ? e.Bid : sigBid, ask = e.Ask > 0 ? e.Ask : sigAsk;
                int side;
                if (ask > 0 && e.Price >= ask)       side = 1;
                else if (bid > 0 && e.Price <= bid)  side = -1;
                else if (e.Price > sigLastPrice)     side = 1;
                else if (e.Price < sigLastPrice)     side = -1;
                else                                 side = sigLastSide;
                sigLastPrice = e.Price;
                if (side != 0) sigLastSide = side;

                if (fpTrades == 0) { fpO = fpH = fpL = e.Price; fpT0 = t; }
                fpH = Math.Max(fpH, e.Price); fpL = Math.Min(fpL, e.Price); fpC = e.Price; fpT1 = t;
                fpTrades++;
                if (side == 0) return;
                if (DateTime.Now >= nextPanelRefresh) PanelRefresh();      // live P&L, about once a second

                long level = (long)Math.Floor(Math.Round(e.Price / sigTickSize) / LevelTicks);
                Dictionary<long, long> d = side > 0 ? fpAsk : fpBid;
                long v;
                d[level] = (d.TryGetValue(level, out v) ? v : 0) + (long)e.Volume;
            }
            catch (Exception ex)
            {
                Print($"{LogTag} error: {ex.Message}");
            }
        }

        private void ResetFootprint()
        {
            fpBid.Clear(); fpAsk.Clear(); fpTrades = 0;
        }

        private void BarClosed(DateTime now)
        {
            // an unfilled limit entry lives for one bar only
            if (entryOrder != null && entryPending && Position.MarketPosition == MarketPosition.Flat)
                CancelOrder(entryOrder);
            if (atmId != null && atmCreated && !atmFilled)
            {
                // Cancel only an entry that is still working with nothing filled: cancelling at the same moment it fills
                // leaves the ATM's stop / target without a valid OCO group ("OCO ID cannot be reused").
                string[] st = GetAtmStrategyEntryOrderStatus(atmOrderId);     // [0] avg price, [1] filled qty, [2] state
                if (st != null && st.Length >= 3 && ParseNum(st[1]) == 0 && (st[2] == "Working" || st[2] == "Accepted"))
                    AtmStrategyCancelEntryOrder(atmOrderId);    // UpdateAtm logs it once NinjaTrader confirms the cancel
            }

            long bv = fpBid.Values.Sum(), av = fpAsk.Values.Sum();
            lastBarInfo = $"{fpT1:HH:mm:ss}  {(fpC > fpO ? "UP" : fpC < fpO ? "DOWN" : "FLAT")}  {fpO:F2} -> {fpC:F2}  |  vol {bv + av}, delta {av - bv:+0;-0}, {fpTrades} trades";

            long lo = long.MaxValue, hi = long.MinValue;
            foreach (long k in fpBid.Keys.Concat(fpAsk.Keys)) { lo = Math.Min(lo, k); hi = Math.Max(hi, k); }
            if (lo == long.MaxValue) return;
            var sb = new StringBuilder();
            string bidArr = "", askArr = "";
            for (long k = lo; k <= hi; k++)
            {
                long b, a;
                bidArr += (k > lo ? "," : "") + (fpBid.TryGetValue(k, out b) ? b : 0);
                askArr += (k > lo ? "," : "") + (fpAsk.TryGetValue(k, out a) ? a : 0);
            }
            barsSent++;
            sb.Append("{\"type\":\"bar\",\"i\":").Append(barsSent)
              .Append(",\"t0\":\"").Append(fpT0.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append("\",\"t1\":\"").Append(fpT1.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append("\",\"o\":").Append(Ticks(fpO)).Append(",\"h\":").Append(Ticks(fpH))
              .Append(",\"l\":").Append(Ticks(fpL)).Append(",\"c\":").Append(Ticks(fpC))
              .Append(",\"trades\":").Append(fpTrades).Append(",\"base\":").Append(lo)
              .Append(",\"bid\":[").Append(bidArr).Append("],\"ask\":[").Append(askArr).Append("]}");

            string json = sb.ToString();
            sentBars.Add(json);                    // kept even while the server is down, so nothing is missing later
            int keep = (TrainingBars == 0 ? 5000 : TrainingBars) + PatternBars + 60;    // + 60 for the server's 50-bar average
            if (sentBars.Count > keep) sentBars.RemoveRange(0, sentBars.Count - keep);

            currentBarJson = json;                 // Connect() replays everything except this one (it is sent below)
            if (client == null) Connect();
            if (client == null) { currentBarJson = null; serverStatus = "server not reachable"; lastDecision = "no prediction -- server not reachable"; statsDirty = true; PanelRefresh(); return; }

            string reply = Request(json);
            currentBarJson = null;
            if (reply == null) { serverStatus = "no reply (reconnecting)"; lastDecision = "no prediction -- no reply"; statsDirty = true; PanelRefresh(); return; }

            double p = ParseDouble(Field(reply, "p_up"), 0.5);
            string status = Field(reply, "status") ?? "?";
            string n = Field(reply, "n_train") ?? "0";
            string hr = Field(reply, "hit_rate"), sc = Field(reply, "scored");
            if (hr != null && hr != "null") hitRate = $"{ParseDouble(hr, 0) * 100:F1}% of {sc}";
            serverStatus = status;
            string barDir = fpC > fpO ? "up" : fpC < fpO ? "down" : "flat";
            lastPred = $"{now:HH:mm:ss} p(up) {p:F3} (trained on {n}) -- {status}";
            statsDirty = true;
            Log("PRED", p >= 0.5 ? "UP" : "DOWN", Ticks(fpC) * sigTickSize, p, 0, $"bar {barsSent} closed {barDir}; n_train {n}; {status}");

            lastP = status == "ok" ? p : double.NaN;      // 0.5 while warming up is a placeholder, not a prediction
            lastNTrain = n; lastPredTime = now.ToString("HH:mm:ss");
            if (hr != null && hr != "null") { lastHit = ParseDouble(hr, double.NaN); int s; if (int.TryParse(sc, out s)) lastScored = s; }

            if (status != "ok") { lastDecision = "no trade -- " + status; PanelRefresh(); return; }
            int dir = p >= 0.5 + ConfidenceMargin ? 1 : p <= 0.5 - ConfidenceMargin ? -1 : 0;
            if (dir != 0)
            {
                // Reverse Signals: trade against the prediction. The server still scores the ORIGINAL prediction (hit rate).
                revNote = ReverseSignals ? $" [REVERSED from {(dir > 0 ? "UP" : "DOWN")} prediction]" : "";
                Enter(ReverseSignals ? -dir : dir, p, now);
            }
            else lastDecision = $"no trade -- p(up) {p:F3} inside the +/-{ConfidenceMargin:F2} margin";
            PanelRefresh();
        }

        private long Ticks(double price) { return (long)Math.Round(price / sigTickSize); }

        private static double ParseNum(string s)
        {
            double v;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return v;
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            return 0;
        }

        /// <summary>Follows the ATM trade (polled on every signal-instrument trade): entry fill, cancel / reject,
        /// time exit, and the final result when the ATM is flat again.</summary>
        private void UpdateAtm(DateTime t)
        {
            try
            {
                if (atmFailed) { Log("ATM-FAILED", atmDir > 0 ? "LONG" : "SHORT", 0, 0, 0, $"template {AtmTemplate}"); ResetAtm(); return; }
                if (!atmCreated) return;
                string side = atmDir > 0 ? "LONG" : "SHORT";
                if (!atmFilled)
                {
                    string[] st = GetAtmStrategyEntryOrderStatus(atmOrderId);     // [0] avg fill price, [1] filled qty, [2] state
                    if (st == null || st.Length < 3) return;
                    double filled = ParseNum(st[1]);
                    if (filled > 0)
                    {
                        atmFilled = true;
                        atmQty = (int)filled;
                        entryPrice = ParseNum(st[0]);
                        entryTime = t;
                        inTradeLong = atmDir > 0;
                        Log("ENTRY", side, entryPrice, 0, 0, $"ATM {AtmTemplate}, qty {atmQty}, {EntryMode}{(ReverseSignals ? ", reversed" : "")}");
                        PanelRefresh();
                    }
                    else if (st[2] == "Cancelled" || st[2] == "Rejected")
                    {
                        Log(st[2] == "Rejected" ? "REJECTED" : "LIMIT-EXPIRED", side, 0, 0, 0, $"ATM {AtmTemplate}");
                        ResetAtm();
                        PanelRefresh();
                    }
                    return;
                }
                MarketPosition mp = GetAtmStrategyMarketPosition(atmId);
                if (mp == MarketPosition.Flat && PositionAccount.MarketPosition != MarketPosition.Flat)
                {
                    // The ATM ended (e.g. its stop / target was rejected) but the account still holds the position:
                    // it is no longer managed and may have no stop.
                    HandleOrphan(side);
                    return;
                }
                if (mp == MarketPosition.Flat)
                {
                    double pnl = GetAtmStrategyRealizedProfitLoss(atmId);                 // currency
                    double pv = Instrument.MasterInstrument.PointValue;
                    double pts = atmQty > 0 ? pnl / (pv * atmQty) : 0;
                    trades++; if (pnl > 0) wins++;
                    netPts += pts;
                    dailyPnL += pnl;
                    Log("EXIT", side, entryPrice, entryPrice + (atmDir > 0 ? pts : -pts), pts, $"ATM {AtmTemplate}, qty {atmQty}, {pnl:F2} $");
                    lastExit = $"{side} {pts:+0.00;-0.00} pts/contract, {pnl:C0} (ATM {AtmTemplate})";
                    entryTime = DateTime.MinValue;
                    statsDirty = true;
                    ResetAtm();
                    PanelRefresh();
                    return;
                }
                if (!atmCloseSent && entryTime != DateTime.MinValue && (t - entryTime).TotalMinutes >= TimeExitMinutes)
                {
                    atmCloseSent = true;
                    AtmStrategyClose(atmId);
                    Log("TIME-EXIT", side, entryPrice, 0, 0, $"ATM {AtmTemplate} closed after {TimeExitMinutes} min");
                }
            }
            catch (Exception ex) { Print($"{LogTag} ATM error: {ex.Message}"); }
        }

        private void HandleOrphan(string side)
        {
            string acct = $"{PositionAccount.MarketPosition} {PositionAccount.Quantity} @ {PositionAccount.AveragePrice:F2}";
            orphanActive = true;
            lastDecision = $"ORPHAN POSITION: ATM '{AtmTemplate}' ended but the account is still {acct}";
            Print($"{LogTag} WARNING: ATM '{AtmTemplate}' ended but {Account.Name} still holds {acct} on {Instrument.FullName} -- "
                + (OrphanAction == Ofp3OrphanAction.Flatten ? "flattening it now." : "it is UNMANAGED: close it or add a stop. No new trades until it is flat."));
            Log("ATM-ORPHAN", side, PositionAccount.AveragePrice, 0, 0, $"account still {acct}; action {OrphanAction}");
            Alert("OFPorphan", Priority.High, $"OrderFlowPredictV3: unmanaged position {acct} on {Instrument.FullName}",
                NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert4.wav", 30, Brushes.Red, Brushes.White);
            if (OrphanAction == Ofp3OrphanAction.Flatten)
                Account.Flatten(new[] { Instrument });
            entryTime = DateTime.MinValue;
            ResetAtm();
            PanelRefresh();
        }

        private void ResetAtm()
        {
            atmId = atmOrderId = null;
            atmCreated = atmFailed = atmFilled = atmCloseSent = false;
            atmQty = 0;
        }

        /// <summary>The ATM's working stop / target orders on this instrument (for the panel).</summary>
        private string AtmOrders(string prefix)
        {
            var parts = new List<string>();
            try
            {
                lock (Account.Orders)
                    foreach (Order o in Account.Orders)
                        if (o.Instrument == Instrument && o.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted || o.OrderState == OrderState.ChangePending))
                            parts.Add($"{(o.StopPrice > 0 ? o.StopPrice : o.LimitPrice):F2} x{o.Quantity}");
            }
            catch { }
            return parts.Count > 0 ? string.Join(", ", parts) : "-";
        }

        // dir = the direction actually traded (already flipped by BarClosed when Reverse Signals is on)
        private void Enter(int dir, double p, DateTime t)
        {
            string side = dir > 0 ? "LONG" : "SHORT";
            int hm = t.Hour * 100 + t.Minute;
            string skip = null;
            if (Session == Ofp3SessionMode.RthOnly && !(hm >= RthStart && hm < RthEnd)) skip = "outside RTH";
            else if (NewsBlackout && hm >= 820 && hm < 845)                         skip = "news blackout";
            else if (Position.MarketPosition != MarketPosition.Flat || entryPending || atmId != null) skip = "already in a position";
            else if (PositionAccount.MarketPosition != MarketPosition.Flat)
                skip = $"the account already holds {PositionAccount.MarketPosition} {PositionAccount.Quantity} on {Instrument.FullName}";
            else if (!tradingAllowed)                                               skip = blockReason;
            else if (dailyPnL <= -DailyLossLimit)                                   skip = "daily loss limit";
            else if (!EnableTrading)                                                skip = "Enable Trading is off";
            if (skip != null) { Log("SKIP", side, 0, p, 0, skip + revNote); lastDecision = $"{side} signal{revNote} SKIPPED -- {skip}"; return; }
            lastDecision = $"TAKE {side} ({EntryMode}{(UseAtm ? ", ATM " + AtmTemplate : "")}){revNote}";

            if (UseAtm)
            {
                // ATM template: NinjaTrader places the entry and then the template's stop / target / quantity.
                atmId = GetAtmStrategyUniqueId();
                atmOrderId = GetAtmStrategyUniqueId();
                atmCreated = atmFailed = atmFilled = atmCloseSent = false;
                atmDir = dir;
                string myId = atmId;
                OrderType ot = EntryMode == Ofp3EntryMode.Market ? OrderType.Market : OrderType.Limit;
                double lpx = EntryMode == Ofp3EntryMode.Market ? 0
                    : Instrument.MasterInstrument.RoundToTickSize(Closes[0][0] - dir * LimitOffsetTicks * TickSize);
                AtmStrategyCreate(dir > 0 ? OrderAction.Buy : OrderAction.Sell, ot, lpx, 0, TimeInForce.Gtc, atmOrderId, AtmTemplate, atmId,
                    (err, id) =>
                    {
                        if (id != myId) return;
                        if (err == ErrorCode.NoError) atmCreated = true;
                        else { atmFailed = true; Print($"{LogTag} ATM '{AtmTemplate}' could not be created: {err}"); }
                    });
                Print($"{LogTag} {t:HH:mm:ss} ENTER {side} via ATM '{AtmTemplate}' ({EntryMode}{(lpx > 0 ? " @ " + lpx.ToString("F2") : "")}, p_up {p:F3}){revNote}");
                return;
            }

            string sig = dir > 0 ? SigLong : SigShort;
            SetStopLoss(sig, CalculationMode.Ticks, StopTicks, false);
            SetProfitTarget(sig, CalculationMode.Ticks, TargetFor());
            curSig = sig; curIsReversal = false; curRevCount = 0;
            entryPending = true;
            entryBar = barsSent;
            if (EntryMode == Ofp3EntryMode.Market)
            {
                if (dir > 0) EnterLong(0, Contracts, sig); else EnterShort(0, Contracts, sig);
                Print($"{LogTag} {t:HH:mm:ss} ENTER {side} at market (p_up {p:F3}){revNote}");
            }
            else
            {
                double last = Closes[0][0];
                double px = Instrument.MasterInstrument.RoundToTickSize(last - dir * LimitOffsetTicks * TickSize);
                if (dir > 0) EnterLongLimit(0, true, Contracts, px, sig); else EnterShortLimit(0, true, Contracts, px, sig);
                Print($"{LogTag} {t:HH:mm:ss} {side} LIMIT @ {px:F2} for one bar (p_up {p:F3}){revNote}");
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity, int filledQty,
                                              double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (IsAdd(order.Name) && (orderState == OrderState.Cancelled || orderState == OrderState.Rejected))
            {
                addPending = false;
                Log("ADD-" + orderState.ToString().ToUpperInvariant(), inTradeLong ? "LONG" : "SHORT", 0, 0, 0, $"{order.Name}: {error} {comment}");
                return;
            }
            if (IsEntry(order.Name))
            {
                entryOrder = order;
                if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    entryPending = false;
                    entryOrder = null;
                    Log(orderState == OrderState.Rejected ? "REJECTED" : "LIMIT-EXPIRED", order.Name == SigLong || order.Name == SigRevLong ? "LONG" : "SHORT",
                        limitPrice, 0, 0, $"{error} {comment}");
                }
                return;
            }
            if (orderState == OrderState.Rejected && (order.Name == "Stop loss" || order.Name == "Profit target"))
            {
                string side = Position.MarketPosition == MarketPosition.Long ? "LONG" : Position.MarketPosition == MarketPosition.Short ? "SHORT" : "FLAT";
                Print($"{LogTag} {order.Name} REJECTED ({comment}) -- position {side}, closing at market");
                Log("PROTECT-REJECTED", side, 0, 0, 0, $"{order.Name}: {error} {comment}");
                if (Position.MarketPosition == MarketPosition.Long)  ExitLong(0, Position.Quantity, "OFP protect exit", "");
                if (Position.MarketPosition == MarketPosition.Short) ExitShort(0, Position.Quantity, "OFP protect exit", "");
            }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
                                                  MarketPosition marketPosition, string orderId, DateTime time)
        {
            try
            {
                string name = execution.Order.Name;
                if (IsEntry(name) || IsAdd(name))
                {
                    posCost += price * quantity; posQty += quantity;          // every fill, partial fills included
                    if (execution.Order.OrderState != OrderState.Filled) return;
                    if (IsAdd(name))
                    {
                        addPending = false; addsDone++;
                        lastAddPrice = execution.Order.AverageFillPrice;
                        if (!activeSigs.Contains(name)) activeSigs.Add(name);
                        // protect the whole, bigger position: shared stop moves to Stop (ticks) behind the newest add
                        int adir = inTradeLong ? 1 : -1;
                        double s = Instrument.MasterInstrument.RoundToTickSize(lastAddPrice - adir * StopTicks * TickSize);
                        if ((s - curStopPrice) * adir > 0) curStopPrice = s;
                        ApplyStop();
                        lastDecision = $"ADDED {execution.Order.Quantity} ({addsDone} of {MaxAdds}) @ {lastAddPrice:F2}, stop for all now {curStopPrice:F2}";
                        Log("ADD", inTradeLong ? "LONG" : "SHORT", lastAddPrice, addsDone, 0, $"{name} x{execution.Order.Quantity}; position {posQty}; shared stop {curStopPrice:F2}");
                        PanelRefresh();
                        return;
                    }
                    entryPending = false;
                    entryOrder = null;
                    entryTime = lastTradeTime != DateTime.MinValue ? lastTradeTime : time;
                    entryPrice = execution.Order.AverageFillPrice;
                    inTradeLong = name == SigLong || name == SigRevLong;
                    curSig = name;
                    curIsReversal = name == SigRevLong || name == SigRevShort;
                    bestProfitTicks = 0;
                    curStopPrice = entryPrice + (inTradeLong ? -1 : 1) * StopTicks * TickSize;
                    activeSigs.Clear(); activeSigs.Add(name);
                    addsDone = 0; addPending = false; lastAddPrice = entryPrice;
                    Log("ENTRY", inTradeLong ? "LONG" : "SHORT", entryPrice, 0, 0, curIsReversal ? "REVERSAL after stop" : EntryMode.ToString() + (ReverseSignals ? ", reversed signal" : ""));
                    PanelRefresh();
                    return;
                }
                if (posQty <= 0) return;                                       // not one of ours
                double pv = Instrument.MasterInstrument.PointValue;
                double avg = posCost / posQty;
                double execPts = (price - avg) * (inTradeLong ? 1 : -1);       // per contract, vs the position's average
                tradePts += execPts * quantity; tradeMoney += execPts * pv * quantity;
                dailyPnL += execPts * pv * quantity;
                posCost -= avg * quantity; posQty -= quantity;
                if (posQty > 0.5) return;                                      // part of the position is still open
                // the whole position is closed: count ONE trade (pts per base contract = all points / Contracts)
                posQty = 0; posCost = 0;
                double pts = tradePts / Math.Max(1, Contracts);
                trades++; if (tradeMoney > 0) wins++;
                netPts += pts;
                Log("EXIT", inTradeLong ? "LONG" : "SHORT", entryPrice, price, pts, $"{name}; adds {addsDone}; {tradeMoney:F2} $");
                lastExit = $"{(inTradeLong ? "LONG" : "SHORT")} {pts:+0.00;-0.00} pts/base contract, {tradeMoney:C0} ({name})"
                    + (addsDone > 0 ? $" [{addsDone} adds]" : "") + (curIsReversal ? " [reversal]" : "");
                tradePts = 0; tradeMoney = 0;
                entryTime = DateTime.MinValue;
                activeSigs.Clear(); addsDone = 0; addPending = false;
                statsDirty = true;

                // Reverse at stop, at most ReverseTimes times per prediction: each stopped trade turns into one opposite
                // trade until the limit is reached -- never an endless chain.
                if (name == "Stop loss" && execution.Order.OrderState == OrderState.Filled && ReverseAtStop && curRevCount < ReverseTimes
                    && Position.MarketPosition == MarketPosition.Flat)
                {
                    int rdir = inTradeLong ? -1 : 1;
                    string rside = rdir > 0 ? "LONG" : "SHORT";
                    if (!EnableTrading || !tradingAllowed || dailyPnL <= -DailyLossLimit)
                        lastDecision = $"no reversal -- {(dailyPnL <= -DailyLossLimit ? "daily loss limit" : "trading off / blocked")}";
                    else
                    {
                        string rsig = rdir > 0 ? SigRevLong : SigRevShort;
                        SetStopLoss(rsig, CalculationMode.Ticks, StopTicks, false);
                        SetProfitTarget(rsig, CalculationMode.Ticks, TargetFor());
                        curSig = rsig; curIsReversal = true; curRevCount++;
                        entryPending = true;
                        if (rdir > 0) EnterLong(0, Contracts, rsig); else EnterShort(0, Contracts, rsig);
                        lastDecision = $"REVERSED to {rside} after the stop ({curRevCount} of {ReverseTimes})";
                        Log("REVERSE", rside, price, curRevCount, 0, $"reversal {curRevCount} of {ReverseTimes} after stop");
                        Print($"{LogTag} stop hit -> REVERSE to {rside} ({curRevCount} of {ReverseTimes})");
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Flat && !entryPending)
                    curSig = null;
                PanelRefresh();
            }
            catch (Exception ex) { Print($"{LogTag} execution error: {ex.Message}"); }
        }

        /// <summary>Trailing stop like an ATM stop strategy: once the trade is TrailTrigger ticks in profit the stop moves to
        /// entry + (TrailTrigger - TrailStop), then follows in TrailStep-tick steps. Only ever tightens.</summary>
        private void Trail()
        {
            int dir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
            double profit = (Closes[0][0] - Position.AveragePrice) * dir / TickSize;
            if (profit > bestProfitTicks) bestProfitTicks = profit;
            if (bestProfitTicks < TrailTriggerTicks) return;
            int steps = TrailStepTicks > 0 ? (int)Math.Floor((bestProfitTicks - TrailTriggerTicks) / TrailStepTicks) : 0;
            double offset = (TrailTriggerTicks - TrailStopTicks) + steps * TrailStepTicks;
            double newStop = Instrument.MasterInstrument.RoundToTickSize(Position.AveragePrice + dir * offset * TickSize);
            if ((newStop - curStopPrice) * dir <= 0) return;
            curStopPrice = newStop;
            ApplyStop();
        }

        /// <summary>One shared stop for the whole position: the original entry and every add.</summary>
        private void ApplyStop()
        {
            foreach (string s in activeSigs)
                SetStopLoss(s, CalculationMode.Price, curStopPrice, false);
        }

        /// <summary>Profit target in ticks: switched off (far away) while scaling in with Let Winner Run.</summary>
        private int TargetFor()
        {
            return ScaleIn && LetWinnerRun ? 100000 : TargetTicks;
        }

        /// <summary>Scale in: add AddContracts each time price has moved AddEveryTicks further in our favour beyond the
        /// last entry / add, up to MaxAdds times. Only ever adds to a WINNING position.</summary>
        private void TryAdd()
        {
            if (UseAtm || addPending || entryPending || addsDone >= MaxAdds || activeSigs.Count == 0) return;
            if (!EnableTrading || !tradingAllowed || dailyPnL <= -DailyLossLimit) return;
            int dir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
            double px = Closes[0][0];
            if ((px - lastAddPrice) * dir < AddEveryTicks * TickSize) return;
            if ((px - Position.AveragePrice) * dir <= 0) return;                 // never add to a loser
            string an = SigAddPrefix + (addsDone + 1);
            SetStopLoss(an, CalculationMode.Price, curStopPrice, false);         // the add shares the position's stop
            SetProfitTarget(an, CalculationMode.Ticks, TargetFor());
            addPending = true;
            if (dir > 0) EnterLong(0, AddContracts, an); else EnterShort(0, AddContracts, an);
            Print($"{LogTag} SCALE IN: add {addsDone + 1} of {MaxAdds}, {AddContracts} @ ~{px:F2}");
        }

        // ---- server connection -------------------------------------------------------------------------------
        private void Connect()
        {
            if (client != null || DateTime.Now < nextConnect) return;
            nextConnect = DateTime.Now.AddSeconds(10);
            var c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(ServerHost, ServerPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(2000)) throw new IOException("connect timed out");
                c.EndConnect(ar);
                c.NoDelay = true;
                c.SendTimeout = ServerTimeoutMs;
                c.ReceiveTimeout = ServerTimeoutMs;
                NetworkStream ns = c.GetStream();
                var w = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var r = new StreamReader(ns, new UTF8Encoding(false));
                w.WriteLine("{\"type\":\"hello\",\"pattern\":" + PatternBars + ",\"train_bars\":" + TrainingBars
                    + ",\"retrain_every\":" + RetrainEvery + ",\"model\":\"" + Model + "\",\"instrument\":\"" + signalName + "\"}");
                string ack = r.ReadLine();
                if (ack == null) throw new IOException("no hello reply");
                // Reconnect: re-send the bars already seen (all but the one about to be sent by BarClosed, if any) so the
                // server -- fresh connection or freshly restarted -- does not warm up again from zero.
                int replay = 0;
                foreach (string bar in sentBars)
                {
                    if (ReferenceEquals(bar, currentBarJson)) continue;
                    w.WriteLine(bar.Replace("\"type\":\"bar\"", "\"type\":\"hist\""));
                    replay++;
                }
                client = c; writer = w; reader = r;
                failLogged = false;
                serverStatus = Field(ack, "status") ?? "connected";
                Print($"{LogTag} server connected ({ServerHost}:{ServerPort}): {serverStatus}"
                    + (replay > 0 ? $"; re-sent {replay} earlier bars, no new warm-up needed" : ""));
                if (replay > 0) Log("RECONNECT", "-", 0, replay, 0, $"re-sent {replay} bars to the server");
                statsDirty = true;
            }
            catch (Exception ex)
            {
                try { c.Close(); } catch { }
                if (!failLogged)
                    Print($"{LogTag} predict server {ServerHost}:{ServerPort} not reachable ({ex.Message}). "
                        + "Start C:\\Users\\hwth0\\.claude\\projects\\orderflow_predict\\start_predict_server.bat. Retrying every 10 s; no trades meanwhile.");
                failLogged = true;
                serverStatus = "not reachable -- start start_predict_server.bat";
                statsDirty = true;
            }
        }

        private string Request(string json)
        {
            if (client == null) return null;
            try
            {
                writer.WriteLine(json);
                string reply = reader.ReadLine();
                if (reply == null) throw new IOException("server closed the connection");
                return reply;
            }
            catch (Exception ex)
            {
                Print($"{LogTag} server connection lost ({ex.Message}); reconnecting (the server starts a fresh warm-up)");
                Disconnect(false);
                return null;
            }
        }

        private void Disconnect(bool bye)
        {
            if (client == null) return;
            try { if (bye) writer.WriteLine("{\"type\":\"bye\"}"); } catch { }
            try { client.Close(); } catch { }
            client = null; writer = null; reader = null;
        }

        private static string Field(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\"((?:[^\"\\\\]|\\\\.)*)\"|[^,}\\s]+)");
            if (!m.Success) return null;
            return m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value;
        }

        private static double ParseDouble(string s, double fallback)
        {
            double v;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        // ---- info panel under Chart Trader (same layout as RenkoBreakoutStrategyV4) -------------------------------
        private static System.Windows.Media.Brush Rgb(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();   // frozen brushes can be handed from the strategy thread to the UI thread
            return br;
        }

        private static readonly System.Windows.Media.Brush PBack   = Rgb(0x1E, 0x1E, 0x1E);
        private static readonly System.Windows.Media.Brush PBorder = Rgb(0x3C, 0x3C, 0x3C);
        private static readonly System.Windows.Media.Brush PHead   = Rgb(0x4F, 0xC3, 0xF7);
        private static readonly System.Windows.Media.Brush PLabel  = Rgb(0x9E, 0x9E, 0x9E);
        private static readonly System.Windows.Media.Brush PText   = Rgb(0xE0, 0xE0, 0xE0);
        private static readonly System.Windows.Media.Brush PGood   = Rgb(0x66, 0xBB, 0x6A);
        private static readonly System.Windows.Media.Brush PWarn   = Rgb(0xFF, 0xB7, 0x4D);
        private static readonly System.Windows.Media.Brush PBad    = Rgb(0xEF, 0x53, 0x50);

        // Section header, then (key, label) rows.
        private static readonly string[][] PanelLayout =
        {
            new[] { "#PREDICTION SERVER" },
            new[] { "server", "Server" }, new[] { "status", "Status" }, new[] { "setup", "Model" }, new[] { "signal", "Order flow from" },
            new[] { "#PREDICTION" },
            new[] { "bar", "Last bar" }, new[] { "pup", "Next bar p(up)" }, new[] { "ntrain", "Trained on" },
            new[] { "decision", "Decision" },
            new[] { "#ACCURACY (LIVE)" },
            new[] { "hit", "Hit rate" }, new[] { "verdict", "Verdict" },
            new[] { "#POSITION" },
            new[] { "pos", "Position" }, new[] { "sl", "Stop loss" }, new[] { "tp", "Target" },
            new[] { "upnl", "Unrealized P&L" }, new[] { "held", "Time in trade" },
            new[] { "#RECORD" },
            new[] { "trades", "Trades / wins" }, new[] { "net", "Net" }, new[] { "lastexit", "Last exit" },
            new[] { "#RISK" },
            new[] { "trading", "Trading" }, new[] { "rules", "Entry / exits" }, new[] { "session", "Session" },
            new[] { "daily", "Today P&L / limit" },
        };

        private void PanelInsert()
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    chartWindow = System.Windows.Window.GetWindow(ChartControl.Parent) as NinjaTrader.Gui.Chart.Chart;
                    if (chartWindow == null || panelRoot != null) return;
                    var trader = chartWindow.FindFirst("ChartWindowChartTraderControl") as NinjaTrader.Gui.Chart.ChartTrader;
                    ctGrid = trader != null ? trader.Content as System.Windows.Controls.Grid : null;
                    if (ctGrid == null)
                    {
                        Print($"{LogTag} Info panel: Chart Trader not found -- open Chart Trader (chart toolbar) and re-enable to see it.");
                        return;
                    }
                    panelRoot = BuildPanel();
                    ctRow = new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) };
                    ctGrid.RowDefinitions.Add(ctRow);
                    System.Windows.Controls.Grid.SetRow(panelRoot, ctGrid.RowDefinitions.Count - 1);
                    System.Windows.Controls.Grid.SetColumnSpan(panelRoot, Math.Max(1, ctGrid.ColumnDefinitions.Count));
                    ctGrid.Children.Add(panelRoot);
                    chartWindow.MainTabControl.SelectionChanged += OnChartTabChanged;
                    panelRoot.Visibility = PanelTabSelected() ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Print($"{LogTag} Info panel could not be added: {ex.Message}");
                }
            });
        }

        private void PanelRemove()
        {
            if (ChartControl == null) return;
            var root = panelRoot; var grid = ctGrid; var row = ctRow; var win = chartWindow;
            panelRoot = null;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (win != null) win.MainTabControl.SelectionChanged -= OnChartTabChanged;
                    if (grid != null && root != null) grid.Children.Remove(root);
                    if (grid != null && row != null) grid.RowDefinitions.Remove(row);
                }
                catch { }
            });
        }

        private bool PanelTabSelected()
        {
            foreach (System.Windows.Controls.TabItem tab in chartWindow.MainTabControl.Items)
            {
                var ct = tab.Content as NinjaTrader.Gui.Chart.ChartTab;
                if (ct != null && ct.ChartControl == ChartControl && tab == chartWindow.MainTabControl.SelectedItem)
                    return true;
            }
            return false;
        }

        private void OnChartTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (panelRoot != null && e.OriginalSource == chartWindow.MainTabControl)
                panelRoot.Visibility = PanelTabSelected() ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }

        private System.Windows.Controls.Border BuildPanel()
        {
            panelCells.Clear();
            var stack = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(6, 4, 6, 6) };
            System.Windows.Controls.Grid grid = null;
            foreach (string[] item in PanelLayout)
            {
                if (item[0].StartsWith("#"))
                {
                    stack.Children.Add(new System.Windows.Controls.TextBlock
                    {
                        Text = item[0].Substring(1), Foreground = PHead, FontWeight = System.Windows.FontWeights.Bold,
                        FontSize = 11, Margin = new System.Windows.Thickness(0, 6, 0, 2)
                    });
                    grid = new System.Windows.Controls.Grid();
                    grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(112) });
                    grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                    stack.Children.Add(grid);
                    continue;
                }
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
                var label = new System.Windows.Controls.TextBlock { Text = item[1], Foreground = PLabel, FontSize = 11, Margin = new System.Windows.Thickness(0, 1, 6, 1) };
                var value = new System.Windows.Controls.TextBlock { Text = "-", Foreground = PText, FontSize = 11, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 1, 0, 1) };
                System.Windows.Controls.Grid.SetRow(label, r);
                System.Windows.Controls.Grid.SetRow(value, r);
                System.Windows.Controls.Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
                panelCells[item[0]] = value;
            }
            return new System.Windows.Controls.Border
            {
                Background = PBack, BorderBrush = PBorder, BorderThickness = new System.Windows.Thickness(1),
                Margin = new System.Windows.Thickness(0, 6, 0, 0),
                Child = new System.Windows.Controls.ScrollViewer
                {
                    VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    Content = stack
                }
            };
        }

        /// <summary>Collects every value on the strategy thread, then hands the finished strings to the UI thread.</summary>
        private void PanelRefresh()
        {
            nextPanelRefresh = DateTime.Now.AddSeconds(1);
            if (ChartControl == null || panelRoot == null) return;
            try
            {
                var rows = new List<Tuple<string, string, System.Windows.Media.Brush>>();
                Action<string, string, System.Windows.Media.Brush> add = (k, v, b) => rows.Add(Tuple.Create(k, v, b));
                double pv = Instrument.MasterInstrument.PointValue;

                // server
                add("server", client != null ? $"connected  {ServerHost}:{ServerPort}" : $"NOT connected  {ServerHost}:{ServerPort}  -- start start_predict_server.bat",
                    client != null ? PGood : PBad);
                add("status", serverStatus, serverStatus == "ok" ? PGood : serverStatus.StartsWith("warming") ? PWarn : client != null ? PText : PBad);
                add("setup", $"{Model}  |  pattern {PatternBars} bars  |  learns from last {(TrainingBars == 0 ? "ALL" : TrainingBars.ToString())} bars, retrain every {RetrainEvery}", null);
                add("signal", $"{signalName} ({BarsPeriod}), rows {LevelTicks} ticks  |  bars sent {barsSent}", PLabel);

                // prediction
                add("bar", lastBarInfo, null);
                if (double.IsNaN(lastP))
                    add("pup", serverStatus.StartsWith("warming") ? "no prediction yet (warming up)" : "-", PLabel);
                else
                {
                    string call = lastP >= 0.5 + ConfidenceMargin ? "UP" : lastP <= 0.5 - ConfidenceMargin ? "DOWN" : "no call";
                    string traded = ReverseSignals && call != "no call" ? $"  ->  trade {(call == "UP" ? "SELL" : "BUY")} (REVERSED)" : "";
                    add("pup", $"{lastP:F3}  ->  {call}{traded}   ({lastPredTime})", call == "UP" ? PGood : call == "DOWN" ? PBad : PWarn);
                }
                add("ntrain", Model == Ofp3Model.Kronos ? $"zero-shot (pre-trained), reads the last {Math.Min(Math.Max(PatternBars, 30), 512)} bars, 7 forecasts per bar"
                                                       : $"{lastNTrain} examples", null);
                add("decision", lastDecision,
                    lastDecision.StartsWith("TAKE") ? PGood : lastDecision.Contains("SKIPPED") || lastDecision.StartsWith("no trade") ? PWarn
                    : lastDecision.StartsWith("no prediction") ? PBad : PText);

                // accuracy
                if (double.IsNaN(lastHit) || lastScored == 0)
                {
                    add("hit", "no scored predictions yet", PLabel);
                    add("verdict", "-", PLabel);
                }
                else
                {
                    double se = Math.Sqrt(0.25 / lastScored);
                    add("hit", $"{lastHit * 100:F1}% of {lastScored} bars  (luck range +/-{2 * se * 100:F1}%)",
                        lastHit >= 0.542 ? PGood : lastHit >= 0.5 ? PWarn : PBad);
                    string verdict = lastScored < 300 ? $"too few bars to judge ({lastScored}/300+)"
                        : lastHit - 2 * se > 0.542 ? "above the 54.2 % break-even beyond luck"
                        : lastHit - 2 * se > 0.5 ? "better than a coin flip, but not proven above break-even"
                        : "not distinguishable from a coin flip (50 %)";
                    add("verdict", verdict, lastScored < 300 ? PLabel : lastHit - 2 * se > 0.542 ? PGood : lastHit - 2 * se > 0.5 ? PWarn : PBad);
                }

                // position
                if (atmId != null)
                {
                    string side = atmDir > 0 ? "LONG" : "SHORT";
                    MarketPosition amp = atmFilled ? GetAtmStrategyMarketPosition(atmId) : MarketPosition.Flat;
                    if (!atmFilled || amp == MarketPosition.Flat)
                    {
                        add("pos", $"{side} entry working via ATM '{AtmTemplate}' ({EntryMode})", PWarn);
                        foreach (string k in new[] { "sl", "tp", "upnl", "held" }) add(k, "-", PLabel);
                    }
                    else
                    {
                        int q = GetAtmStrategyPositionQuantity(atmId);
                        double avg = GetAtmStrategyPositionAveragePrice(atmId);
                        double u = GetAtmStrategyUnrealizedProfitLoss(atmId);
                        add("pos", $"{side} {q} @ {avg:F2}  (ATM '{AtmTemplate}')", atmDir > 0 ? PGood : PBad);
                        add("sl", AtmOrders("Stop"), PBad);
                        add("tp", AtmOrders("Target"), PGood);
                        add("upnl", $"{u:C0}", u >= 0 ? PGood : PBad);
                        add("held", entryTime != DateTime.MinValue && lastTradeTime >= entryTime
                            ? $"{(lastTradeTime - entryTime).TotalMinutes:F1} min  (time exit at {TimeExitMinutes})" : "-", null);
                    }
                }
                else if (Position.MarketPosition == MarketPosition.Flat && PositionAccount.MarketPosition != MarketPosition.Flat)
                {
                    add("pos", $"ACCOUNT holds {PositionAccount.MarketPosition} {PositionAccount.Quantity} @ {PositionAccount.AveragePrice:F2} -- "
                        + (orphanActive ? "UNMANAGED (ATM ended): close it / check its stop" : "not opened by this strategy") + "  |  no new trades until flat", PBad);
                    foreach (string k in new[] { "tp", "upnl", "held" }) add(k, "-", PLabel);
                    add("sl", AtmOrders("Stop"), PBad);
                }
                else if (Position.MarketPosition == MarketPosition.Flat)
                {
                    add("pos", entryPending ? $"FLAT  |  {(EntryMode == Ofp3EntryMode.Limit ? "limit entry working" : "entry pending")}" : "FLAT",
                        entryPending ? PWarn : PLabel);
                    foreach (string k in new[] { "sl", "tp", "upnl", "held" }) add(k, "-", PLabel);
                }
                else
                {
                    bool isLong = Position.MarketPosition == MarketPosition.Long;
                    double avg = Position.AveragePrice;
                    double sl = curStopPrice > 0 ? curStopPrice : (isLong ? avg - StopTicks * TickSize : avg + StopTicks * TickSize);
                    double tp = isLong ? avg + TargetTicks * TickSize : avg - TargetTicks * TickSize;
                    double last = Closes[0][0];
                    double upts = (last - avg) * (isLong ? 1 : -1);
                    double slPts = (avg - sl) * (isLong ? 1 : -1);          // > 0 = risk left, < 0 = profit locked in
                    add("pos", $"{(isLong ? "LONG" : "SHORT")} {Position.Quantity} @ {avg:F2}"
                        + (ScaleIn ? $"  |  adds {addsDone} of {MaxAdds}{(addPending ? " (add working)" : "")}" : ""), isLong ? PGood : PBad);
                    add("sl", $"{sl:F2}  ({(slPts >= 0 ? "risk" : "locks in")} {Math.Abs(slPts):F2} pts = {Math.Abs(slPts) * pv * Position.Quantity:C0})"
                        + (ScaleIn ? "  shared by all entries" : ""), slPts >= 0 ? PBad : PGood);
                    add("tp", ScaleIn && LetWinnerRun ? "none -- letting the winner run (trailing / shared stop)"
                        : $"{tp:F2}  ({TargetTicks * TickSize:F2} pts, {TargetTicks * TickSize * pv * Position.Quantity:C0})", PGood);
                    add("upnl", $"{upts:+0.00;-0.00} pts  =  {upts * pv * Position.Quantity:C0}", upts >= 0 ? PGood : PBad);
                    add("held", entryTime != DateTime.MinValue && lastTradeTime >= entryTime
                        ? $"{(lastTradeTime - entryTime).TotalMinutes:F1} min  (time exit at {TimeExitMinutes})" : "-", null);
                }

                // record
                add("trades", trades > 0 ? $"{trades} / {wins}  ({100.0 * wins / trades:F0}% wins)" : "0", null);
                add("net", trades > 0 ? $"{netPts:+0.00;-0.00} pts/contract  ({netPts / trades:+0.00;-0.00}/trade)" : "-",
                    trades == 0 ? PLabel : netPts >= 0 ? PGood : PBad);
                add("lastexit", lastExit, null);

                // risk
                add("trading", EnableTrading ? (tradingAllowed ? $"ON  |  {(Account != null ? Account.Name : "?")}" : "BLOCKED -- " + blockReason)
                                             : "SHADOW -- Enable Trading is off (predictions only)",
                    EnableTrading && tradingAllowed ? PGood : PWarn);
                add("rules", $"{EntryMode}{(EntryMode == Ofp3EntryMode.Limit ? $" {LimitOffsetTicks} ticks better, one bar" : "")}  |  "
                    + (ReverseSignals ? "REVERSE SIGNALS  |  " : "")
                    + (UseAtm ? $"ATM '{AtmTemplate}' (stop / target / qty from the template)"
                              : $"stop {StopTicks} / target {TargetTicks} ticks  |  {Contracts} contract(s)"
                                + (TrailTriggerTicks > 0 ? $"  |  trail after +{TrailTriggerTicks}t, {TrailStopTicks}t behind, step {TrailStepTicks}t" : "")
                                + (ReverseAtStop ? "  |  REVERSE at stop x" + ReverseTimes : "")
                                + (ScaleIn ? $"  |  SCALE IN +{AddContracts} every {AddEveryTicks}t, max {MaxAdds} adds (max {Contracts + MaxAdds * AddContracts} contracts)"
                                             + (LetWinnerRun ? ", no target" : "") : ""))
                    + $"  |  margin {ConfidenceMargin:F2}", null);
                add("session", $"{Session}{(Session == Ofp3SessionMode.RthOnly ? $" {RthStart:0000}-{RthEnd:0000}" : "")}{(NewsBlackout ? "  |  news blackout 08:20-08:45" : "")}", null);
                add("daily", $"{dailyPnL:C0}  |  limit -{DailyLossLimit:C0}" + (dailyPnL <= -DailyLossLimit ? "  HIT -- paused" : ""),
                    dailyPnL <= -DailyLossLimit ? PBad : dailyPnL >= 0 ? PGood : PWarn);

                ChartControl.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var row in rows)
                    {
                        System.Windows.Controls.TextBlock tbk;
                        if (!panelCells.TryGetValue(row.Item1, out tbk)) continue;
                        tbk.Text = row.Item2;
                        tbk.Foreground = row.Item3 ?? PText;
                    }
                });
            }
            catch { }
        }

        // ---- log and stats -----------------------------------------------------------------------------------
        private void Log(string ev, string side, double p1, double p2, double pts, string note)
        {
            try
            {
                if (!File.Exists(logPath))
                    File.AppendAllText(logPath, "wallclock,market_time,event,side,price1,value2,pts,note,model,pattern,train_bars,account,instrument\n");
                File.AppendAllText(logPath, string.Join(",",
                    DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    lastTradeTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), ev, side,
                    p1.ToString("F2", CultureInfo.InvariantCulture), p2.ToString("F4", CultureInfo.InvariantCulture),
                    pts.ToString("F2", CultureInfo.InvariantCulture), "\"" + note.Replace("\"", "'") + "\"",
                    Model, PatternBars, TrainingBars, Account != null ? Account.Name : "", Instrument.FullName) + "\n");
            }
            catch { }
        }

        private void DrawStats()
        {
            statsDirty = false;
            string text = $"OrderFlowPredictV3  |  {(EnableTrading ? (tradingAllowed ? "TRADING" : "BLOCKED") : "SHADOW (Enable Trading off)")}{(ReverseSignals ? "  |  REVERSED" : "")}  |  "
                + $"{Model}, pattern {PatternBars}, training {(TrainingBars == 0 ? "ALL" : TrainingBars.ToString())} bars  |  signal {signalName}"
                + $"\nserver: {serverStatus}"
                + $"\nlast: {lastPred}"
                + $"\nprediction hit rate: {hitRate}   (coin flip = 50 %; 1:1 needs ~54 % after costs)"
                + $"\ntrades {trades}, wins {wins}, net {netPts:+0.0;-0.0} pts ({(trades > 0 ? netPts / trades : 0):+0.00;-0.00}/trade before commission)  |  today ${dailyPnL:F0}";
            Draw.TextFixed(this, "OFPstats", text, TextPosition.TopLeft, Brushes.White,
                new SimpleFont("Arial", 11), Brushes.Transparent, Brushes.Black, 70);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Signal Instrument", Description = "Auto = the full-size contract of the chart's instrument and expiry (MNQ 12-26 -> NQ 12-26).", Order = 1, GroupName = "1. Prediction")]
        public string SignalInstrument { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Level Size (ticks)", Description = "Footprint row size sent to the server (match HWFootprint).", Order = 2, GroupName = "1. Prediction")]
        public int LevelTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Pattern Bars", Description = "How many recent bars the prediction looks at (e.g. 5 or 100).", Order = 3, GroupName = "1. Prediction")]
        public int PatternBars { get; set; }

        [NinjaScriptProperty]
        [Range(0, 100000)]
        [Display(Name = "Training Bars", Description = "The model learns from the last N bars (0 = all bars seen). Must be well above Pattern Bars: the server needs >= max(30, 2 x Pattern) examples.", Order = 4, GroupName = "1. Prediction")]
        public int TrainingBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Retrain Every (bars)", Order = 5, GroupName = "1. Prediction")]
        public int RetrainEvery { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Model", Description = "Kronos = pre-trained candle model (zero-shot): Pattern Bars = how many past bars it reads (120 recommended, 30-512); "
            + "Training Bars / Retrain Every are ignored. p(up) = share of 7 sampled forecasts that close higher.", Order = 6, GroupName = "1. Prediction")]
        public Ofp3Model Model { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 0.5)]
        [Display(Name = "Confidence Margin", Description = "Long if p(up) >= 0.5 + margin, short if <= 0.5 - margin, else no trade. 0 = trade every bar.", Order = 7, GroupName = "1. Prediction")]
        public double ConfidenceMargin { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reverse Signals", Description = "ON = trade against the prediction: p(up) high -> SELL, p(up) low -> BUY. The margin test is applied first, then the direction is flipped. "
            + "Stop / target / trail / scale-in / ATM apply to the position actually taken. The server's hit rate still scores the original prediction. UNTESTED.", Order = 8, GroupName = "1. Prediction")]
        public bool ReverseSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Server Host", Order = 9, GroupName = "1. Prediction")]
        public string ServerHost { get; set; }

        [NinjaScriptProperty]
        [Range(1, 65535)]
        [Display(Name = "Server Port", Order = 10, GroupName = "1. Prediction")]
        public int ServerPort { get; set; }

        [NinjaScriptProperty]
        [Range(500, 60000)]
        [Display(Name = "Server Timeout (ms)", Order = 11, GroupName = "1. Prediction")]
        public int ServerTimeoutMs { get; set; }

        [NinjaScriptProperty]
        [TypeConverter(typeof(Ofp3AtmTemplateConverter))]
        [Display(Name = "ATM Strategy", Description = "None = the strategy's own Stop / Target / Contracts. Or pick a saved ATM template (e.g. 50_100): "
            + "the entry is placed through it and the template's stop, target, quantity, breakeven and trailing manage the trade.", Order = 0, GroupName = "2. Trade")]
        public string AtmTemplate { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Scale In", Description = "Add to WINNING positions: each time price moves 'Add Every' ticks further in your favour beyond the last entry, "
            + "add 'Add Contracts', up to 'Max Adds' times. Never adds to a loser. After each add ONE shared stop for the whole position moves to "
            + "Stop (ticks) behind the newest add. Only without an ATM template.", Order = 1, GroupName = "4. Scale In")]
        public bool ScaleIn { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Add Every (ticks)", Description = "Favourable move beyond the last entry / add that triggers the next add.", Order = 2, GroupName = "4. Scale In")]
        public int AddEveryTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Adds", Description = "Maximum number of adds. Biggest position = Contracts + Max Adds x Add Contracts -- keep it within your account's limit (Lucid 50K: 4 minis / 40 micros).", Order = 3, GroupName = "4. Scale In")]
        public int MaxAdds { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Add Contracts", Description = "Contracts per add.", Order = 4, GroupName = "4. Scale In")]
        public int AddContracts { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Let Winner Run", Description = "On = no fixed profit target while scaling in: the position exits only at the shared (trailing) stop or Time Exit. Off = every entry keeps Target (ticks).", Order = 5, GroupName = "4. Scale In")]
        public bool LetWinnerRun { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Orphan Position", Description = "If an ATM ends (e.g. its stop / target was rejected) while the account still holds the position: "
            + "Flatten = close ALL positions on this instrument in this account at market (also manual ones); AlertOnly = alarm + no new trades until flat.", Order = 0, GroupName = "3. Safety")]
        public Ofp3OrphanAction OrphanAction { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reverse at Stop", Description = "When a trade's stop is hit, open the opposite trade (same stop / target / trail), up to "
            + "'Reverse Times' times per prediction -- never an endless chain. Only without an ATM template (ATM Strategy = None).", Order = 11, GroupName = "2. Trade")]
        public bool ReverseAtStop { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Reverse Times", Description = "Maximum reversals per prediction. 1 = the original trade can reverse once; if the reversal is stopped too, "
            + "it ends. 2 = reverse, and the reversal may reverse once more, and so on.", Order = 12, GroupName = "2. Trade")]
        public int ReverseTimes { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Trail Trigger (ticks)", Description = "0 = no trailing. Once the trade is this many ticks in profit, the stop starts trailing (like the ATM stop strategy's Profit Trigger). Only without an ATM.", Order = 13, GroupName = "2. Trade")]
        public int TrailTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Trail Stop (ticks)", Description = "Distance of the trailing stop behind price at the trigger (ATM 'Stop loss' of the trail step).", Order = 14, GroupName = "2. Trade")]
        public int TrailStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0, 10000)]
        [Display(Name = "Trail Step (ticks)", Description = "The stop moves up by this many ticks each time profit grows by this many (ATM 'Frequency'). 0 = move only once at the trigger.", Order = 15, GroupName = "2. Trade")]
        public int TrailStepTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Entry Mode", Description = "Market = at the next trade. Limit = Limit Offset ticks better than the close, valid for one bar.", Order = 1, GroupName = "2. Trade")]
        public Ofp3EntryMode EntryMode { get; set; }

        [NinjaScriptProperty]
        [Range(0, 1000)]
        [Display(Name = "Limit Offset (ticks)", Order = 2, GroupName = "2. Trade")]
        public int LimitOffsetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Stop (ticks)", Order = 3, GroupName = "2. Trade")]
        public int StopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1000)]
        [Display(Name = "Target (ticks)", Order = 4, GroupName = "2. Trade")]
        public int TargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 1440)]
        [Display(Name = "Time Exit (minutes)", Order = 5, GroupName = "2. Trade")]
        public int TimeExitMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Session", Order = 6, GroupName = "2. Trade")]
        public Ofp3SessionMode Session { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "RTH Start (HHMM, chart time)", Order = 7, GroupName = "2. Trade")]
        public int RthStart { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "RTH End (HHMM, chart time)", Order = 8, GroupName = "2. Trade")]
        public int RthEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Blackout 08:20-08:45", Order = 9, GroupName = "2. Trade")]
        public bool NewsBlackout { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Contracts", Order = 10, GroupName = "2. Trade")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit ($)", Order = 1, GroupName = "3. Safety")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading", Description = "Off = predictions are only logged.", Order = 2, GroupName = "3. Safety")]
        public bool EnableTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Non-Sim Account", Description = "Keep OFF.", Order = 3, GroupName = "3. Safety")]
        public bool AllowNonSimAccount { get; set; }
        #endregion
    }

    /// <summary>Drop-down of the saved ATM templates (Documents\NinjaTrader 8\templates\AtmStrategy\*.xml) plus "None".</summary>
    public class Ofp3AtmTemplateConverter : StringConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var list = new List<string> { "None" };
            try
            {
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "templates", "AtmStrategy");
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.xml"))
                        list.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            return new StandardValuesCollection(list);
        }
    }
}
