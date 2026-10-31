#region Using declarations
using System;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
#endregion

// GridStartTest -- does NOTHING: no orders, no drawing, no indicators. Same start settings as the grid strategies
// (unmanaged, StartBehavior.ImmediatelySubmit). Use it to find out whether "Unable to cancel out live orders" comes from
// the grid code or from the account / Playback setup: put it on the same chart, with the same account, and press play.
// It prints which account it is attached to and when it reaches real time.

namespace NinjaTrader.NinjaScript.Strategies
{
    public class GridStartTest : Strategy
    {
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "GridStartTest";
                Description = "Does nothing; used to test that a strategy can start on this chart / account.";
                Calculate   = Calculate.OnBarClose;
                IsUnmanaged = true;
                StartBehavior = StartBehavior.ImmediatelySubmit;
            }
            else if (State == State.DataLoaded)
            {
                Print("[GridStartTest] loaded. Account = " + (Account != null ? Account.Name : "none")
                    + ", instrument " + Instrument.FullName + ", StartBehavior " + StartBehavior);
            }
            else if (State == State.Realtime)
            {
                Print("[GridStartTest] REAL-TIME reached -- the strategy started fine.");
            }
        }

        protected override void OnBarUpdate() { }
    }
}
