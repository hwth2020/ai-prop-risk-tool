import type { Metadata } from "next";
import Link from "next/link";

export const metadata: Metadata = {
  title: "AI Trading Repo Research",
  description: "Notes on open-source AI trading and finance repos and how they fit NQ futures prop trading.",
};

type Fit = "Good" | "Partial" | "Poor";

type Repo = {
  name: string;
  url: string;
  what: string;
  license: string;
  stars: string;
  howToUse: string;
  futures: string;
  fit: Fit;
  verdict: string;
};

// Facts below come from each project's README / listings as read on 2026-10-18; star counts drift.
const repos: Repo[] = [
  {
    name: "OpenBB",
    url: "https://github.com/OpenBB-finance/OpenBB",
    what: "Open data platform for analysts, quants and AI agents; Python SDK, REST API and MCP servers.",
    license: "Apache-2.0",
    stars: "~74k",
    howToUse: "pip install openbb, then obb.equity.price.historical(...). Needs per-provider API keys for some data.",
    futures: "README does not list CME NQ/ES coverage - verify before relying on it.",
    fit: "Partial",
    verdict: "Useful for macro context (rates, dollar, yields) around NQ. Not a tick or footprint data source.",
  },
  {
    name: "TradingView MCP (community servers)",
    url: "https://glama.ai/mcp/servers/ulianbass/tradingview-mcp",
    what: "Unofficial MCP servers that let Claude read and control the TradingView Desktop app (draw levels, indicators, replay). Several forks trace to tradesdontlie/tradingview-mcp.",
    license: "Varies by fork - check each repo",
    stars: "n/a",
    howToUse: "Needs TradingView Desktop (paid subscription). Control goes over Chrome DevTools Protocol. Review the source first: it can drive an app on your machine.",
    futures: "Works on whatever symbols TradingView shows, including NQ.",
    fit: "Good",
    verdict: "Best fit for discretionary charting help. It does not place trades or feed your NinjaTrader strategies.",
  },
  {
    name: "Fincept Terminal",
    url: "https://github.com/Fincept-Corporation/FinceptTerminal",
    what: "C++/Qt desktop finance terminal with analytics, 37 AI agents, 100+ data connectors, paper trading and an AI quant lab.",
    license: "AGPL-3.0-or-later (strong copyleft)",
    stars: "~33k",
    howToUse: "Download a desktop build. Bring your own LLM and data keys. Paid tiers add cloud credits.",
    futures: "Not mentioned in the README.",
    fit: "Poor",
    verdict: "Broad research desk, but equity/crypto focused. Little reason to adopt it for NQ prop trading.",
  },
  {
    name: "daily_stock_analysis (ZhuLinsen)",
    url: "https://github.com/ZhuLinsen/daily_stock_analysis",
    what: "LLM-driven watchlist analysis: buy/hold/sell dashboard, scores, and push reports to Telegram, Discord, Slack or email.",
    license: "MIT",
    stars: "~42-56k (sources disagree)",
    howToUse: "Local, Docker or scheduled GitHub Actions. Needs at least one LLM key; data from AkShare, Tushare, YFinance.",
    futures: "A-shares, HK, US, Japan, Korea, Taiwan stocks - no futures.",
    fit: "Poor",
    verdict: "Stock-watchlist tool. Not relevant to a single-instrument futures strategy.",
  },
  {
    name: "TradingAgents",
    url: "https://github.com/TauricResearch/TradingAgents",
    what: "LangGraph multi-agent framework: fundamental, sentiment, news and technical analysts, researchers, trader and risk team debate a ticker.",
    license: "Apache-2.0",
    stars: "~112k",
    howToUse: "Python 3.11+, pip install, set an LLM key (Anthropic supported). CLI or TradingAgentsGraph().propagate().",
    futures: "Anything Yahoo Finance covers (stocks, crypto like BTC-USD). Not tick or order-flow data.",
    fit: "Partial",
    verdict: "Interesting for multi-day stock or crypto ideas. Its README says it is for research, not trading advice. An LLM debate is not an edge on a 1-minute NQ system.",
  },
  {
    name: "AI Hedge Fund (virattt)",
    url: "https://github.com/virattt/ai-hedge-fund",
    what: "Agents modelled on famous investors each give a signal and confidence per ticker; includes backtesting.",
    license: "MIT",
    stars: "~64k",
    howToUse: "pipx install aihf. Needs a Financial Datasets API key and an LLM key.",
    futures: "Not mentioned. Equity fundamentals oriented.",
    fit: "Poor",
    verdict: "Educational only, and the project says it does not make trades. Persona opinions are not validated signals.",
  },
  {
    name: "Alpaca MCP Server",
    url: "https://github.com/alpacahq/alpaca-mcp-server",
    what: "Official MCP server so an LLM client can call Alpaca's trading API: orders, positions, market data, watchlists.",
    license: "MIT",
    stars: "~1k",
    howToUse: "uvx alpaca-mcp-server with API keys in the MCP client's env block. Paper trading is the default.",
    futures: "Stocks, ETFs, crypto and options only - no futures.",
    fit: "Poor",
    verdict: "The only execution layer in the list, and it cannot trade NQ. Keep to paper and never expose the server publicly.",
  },
];

