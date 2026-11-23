/** Read-only portfolio metrics. All figures arrive from owner-protected API endpoints. */
export type MarkedHolding = {
  instrumentId: string
  quantity: number
  costBasis: number
  averageAcquisitionCost: number
  realizedPnl: number
  markPrice: number | null
  markAsOfUtc: string | null
  marketValue: number | null
  unrealizedPnl: number | null
}

export type PortfolioValuation = {
  accountId: string
  currency: string
  asOfUtc: string
  markPolicy: string
  markDisclosure: string
  cashBalance: number
  totalCostBasis: number
  realizedPnl: number
  marketValue: number | null
  nav: number | null
  unrealizedPnl: number | null
  missingMarkInstruments: string[]
  positions: MarkedHolding[]
}

export type BalanceHistory = {
  accountId: string
  currency: string
  currentCashBalance: number
  journalBalance: number
  reconciles: boolean
  history: {
    id: string
    amount: number
    eventKey: string
    createdAtUtc: string
    balanceAfter: number
  }[]
}

export type SettledTrade = {
  executionId: string
  instrumentId: string
  side: 'BUY' | 'SELL'
  quantity: number
  executionPrice: number
  notional: number
  settledAtUtc: string
}

type Props = {
  currency: string
  cash: number
  valuation: PortfolioValuation | null
  history: BalanceHistory | null
  trades: SettledTrade[]
  loading: boolean
  error: string
  compact?: boolean
}

function money(value: number, currency: string) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency,
    minimumFractionDigits: 2, maximumFractionDigits: 2 }).format(value)
}

function displayDate(value: string) {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString(undefined, {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  })
}

