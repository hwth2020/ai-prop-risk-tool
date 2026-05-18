"use client";

import { useState } from "react";

export default function Home() {
  const [contracts, setContracts] = useState(1);
  const [stopLoss, setStopLoss] = useState(10);
  const [drawdown, setDrawdown] = useState(1500);
  const [profitTarget, setProfitTarget] = useState(3000);
  const [winRate, setWinRate] = useState(55);
  const [rr, setRR] = useState(1.5);
  const [trades, setTrades] = useState(30);

  const pointValue = 20;

  const riskPerTrade = contracts * stopLoss * pointValue;

  const lossesUntilFailure =
    riskPerTrade > 0 ? drawdown / riskPerTrade : 0;

  const safeRisk = drawdown * 0.02;

  const recommendedContracts =
    stopLoss > 0 ? safeRisk / (stopLoss * pointValue) : 0;

  const w = winRate / 100;

  const ev = (w * rr) - (1 - w);

  const expectedProfit = ev * riskPerTrade * trades;

  const passProbability =
    profitTarget > 0 ? expectedProfit / profitTarget : 0;

  function score() {
    if (passProbability < 0.5) return { text: "Low", color: "text-red-400" };
    if (passProbability < 1) return { text: "Moderate", color: "text-yellow-400" };
    return { text: "High", color: "text-green-400" };
  }

  const scoreData = score();

  const Card = ({ title, value, color = "text-white" }: any) => (
    <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-5">
      <div className="text-zinc-400 text-sm">{title}</div>
      <div className={`text-2xl font-bold mt-2 ${color}`}>{value}</div>
    </div>
  );

  const Input = ({ label, value, setValue }: any) => (
    <div className="mb-3">
      <label className="text-sm text-zinc-400">{label}</label>
      <input
        type="number"
        value={value}
        onChange={(e) => setValue(Number(e.target.value))}
        className="w-full mt-1 p-2 rounded-lg bg-black border border-zinc-700 text-white"
      />
    </div>
  );

  return (
    <main className="min-h-screen bg-black text-white p-6">
      <div className="max-w-6xl mx-auto">

        {/* HEADER */}
        <div className="mb-8">
          <h1 className="text-3xl font-bold">
            AI Prop Firm Risk Calculator
          </h1>
          <p className="text-zinc-400 mt-1">
            Analyze your prop firm survival probability instantly
          </p>
        </div>

        <div className="grid md:grid-cols-2 gap-6">

          {/* INPUTS */}
          <div className="bg-zinc-950 border border-zinc-800 p-6 rounded-2xl">
            <h2 className="text-xl font-semibold mb-4">Inputs</h2>

            <Input label="Contracts" value={contracts} setValue={setContracts} />
            <Input label="Stop Loss Points" value={stopLoss} setValue={setStopLoss} />
            <Input label="Max Drawdown ($)" value={drawdown} setValue={setDrawdown} />
            <Input label="Profit Target ($)" value={profitTarget} setValue={setProfitTarget} />
            <Input label="Win Rate (%)" value={winRate} setValue={setWinRate} />
            <Input label="Reward:Risk Ratio" value={rr} setValue={setRR} />
            <Input label="Trades" value={trades} setValue={setTrades} />
          </div>

          {/* OUTPUTS */}
          <div className="space-y-4">

            <Card
              title="Risk Per Trade"
              value={`$${riskPerTrade.toFixed(2)}`}
              color="text-red-400"
            />

            <Card
              title="Losses Until Failure"
              value={lossesUntilFailure.toFixed(1)}
              color="text-yellow-400"
            />

            <Card
              title="Recommended Contracts"
              value={recommendedContracts.toFixed(2)}
              color="text-blue-400"
            />

            <Card
              title="Pass Probability"
              value={scoreData.text}
              color={scoreData.color}
            />

            <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-5">
              <h3 className="text-lg font-semibold mb-2">AI Insight</h3>
              <p className="text-zinc-400">
                {riskPerTrade > drawdown * 0.1
                  ? "High risk per trade. Likely to fail evaluation under volatility."
                  : "Risk profile is within reasonable prop firm limits."}
              </p>
            </div>

          </div>
        </div>

        {/* CTA */}
        <div className="mt-10 bg-zinc-950 border border-zinc-800 rounded-2xl p-6 text-center">
          <h2 className="text-xl font-bold mb-2">
            Start a Prop Firm Challenge
          </h2>
          <p className="text-zinc-400 mb-4">
            Compare funded trader programs and choose your evaluation.
          </p>

          <a
            href="YOUR_AFFILIATE_LINK"
            className="inline-block bg-green-500 hover:bg-green-400 text-black font-bold px-6 py-3 rounded-xl"
          >
            Get Funded
          </a>
        </div>

      </div>
    </main>
  );
}