const fitStyle: Record<Fit, string> = {
  Good: "text-green-400 border-green-900",
  Partial: "text-yellow-400 border-yellow-900",
  Poor: "text-red-400 border-red-900",
};

export default function ResearchPage() {
  return (
    <main className="min-h-screen bg-black text-white p-6">
      <div className="max-w-5xl mx-auto">
        <Link href="/" className="text-sm text-zinc-400 hover:text-white">
          &larr; Risk calculator
        </Link>

        <h1 className="text-3xl font-bold mt-4">AI Trading Repo Research</h1>
        <p className="text-zinc-400 mt-1">
          Seven repos from a trading-AI video, checked against their READMEs and judged for NQ futures prop trading.
        </p>

        <div className="bg-zinc-950 border border-zinc-800 rounded-2xl p-5 mt-6 text-sm text-zinc-300">
          <div className="font-semibold text-white mb-1">Bottom line</div>
          Most of these target stocks and crypto. Only the TradingView MCP matches the NQ chart workflow, and it
          is a charting assistant, not an edge. Treat every agent&apos;s buy/sell output as an opinion until it has
          been backtested on your own rules. Nothing here connects to your NinjaTrader strategies.
        </div>

        <div className="mt-6 space-y-4">
          {repos.map((r) => (
            <section key={r.name} className="bg-zinc-950 border border-zinc-800 rounded-2xl p-5">
              <div className="flex flex-wrap items-center gap-3">
                <h2 className="text-xl font-semibold">
                  <a href={r.url} target="_blank" rel="noreferrer" className="hover:underline">
                    {r.name}
                  </a>
                </h2>
                <span className={`text-xs border rounded-full px-2 py-0.5 ${fitStyle[r.fit]}`}>
                  NQ fit: {r.fit}
                </span>
                <span className="text-xs text-zinc-500">{r.license} &middot; {r.stars} stars</span>
              </div>
              <p className="text-zinc-300 mt-2">{r.what}</p>
              <dl className="grid md:grid-cols-2 gap-x-6 gap-y-2 mt-3 text-sm">
                <div>
                  <dt className="text-zinc-500">How to use</dt>
                  <dd className="text-zinc-300">{r.howToUse}</dd>
                </div>
                <div>
                  <dt className="text-zinc-500">Futures / NQ</dt>
                  <dd className="text-zinc-300">{r.futures}</dd>
                </div>
              </dl>
              <p className="text-sm mt-3">
                <span className="text-zinc-500">Verdict: </span>
                {r.verdict}
              </p>
            </section>
          ))}
        </div>

        <p className="text-xs text-zinc-600 mt-6">
          Facts read from each project&apos;s page on 2026-10-18; star counts and licenses can change. Not financial advice.
        </p>
      </div>
    </main>
  );
}