function CashBalanceChart({ history, currency }: { history: BalanceHistory; currency: string }) {
  const points = history.history
  if (points.length < 2) {
    return (
      <div className="insights-chart-empty">
        <div className="insights-empty-icon" aria-hidden="true">⌁</div>
        <strong>Your journey starts here</strong>
        <p>Your starting balance is ready. Your chart will grow as you make trades.</p>
      </div>
    )
  }
  const values = points.map((point) => point.balanceAfter)
  const min = Math.min(...values)
  const max = Math.max(...values)
  const span = Math.max(max - min, 1)
  const width = 720
  const height = 210
  const padx = 18
  const pady = 22
  const coord = points.map((point, index) => ({
    x: padx + (index / (points.length - 1)) * (width - padx * 2),
    y: height - pady - ((point.balanceAfter - min) / span) * (height - pady * 2),
  }))
  const path = coord.map((p, index) => `${index === 0 ? 'M' : 'L'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')
  const area = `${path} L${coord[coord.length - 1].x.toFixed(1)},${height} L${coord[0].x.toFixed(1)},${height} Z`
  return (
    <>
      <svg className="insights-line-chart" viewBox={`0 0 ${width} ${height}`}
        role="img" aria-label={`Cash balance over time: ${points.length} movements. Latest balance ${money(values[values.length - 1], currency)}.`}>
        <defs>
          <linearGradient id="qdCashFill" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#58e5b4" stopOpacity=".24" />
            <stop offset="100%" stopColor="#58e5b4" stopOpacity="0" />
          </linearGradient>
        </defs>
        {[.18, .5, .82].map((ratio) =>
          <line key={ratio} x1="0" y1={height * ratio} x2={width} y2={height * ratio} className="insights-grid-line" />)}
        <path d={area} fill="url(#qdCashFill)" />
        <path d={path} fill="none" stroke="#77e8ba" strokeWidth="3.5" strokeLinejoin="round" strokeLinecap="round" />
        <circle cx={coord[coord.length - 1].x} cy={coord[coord.length - 1].y} r="5" fill="#d0ffe7" stroke="#25aa81" strokeWidth="3" />
      </svg>
      <div className="insights-chart-axis">
        <span>{displayDate(points[0].createdAtUtc)}</span>
        <span>{displayDate(points[points.length - 1].createdAtUtc)}</span>
      </div>
    </>
  )
}

export function PortfolioInsights({ currency, cash, valuation, history, trades, loading, error, compact = false }: Props) {
  const ready = valuation !== null
  const incompleteMarks = Boolean(valuation && valuation.missingMarkInstruments.length > 0)
  return (
    <section className="insights-dashboard" aria-label="Financial overview">
      <div className="insights-header">
        <div><span className="eyebrow">PORTFOLIO AT A GLANCE</span><h2>Financial overview</h2></div>
        <span className="insights-source">Your portfolio at a glance</span>
      </div>
      {error && <div className="alert alert-error" role="alert">Some portfolio details are temporarily unavailable: {error}</div>}
      {loading && <p className="muted" role="status">Loading valuation, trade history and cash movements…</p>}
      <div className="insights-kpis">
        <article className="insights-kpi"><span className="insights-kpi-label">Available cash</span><strong>{money(cash, currency)}</strong><small>Virtual funds · from your account</small></article>
        <article className="insights-kpi"><span className="insights-kpi-label">Estimated portfolio value</span><strong>{ready && valuation.nav !== null ? money(valuation.nav, currency) : 'Unavailable'}</strong><small>{incompleteMarks ? 'Current prices are not available for every holding' : 'Cash and estimated holdings'}</small></article>
        <article className="insights-kpi"><span className="insights-kpi-label">Unrealized gain / loss</span><strong className={ready && (valuation.unrealizedPnl ?? 0) > 0 ? 'money-positive' : ready && (valuation.unrealizedPnl ?? 0) < 0 ? 'money-negative' : ''}>{ready && valuation.unrealizedPnl !== null ? money(valuation.unrealizedPnl, currency) : 'Unavailable'}</strong><small>Open holdings · only if all are marked</small></article>
        <article className="insights-kpi"><span className="insights-kpi-label">Realized gain / loss</span><strong className={ready && valuation.realizedPnl > 0 ? 'money-positive' : ready && valuation.realizedPnl < 0 ? 'money-negative' : ''}>{ready ? money(valuation.realizedPnl, currency) : '—'}</strong><small>Completed trades · before fees</small></article>
      </div>
      {valuation && <div className="insights-disclosure"><details className="valuation-details"><summary>How are portfolio values estimated?</summary><p>{valuation.markDisclosure}</p></details></div>}
      {incompleteMarks && <p className="insights-missing" role="status">Recent prices are unavailable for: {valuation?.missingMarkInstruments.join(', ')}. An estimated portfolio value cannot be shown yet.</p>}
      {!compact && (
        <div className="insights-details-grid">
          <article className="insights-detail-card">
            <div className="insights-card-title"><div><span className="eyebrow">CASH FLOW</span><h3>Balance history</h3></div><span className="muted">{history?.history.length ?? 0} events</span></div>
            {history ? <CashBalanceChart history={history} currency={currency} /> : <p className="muted">{loading ? 'Loading recorded movements…' : 'Cash history unavailable.'}</p>}
            {history && <div className={history.reconciles ? 'insights-reconcile passed' : 'insights-reconcile failed'}>
              {history.reconciles ? '✓ Your cash balance is up to date' : '⚠ Your cash balance needs attention'}
            </div>}
          </article>
          <article className="insights-detail-card">
            <div className="insights-card-title"><div><span className="eyebrow">TRADING ACTIVITY</span><h3>Recent trades</h3></div><span className="muted">Latest activity</span></div>
            {trades.length ? <div className="insights-trade-scroll"><table className="portfolio-table insights-trade-table">
              <thead><tr><th>Instrument</th><th>Side</th><th>Qty</th><th>Price</th></tr></thead>
              <tbody>{trades.slice(0, 8).map((trade) => <tr key={trade.executionId}>
                <td><strong>{trade.instrumentId}</strong><small>{displayDate(trade.settledAtUtc)}</small></td>
                <td><span className={trade.side === 'BUY' ? 'insights-side buy' : 'insights-side sell'}>{trade.side}</span></td>
                <td>{trade.quantity}</td><td>{money(trade.executionPrice, currency)}</td>
              </tr>)}</tbody>
            </table></div> : <div className="insights-chart-empty trade-empty"><div className="insights-empty-icon" aria-hidden="true">↔</div><strong>No trades yet</strong><p>Your completed trades will appear here.</p></div>}
          </article>
        </div>
      )}
    </section>
  )
}
