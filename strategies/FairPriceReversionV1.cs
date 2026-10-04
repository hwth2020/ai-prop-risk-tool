#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// FairPriceReversionV1 -- a mechanical version of the "fair pricing theory" described by JJ Simon (Chart Fanatics
// interview). Built from the video's description only: UNPROVEN, not backtested by its author here. Sim / playback only.
//
//  Put it on a 1 MINUTE NQ or MNQ chart. Chart time zone must be US Eastern (session / news times are chart time).
//  Orders are placed at the CLOSE of the signal candle (the next bar's open), like the video ("wait for the candle close").
//
//  Fair price
//   - Normal session: the OPEN price of the session (Session Starts, e.g. 09:30, 14:00, 18:00 ET). Each session is
//     traded for Session Window minutes (90).
//   - Scheduled news day (dates listed in News Dates, e.g. CPI / PPI at 08:30): the PRE-NEWS price (close of the candle
//     before the news). It stays the fair price through the news window (to ~11:00), also across the 09:30 open.
//
//  Trades (one position at a time, only inside a session / news window)
//   1. OPENING CONTINUATION: the opening candle's direction, if its close breaks the high / low of the previous
//      Structure Bars candles and agrees with the higher-timeframe bias (bias = the OPPOSITE of the last Bias Hours'
//      move, i.e. reversion). Target 38 / stop 25 points; if the opening candle is bigger than 25 points: 76 / 50
//      with half the contracts (min 1).
//   2. REVERSION TO FAIR PRICE: against the move, toward fair price, on
//        - a DISPLACEMENT candle: body larger than the previous candle's body, the previous candle is the opposite colour,
//          and it closes beyond the previous candle's wick; or
//        - a BREAK OF STRUCTURE: it closes beyond the most recent 3-candle swing low (for shorts) / high (for longs),
//          the first close beyond it.
//      The target must have >= Min Fair Fraction (80 %) of its points still available to fair price.
//
//  Account profile (video: static risk / reward per account type)
//   Evaluation        : static Eval Target / Eval Stop (38 / 25); displacement and break of structure.
//   FundedConsistency : same static 38 / 25 (about 1:1.5); break of structure only.
//   FundedNoConsistency: the LARGEST target in Funded Targets (50,75,100) that fits 80 % of the points to fair price;
//                        stop = target / Funded RR (min Min Stop Points); break of structure only.
//
//  Rules: stop for the session after Max Losses in a row (3); daily loss limit; optional exit at the window's end.
//  NOT in this version: the 45-account layering / per-account dollar sizing, discretionary fair-price changes
//  (unexpected news, trend days), manual structure exits, break-even moves. Reverse Signals flips every trade (untested).
//  Safety: Enable Trading can be switched off; in real time only Sim / Playback accounts unless allowed.

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum FpProfile { Evaluation, FundedConsistency, FundedNoConsistency }

    public class FairPriceReversionV1 : Strategy
    {
        private const string LogTag = "[FPR-V1]";
        private const string Sig = "FPR";

        // fair price / window state
        private double fair;
        private string fairKind = "none";
        private DateTime windowStart = DateTime.MinValue, windowEnd = DateTime.MinValue, openingBarTime = DateTime.MinValue;
        private int fairBar;
        private int drawId;

        // parsed settings
        private readonly HashSet<string> newsDates = new HashSet<string>();
        private readonly List<TimeSpan> sessionStarts = new List<TimeSpan>();
        private TimeSpan newsTime;
        private double[] fundedTargets = new double[0];

        // risk / stats
        private int sessionLosses, tradeCountSeen, totalTrades, wins, tradesToday;
        private double dailyPnL, netMoney;
        private DateTime pnlDate = DateTime.MinValue;
        private bool tradingAllowed = true;
        private string blockReason = "", lastNote = "none yet";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Mechanical 'fair pricing theory': continuation of the opening candle and reversion to the session open / pre-news price (unproven).";
                Name        = "FairPriceReversionV1";
                Calculate   = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                BarsRequiredToTrade          = 25;
                IsOverlay = true;

                SessionStarts      = "0930,1400,1800";
                SessionWindowMinutes = 90;
                NewsEnabled        = true;
                NewsDates          = "";
                NewsTime           = 830;
                NewsWindowMinutes  = 150;

                UseContinuation    = true;
                RequireBias        = true;
                BiasHours          = 6;
                StructureBars      = 3;
                BosLookback        = 15;
                ContTarget         = 38;
                ContStop           = 25;
                ContBigCandle      = 25;

                Profile            = FpProfile.Evaluation;
                EvalTarget         = 38;
                EvalStop           = 25;
                FundedTargets      = "50,75,100";
                FundedRR           = 3.0;
                MinStopPoints      = 25;
                MinFairFraction    = 0.8;
                Contracts          = 1;

                MaxLosses          = 3;
                DailyLossLimit     = 1500;
                ExitAtWindowEnd    = false;
                ReverseSignals     = false;
                EnableTrading      = true;
                AllowNonSimAccount = false;
            }
            else if (State == State.DataLoaded)
            {
                newsDates.Clear(); sessionStarts.Clear();
                foreach (string d in (NewsDates ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    newsDates.Add(d.Trim());
                foreach (string s in (SessionStarts ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int v;
                    if (int.TryParse(s, out v)) sessionStarts.Add(Hhmm(v));
                }
                newsTime = Hhmm(NewsTime);
                var list = new List<double>();
                foreach (string s in (FundedTargets ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    double v;
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0) list.Add(v);
                }
                list.Sort(); list.Reverse();                       // largest first
                fundedTargets = list.ToArray();

                if (Account != null && !AllowNonSimAccount && !Account.Name.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                    && !Account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase))
                {
                    tradingAllowed = false;
                    blockReason = $"account {Account.Name} is not a Sim / Playback account -- live trading blocked";
                }
                Print($"{LogTag} {Instrument.FullName} {BarsPeriod}: profile {Profile}, sessions {SessionStarts} x {SessionWindowMinutes} min, "
                    + $"news {(NewsEnabled ? newsDates.Count + " date(s) at " + NewsTime : "off")}, reverse {ReverseSignals}, trading {EnableTrading}");
            }
        }

        private static TimeSpan Hhmm(int v) { return new TimeSpan(Math.Min(23, v / 100), Math.Min(59, v % 100), 0); }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;
            DateTime t = Time[0], tp = Time[1];

            UpdateTradeStats();
            if (t.Date != pnlDate) { pnlDate = t.Date; dailyPnL = 0; tradesToday = 0; }

            // ---- session / news detection (a bar's time is its CLOSE time) ----------------------------------
            if (NewsEnabled && newsDates.Contains(t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            {
                DateTime ns = t.Date + newsTime;
                if (tp <= ns && t > ns) StartNews(ns, t);
            }
            foreach (TimeSpan s in sessionStarts)
            {
                DateTime st = t.Date + s;
                if (tp <= st && t > st) StartSession(st, t);
            }

            bool inWindow = fairKind != "none" && t > windowStart && t <= windowEnd;
            if (inWindow) DrawFair();
            DrawStats();

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                if (ExitAtWindowEnd && t > windowEnd)
                {
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong("FPR window end", Sig);
                    else ExitShort("FPR window end", Sig);
                }
                return;
            }
            if (!inWindow) return;
            if (!CanTrade()) return;

            if (t == openingBarTime)
            {
                if (UseContinuation) TryContinuation();
                return;
            }
            TryReversion();
        }

        // ---- fair price ---------------------------------------------------------------------------------------
        private void StartNews(DateTime ns, DateTime t)
        {
            fair = Close[1];                                   // the candle before the news candle = the pre-news price
            fairKind = "pre-news";
            windowStart = t;
            windowEnd = ns.AddMinutes(NewsWindowMinutes);
            sessionLosses = 0;
            openingBarTime = DateTime.MinValue;
            fairBar = CurrentBar; drawId++;
            Print($"{LogTag} {t:yyyy-MM-dd HH:mm} NEWS: fair price = pre-news {fair:F2}");
        }

        private void StartSession(DateTime st, DateTime t)
        {
            if (fairKind == "pre-news" && t < windowEnd)
            {
                // the open falls inside a news window: the pre-news price stays fair (it is stronger than the open)
                windowEnd = new DateTime(Math.Max(windowEnd.Ticks, st.AddMinutes(SessionWindowMinutes).Ticks));
            }
            else
            {
                fair = Open[0];                                // the first candle of the session opens at the session open
                fairKind = "open";
                windowStart = st;
                windowEnd = st.AddMinutes(SessionWindowMinutes);
                sessionLosses = 0;
                fairBar = CurrentBar; drawId++;
            }
            openingBarTime = t;                                // this bar is the opening candle
            Print($"{LogTag} {t:yyyy-MM-dd HH:mm} SESSION: fair price = {fairKind} {fair:F2}");
        }

        // ---- entries ------------------------------------------------------------------------------------------
        private void TryContinuation()
        {
            int dir = Close[0] > Open[0] ? 1 : Close[0] < Open[0] ? -1 : 0;
            if (dir == 0) return;
            double ref1 = dir > 0 ? Highest(1, StructureBars, true) : Highest(1, StructureBars, false);
            bool broke = dir > 0 ? Close[0] > ref1 : Close[0] < ref1;
            if (!broke) { lastNote = $"{Time[0]:HH:mm} opening candle did not break structure"; return; }
            if (RequireBias)
            {
                int bias = HtfBias();
                if (bias != dir) { lastNote = $"{Time[0]:HH:mm} opening candle against the {BiasHours}h reversion bias"; return; }
            }
            bool big = (High[0] - Low[0]) > ContBigCandle;
            double tp = big ? ContTarget * 2 : ContTarget, sl = big ? ContStop * 2 : ContStop;
            int qty = big ? Math.Max(1, Contracts / 2) : Contracts;
            Place(dir, tp, sl, qty, "continuation");
        }

        private void TryReversion()
        {
            double avail = Math.Abs(Close[0] - fair);          // points still available to fair price
            if (avail <= 0) return;
            int dir = Close[0] > fair ? -1 : 1;                // toward fair price

            bool disp = Profile == FpProfile.Evaluation && IsDisplacement(dir);
            bool bos = IsBreakOfStructure(dir);
            if (!disp && !bos) return;

            double tp, sl;
            if (Profile == FpProfile.FundedNoConsistency)
            {
                tp = 0;
                foreach (double c in fundedTargets) if (avail >= MinFairFraction * c) { tp = c; break; }
                if (tp <= 0) { lastNote = $"{Time[0]:HH:mm} signal skipped: only {avail:F0} pts to fair price"; return; }
                sl = Math.Max(MinStopPoints, tp / Math.Max(0.1, FundedRR));
            }
            else
            {
                tp = EvalTarget; sl = EvalStop;
                if (avail < MinFairFraction * tp) { lastNote = $"{Time[0]:HH:mm} signal skipped: only {avail:F0} pts to fair price"; return; }
            }
            Place(dir, tp, sl, Contracts, disp ? "displacement" : "break of structure");
        }

        // Displacement toward fair price (dir -1 = short): body larger than the previous body, previous candle of the
        // opposite colour, and a close beyond the previous candle's wick.
        private bool IsDisplacement(int dir)
        {
            double body0 = Math.Abs(Close[0] - Open[0]), body1 = Math.Abs(Close[1] - Open[1]);
            if (body0 <= body1) return false;
            if (dir < 0) return Close[0] < Open[0] && Close[1] > Open[1] && Close[0] < Low[1];
            return Close[0] > Open[0] && Close[1] < Open[1] && Close[0] > High[1];
        }

        // Break of structure: the first close beyond the most recent 3-candle swing low (short) / swing high (long).
        private bool IsBreakOfStructure(int dir)
        {
            int max = Math.Min(BosLookback, CurrentBar - 3);
            for (int j = 2; j <= max; j++)
            {
                bool pivot = dir < 0 ? Low[j] < Low[j + 1] && Low[j] < Low[j - 1]
                                     : High[j] > High[j + 1] && High[j] > High[j - 1];
                if (!pivot) continue;
                double level = dir < 0 ? Low[j] : High[j];
                if (dir < 0 ? Close[0] >= level : Close[0] <= level) return false;       // not broken now
                for (int k = 1; k < j; k++)                                                // and not broken before
                    if (dir < 0 ? Close[k] < level : Close[k] > level) return false;
                return true;
            }
            return false;
        }

        // Highest high (up == true) or lowest low of `count` bars starting `from` bars ago.
        private double Highest(int from, int count, bool up)
        {
            double v = up ? double.MinValue : double.MaxValue;
            for (int i = from; i < from + count && i <= CurrentBar; i++)
                v = up ? Math.Max(v, High[i]) : Math.Min(v, Low[i]);
            return v;
        }

        // Higher-timeframe bias = the OPPOSITE of the move over the last BiasHours (reversion). +1 long, -1 short, 0 none.
        private int HtfBias()
        {
            DateTime target = Time[0].AddHours(-BiasHours);
            int i = 0;
            while (i < CurrentBar && i < 5000 && Time[i] > target) i++;
            if (i >= CurrentBar || i >= 5000) return 0;
            double move = Close[0] - Close[i];
            return move > 0 ? -1 : move < 0 ? 1 : 0;
        }

        private bool CanTrade()
        {
            if (!EnableTrading) return false;
            if (State == State.Realtime && !tradingAllowed) return false;
            if (sessionLosses >= MaxLosses) return false;
            if (DailyLossLimit > 0 && dailyPnL <= -DailyLossLimit) return false;
            return true;
        }

        private void Place(int dir, double tpPts, double slPts, int qty, string why)
        {
            if (ReverseSignals) dir = -dir;
            SetStopLoss(Sig, CalculationMode.Ticks, Math.Max(1, Math.Round(slPts / TickSize)), false);
            SetProfitTarget(Sig, CalculationMode.Ticks, Math.Max(1, Math.Round(tpPts / TickSize)));
            if (dir > 0) EnterLong(qty, Sig); else EnterShort(qty, Sig);
            tradesToday++;
            lastNote = $"{Time[0]:HH:mm} {(dir > 0 ? "LONG" : "SHORT")} x{qty} ({why}{(ReverseSignals ? ", REVERSED" : "")}) tp {tpPts:F0} / sl {slPts:F0} pts, fair {fair:F2} ({fairKind})";
            Print($"{LogTag} {lastNote}");
        }

        // closed trades -> loss streak, daily P&L, stats
        private void UpdateTradeStats()
        {
            int n = SystemPerformance.AllTrades.Count;
            while (tradeCountSeen < n)
            {
                Trade tr = SystemPerformance.AllTrades[tradeCountSeen++];
                double pnl = tr.ProfitCurrency;
                dailyPnL += pnl; netMoney += pnl;
                totalTrades++;
                if (pnl > 0) { wins++; sessionLosses = 0; }
                else sessionLosses++;
            }
        }

        // ---- drawing ------------------------------------------------------------------------------------------
        private void DrawFair()
        {
            int startAgo = CurrentBar - fairBar;
            Draw.Line(this, "FPRfair" + drawId, false, startAgo, fair, 0, fair,
                fairKind == "pre-news" ? Brushes.Orange : Brushes.DodgerBlue, DashStyleHelper.Dash, 2);
        }

        private void DrawStats()
        {
            string text = $"FairPriceReversionV1  |  {Profile}{(ReverseSignals ? "  |  REVERSED" : "")}  |  "
                + (EnableTrading ? (State != State.Realtime || tradingAllowed ? "TRADING" : "BLOCKED") : "OFF")
                + $"\nfair price: {(fairKind == "none" ? "-" : fair.ToString("F2") + " (" + fairKind + ")")}"
                + $"  |  session losses in a row {sessionLosses}/{MaxLosses}  |  today ${dailyPnL:F0}"
                + $"\nlast: {lastNote}"
                + $"\ntrades {totalTrades}, wins {wins}, net ${netMoney:F0}   (UNPROVEN -- sim only)";
            Draw.TextFixed(this, "FPRstats", text, TextPosition.TopLeft, Brushes.White,
                new SimpleFont("Arial", 11), Brushes.Transparent, Brushes.Black, 70);
        }

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Session Starts (HHMM, chart time)", Description = "Comma-separated session opens, US Eastern: 0930 New York, 1400 New York PM, 1800 Asia, 0300 London.", Order = 1, GroupName = "1. Fair Price")]
        public string SessionStarts { get; set; }

        [NinjaScriptProperty]
        [Range(10, 600)]
        [Display(Name = "Session Window (minutes)", Description = "Each session is traded for its first N minutes (video: 90).", Order = 2, GroupName = "1. Fair Price")]
        public int SessionWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Reversion", Description = "On a listed news day, the pre-news price is the fair price from the news time on.", Order = 3, GroupName = "1. Fair Price")]
        public bool NewsEnabled { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "News Dates (yyyy-MM-dd, comma-separated)", Description = "Days with scheduled red-folder news (CPI, PPI ...), e.g. 2026-07-14,2026-07-15. You must fill these in yourself.", Order = 4, GroupName = "1. Fair Price")]
        public string NewsDates { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "News Time (HHMM, chart time)", Order = 5, GroupName = "1. Fair Price")]
        public int NewsTime { get; set; }

        [NinjaScriptProperty]
        [Range(10, 1000)]
        [Display(Name = "News Window (minutes)", Description = "How long the pre-news price stays fair (150 = until 11:00 after an 08:30 release).", Order = 6, GroupName = "1. Fair Price")]
        public int NewsWindowMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Opening Continuation", Description = "Trade the opening candle's direction once per session.", Order = 1, GroupName = "2. Entries")]
        public bool UseContinuation { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require Bias", Description = "Continuation only if the opening candle agrees with the higher-timeframe bias (the opposite of the last Bias Hours' move).", Order = 2, GroupName = "2. Entries")]
        public bool RequireBias { get; set; }

        [NinjaScriptProperty]
        [Range(1, 48)]
        [Display(Name = "Bias Hours", Description = "Video: 6 to 12 hours.", Order = 3, GroupName = "2. Entries")]
        public int BiasHours { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Structure Bars", Description = "The opening candle must close beyond the high / low of this many previous candles.", Order = 4, GroupName = "2. Entries")]
        public int StructureBars { get; set; }

        [NinjaScriptProperty]
        [Range(4, 100)]
        [Display(Name = "Break of Structure Lookback (bars)", Description = "How far back to look for the swing low / high that gets broken.", Order = 5, GroupName = "2. Entries")]
        public int BosLookback { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 1000.0)]
        [Display(Name = "Continuation Target (points)", Description = "Video: 38.", Order = 6, GroupName = "2. Entries")]
        public double ContTarget { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 1000.0)]
        [Display(Name = "Continuation Stop (points)", Description = "Video: 25.", Order = 7, GroupName = "2. Entries")]
        public double ContStop { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 1000.0)]
        [Display(Name = "Big Opening Candle (points)", Description = "Above this range the continuation uses double target / stop with half the contracts (video: 25 -> 76 / 50).", Order = 8, GroupName = "2. Entries")]
        public double ContBigCandle { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Account Profile", Description = "Evaluation: static 38/25, displacement + break of structure. FundedConsistency: static, break of structure only. FundedNoConsistency: large targets, break of structure only.", Order = 1, GroupName = "3. Targets and Stops")]
        public FpProfile Profile { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 1000.0)]
        [Display(Name = "Eval Target (points)", Description = "Video: 38 (with 25 stop = -500 / +760 on 1 NQ).", Order = 2, GroupName = "3. Targets and Stops")]
        public double EvalTarget { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 1000.0)]
        [Display(Name = "Eval Stop (points)", Order = 3, GroupName = "3. Targets and Stops")]
        public double EvalStop { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Funded Targets (points, comma-separated)", Description = "FundedNoConsistency: the largest one that fits Min Fair Fraction of the points to fair price is used.", Order = 4, GroupName = "3. Targets and Stops")]
        public string FundedTargets { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 10.0)]
        [Display(Name = "Funded Reward:Risk", Description = "Stop = target / this (video: 1:3 up to 1:6 on funded accounts with no consistency rule).", Order = 5, GroupName = "3. Targets and Stops")]
        public double FundedRR { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 1000.0)]
        [Display(Name = "Min Stop (points)", Description = "Video: never less than 25 in the New York session.", Order = 6, GroupName = "3. Targets and Stops")]
        public double MinStopPoints { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 1.0)]
        [Display(Name = "Min Fair Fraction", Description = "Need at least this share of the target's points still available to fair price (video: 80 %, 75 % if greedy).", Order = 7, GroupName = "3. Targets and Stops")]
        public double MinFairFraction { get; set; }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "Contracts", Order = 8, GroupName = "3. Targets and Stops")]
        public int Contracts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Losses in a Row", Description = "Stop trading for the session after this many losses in a row (video: 3).", Order = 1, GroupName = "4. Safety")]
        public int MaxLosses { get; set; }

        [NinjaScriptProperty]
        [Range(0, double.MaxValue)]
        [Display(Name = "Daily Loss Limit ($, 0 = off)", Order = 2, GroupName = "4. Safety")]
        public double DailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Exit at Window End", Description = "Close any open trade when the session / news window ends.", Order = 3, GroupName = "4. Safety")]
        public bool ExitAtWindowEnd { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Reverse Signals", Description = "ON = flip every trade's direction (continuation and reversion). Stops / targets stay as set. UNTESTED.", Order = 4, GroupName = "4. Safety")]
        public bool ReverseSignals { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading", Description = "Off = no orders.", Order = 5, GroupName = "4. Safety")]
        public bool EnableTrading { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Allow Non-Sim Account", Description = "Keep OFF.", Order = 6, GroupName = "4. Safety")]
        public bool AllowNonSimAccount { get; set; }
        #endregion
    }
}
