import { useEffect, useState } from 'react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { productName } from './config'

type Module = {
  code: string
  name: string
  owner: string
}

type User = {
  authenticated: boolean
  username: string | null
  email: string | null
  quantDeskUserId: string | null
}

type TradingAccount = {
  id: string
  accountType: string
  currency: string
  cashBalance: number
  createdAtUtc?: string
}

type StockPosition = {
  id: string
  compteTradingId: string
  instrumentId: string
  quantity: number
  costBasis: number
  averageAcquisitionCost: number
  realizedPnl: number
}

type CashJournalRow = {
  id: string
  compteTradingId: string
  amount: number
  eventKey: string
  createdAtUtc: string
}

type SessionStatus = 'loading' | 'authenticated' | 'anonymous' | 'error'
type AccountStatus = 'idle' | 'loading' | 'ready' | 'error'
type View = 'overview' | 'portfolio' | 'profile' | 'modules'
type LiveStatus = 'Offline' | 'Connecting' | 'Connected' | 'Reconnecting' | 'Connection failed'

const navigation: { id: View; label: string; symbol: string }[] = [
  { id: 'overview', label: 'Overview', symbol: '◫' },
  { id: 'portfolio', label: 'My portfolio', symbol: '▤' },
  { id: 'profile', label: 'My profile', symbol: '◉' },
  { id: 'modules', label: 'Modules', symbol: '▦' },
]

const workspaces = [
  { name: 'Market Data', description: 'Equities, quotes, and historical prices' },
  { name: 'Strategy Registry', description: 'Strategy versions and deployments' },
  { name: 'Paper Trading', description: 'Virtual orders and execution workflows' },
  { name: 'Portfolio', description: 'Accounts, positions, and balances' },
  { name: 'Arena', description: 'Simulation sessions and challenges' },
  { name: 'Administration', description: 'Authorized platform management' },
]

function formatMoney(amount: number, currency: string): string {
  try {
    return new Intl.NumberFormat('en-US', {
      style: 'currency',
      currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    }).format(amount)
  } catch {
    return `${amount.toFixed(2)} ${currency}`
  }
}

function formatDate(value?: string): string {
  if (!value) return 'Not available'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'Not available' : date.toLocaleDateString()
}

export function App() {
  const [view, setView] = useState<View>('overview')
  const [refreshKey, setRefreshKey] = useState(0)
  const [sessionStatus, setSessionStatus] = useState<SessionStatus>('loading')
  const [user, setUser] = useState<User | null>(null)
  const [sessionError, setSessionError] = useState('')
  const [actionError, setActionError] = useState('')
  const [signingOut, setSigningOut] = useState(false)

  const [modules, setModules] = useState<Module[]>([])
  const [modulesError, setModulesError] = useState('')
  const [modulesLoading, setModulesLoading] = useState(true)

  const [accounts, setAccounts] = useState<TradingAccount[]>([])
  const [accountStatus, setAccountStatus] = useState<AccountStatus>('idle')
  const [accountError, setAccountError] = useState('')
  const [accountMessage, setAccountMessage] = useState('')
  const [creatingAccount, setCreatingAccount] = useState(false)
  const [accountsRevision, setAccountsRevision] = useState(0)

  const [positions, setPositions] = useState<StockPosition[]>([])
  const [positionsStatus, setPositionsStatus] = useState<AccountStatus>('idle')
  const [positionsError, setPositionsError] = useState('')
  const [cashJournal, setCashJournal] = useState<CashJournalRow[]>([])
  const [journalStatus, setJournalStatus] = useState<AccountStatus>('idle')
  const [journalError, setJournalError] = useState('')

  const [liveStatus, setLiveStatus] = useState<LiveStatus>('Offline')
  const [latestNotification, setLatestNotification] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    setSessionStatus('loading')
    setSessionError('')

    void fetch('/api/me', {
      credentials: 'same-origin',
      signal: controller.signal,
    })
      .then(async (response) => {
        if (response.status === 401) {
          if (!controller.signal.aborted) {
            setUser(null)
            setSessionStatus('anonymous')
          }
          return
        }
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        const identity = (await response.json()) as User
        if (!identity.authenticated || !identity.quantDeskUserId) {
          throw new Error('The authenticated identity is incomplete.')
        }
        if (!controller.signal.aborted) {
          setUser(identity)
          setSessionStatus('authenticated')
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setUser(null)
          setSessionError(String(error))
          setSessionStatus('error')
        }
      })

    return () => controller.abort()
  }, [refreshKey])

  useEffect(() => {
    const controller = new AbortController()
    setModulesLoading(true)
    setModulesError('')

    void fetch('/api/meta/modules', { credentials: 'same-origin', signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        return (await response.json()) as Module[]
      })
      .then((items) => {
        if (!controller.signal.aborted) setModules(items)
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setModulesError(String(error))
      })
      .finally(() => {
        if (!controller.signal.aborted) setModulesLoading(false)
      })

    return () => controller.abort()
  }, [refreshKey])

  useEffect(() => {
    if (sessionStatus !== 'authenticated' || !user?.quantDeskUserId) {
      setAccounts([])
      setAccountStatus('idle')
      return
    }

    const controller = new AbortController()
    setAccountStatus('loading')
    setAccountError('')

    void fetch('/api/accounts', { credentials: 'same-origin', signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        const data: unknown = await response.json()
        if (!Array.isArray(data)) throw new Error('Invalid accounts response')
        return data as TradingAccount[]
      })
      .then((items) => {
        if (!controller.signal.aborted) {
          setAccounts(items)
          setAccountStatus('ready')
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setAccountError(String(error))
          setAccountStatus('error')
        }
      })

    return () => controller.abort()
  }, [sessionStatus, user?.quantDeskUserId, refreshKey, accountsRevision])

  const personalAccountId = accounts.find((account) => account.accountType === 'Personal')?.id ?? null

  useEffect(() => {
    if (sessionStatus !== 'authenticated' || !personalAccountId) {
      setPositions([])
      setPositionsStatus('idle')
      setCashJournal([])
      setJournalStatus('idle')
      return
    }

    const controller = new AbortController()
    setPositions([])
    setPositionsStatus('loading')
    setPositionsError('')
    setCashJournal([])
    setJournalStatus('loading')
    setJournalError('')

    const loadPositions = async () => {
      try {
        const response = await fetch(`/api/accounts/${encodeURIComponent(personalAccountId)}/positions`, {
          credentials: 'same-origin', signal: controller.signal,
        })
        if (!response.ok) throw new Error(`Positions API: HTTP ${response.status}`)
        const body: unknown = await response.json()
        if (!Array.isArray(body)) throw new Error('Invalid positions response')
        if (!controller.signal.aborted) {
          setPositions(body as StockPosition[])
          setPositionsStatus('ready')
        }
      } catch (error: unknown) {
        if (!controller.signal.aborted) {
          setPositionsError(String(error))
          setPositionsStatus('error')
        }
      }
    }

    const loadJournal = async () => {
      try {
        const response = await fetch(`/api/accounts/${encodeURIComponent(personalAccountId)}/journal`, {
          credentials: 'same-origin', signal: controller.signal,
        })
        if (!response.ok) throw new Error(`Cash journal API: HTTP ${response.status}`)
        const body: unknown = await response.json()
        if (!Array.isArray(body)) throw new Error('Invalid cash journal response')
        if (!controller.signal.aborted) {
          setCashJournal(body as CashJournalRow[])
          setJournalStatus('ready')
        }
      } catch (error: unknown) {
        if (!controller.signal.aborted) {
          setJournalError(String(error))
          setJournalStatus('error')
        }
      }
    }

    void loadPositions()
    void loadJournal()
    return () => controller.abort()
  }, [sessionStatus, personalAccountId, refreshKey, accountsRevision])

  useEffect(() => {
    if (sessionStatus !== 'authenticated') {
      setLiveStatus('Offline')
      setLatestNotification(null)
      return
    }

    let active = true
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/player', { withCredentials: true })
      .withAutomaticReconnect()
      .build()

    connection.on('PlayerNotification', (message: string) => {
      if (active) {
        setLatestNotification(message)
        setAccountsRevision((value) => value + 1)
      }
    })
    connection.onreconnecting(() => {
      if (active) setLiveStatus('Reconnecting')
    })
    connection.onreconnected(() => {
      if (active) {
        setLiveStatus('Connected')
        // Refresh server state: messages may have been missed while offline.
        setAccountsRevision((value) => value + 1)
      }
    })
    connection.onclose(() => {
      if (active) setLiveStatus('Offline')
    })

    setLiveStatus('Connecting')
    void connection.start()
      .then(async () => {
        if (!active) {
          await connection.stop()
          return
        }
        const reply = await connection.invoke<string>('Ping')
        if (active) setLiveStatus(reply === 'pong' ? 'Connected' : 'Connection failed')
      })
      .catch((error: unknown) => {
        if (active) {
          console.error('SignalR connection failed:', error)
          setLiveStatus('Connection failed')
        }
      })

    return () => {
      active = false
      void connection.stop().catch(() => {})
    }
  }, [sessionStatus, user?.quantDeskUserId])

  function refresh() {
    setActionError('')
    setAccountMessage('')
    setRefreshKey((value) => value + 1)
  }

  async function createPersonalAccount() {
    if (sessionStatus !== 'authenticated' || creatingAccount) return
    setCreatingAccount(true)
    setActionError('')
    setAccountMessage('')
    try {
      const csrfResponse = await fetch('/api/auth/csrf', { credentials: 'same-origin' })
      if (!csrfResponse.ok) throw new Error(`CSRF request failed: HTTP ${csrfResponse.status}`)
      const csrfData = (await csrfResponse.json()) as { csrfToken?: string }
      if (!csrfData.csrfToken) throw new Error('Missing CSRF token')

      const response = await fetch('/api/accounts/personal', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': csrfData.csrfToken },
      })
      if (!response.ok) throw new Error(`Account creation failed: HTTP ${response.status}`)

      setAccountMessage(response.status === 201
        ? 'Your virtual personal account was created successfully.'
        : 'Your personal account is ready.')
      setAccountsRevision((value) => value + 1)
    } catch (error: unknown) {
      setActionError(String(error))
    } finally {
      setCreatingAccount(false)
    }
  }

  async function logout() {
    setSigningOut(true)
    setActionError('')
    try {
      const response = await fetch('/api/auth/csrf', { credentials: 'same-origin' })
      if (!response.ok) throw new Error(`CSRF request failed: HTTP ${response.status}`)
      const data = (await response.json()) as { csrfToken?: string }
      if (!data.csrfToken) throw new Error('CSRF token is missing')

      // A real form navigation allows the OIDC logout redirect to complete.
      const form = document.createElement('form')
      form.method = 'POST'
      form.action = '/auth/logout'
      form.hidden = true
      const token = document.createElement('input')
      token.type = 'hidden'
      token.name = '__RequestVerificationToken'
      token.value = data.csrfToken
      form.appendChild(token)
      document.body.appendChild(form)
      form.submit()
    } catch (error: unknown) {
      setActionError(String(error))
      setSigningOut(false)
    }
  }

  const personalAccount = accounts.find((account) => account.accountType === 'Personal') ?? null
  const positionsCostBasis = positions.reduce((sum, position) => sum + position.costBasis, 0)
  const totalRealizedPnl = positions.reduce((sum, position) => sum + position.realizedPnl, 0)
  const positionCurrency = personalAccount?.currency ?? 'USD'
  const isAuthenticated = sessionStatus === 'authenticated'
  const accountPanel = (
    <section className="identity-panel" aria-label="Personal trading account">
      <div>
        <span className="eyebrow">VIRTUAL TRADING</span>
        <h2>Personal trading account</h2>
        <p>Balances are loaded from QuantDesk's PostgreSQL-backed API, not sample values.</p>
        {accountMessage && <p role="status">{accountMessage}</p>}
      </div>
      <div className="identity-details">
        {!isAuthenticated ? (
          <p>Sign in to view or create your virtual trading account.</p>
        ) : accountStatus === 'loading' ? (
          <p role="status">Loading your account...</p>
        ) : accountStatus === 'error' ? (
          <div className="alert alert-error" role="alert">Unable to load account: {accountError}</div>
        ) : personalAccount ? (
          <>
            <div><span>Cash balance</span><strong>{formatMoney(personalAccount.cashBalance, personalAccount.currency)}</strong></div>
            <div><span>Account type</span><strong>{personalAccount.accountType}</strong></div>
            <div><span>Currency</span><strong>{personalAccount.currency}</strong></div>
            <div><span>Created</span><strong>{formatDate(personalAccount.createdAtUtc)}</strong></div>
            <div><span>Account ID</span><code>{personalAccount.id}</code></div>
          </>
        ) : (
          <>
            <p>You don't have a personal trading account yet.</p>
            <button type="button" className="button button-primary"
              onClick={() => void createPersonalAccount()} disabled={creatingAccount}>
              {creatingAccount ? 'Creating account...' : 'Create virtual trading account'}
            </button>
          </>
        )}
      </div>
    </section>
  )

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-icon">Q</div>
          <div><strong>{productName}</strong><small>Trading Simulation</small></div>
        </div>
        <div className="sidebar-section-label">WORKSPACE</div>
        <nav className="navigation" aria-label="Main navigation">
          {navigation.map((item) => (
            <button key={item.id} type="button"
              className={view === item.id ? 'nav-item active' : 'nav-item'}
              aria-current={view === item.id ? 'page' : undefined}
              onClick={() => setView(item.id)}>
              <span aria-hidden="true">{item.symbol}</span>{item.label}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="environment-label"><span className="status-dot" />Development environment</div>
          <small>Virtual funds only · No live trading</small>
        </div>
      </aside>

      <div className="main-area">
        <header className="topbar">
          <div>
            <span className="eyebrow">QUANTDESK PLATFORM</span>
            <h1>{navigation.find((item) => item.id === view)?.label}</h1>
          </div>
          <div className="topbar-actions">
            <button type="button" className="button button-secondary" onClick={refresh}>Refresh</button>
            {isAuthenticated ? (
              <div className="user-menu">
                <div className="avatar" aria-hidden="true">{(user?.username || 'U')[0].toUpperCase()}</div>
                <div className="user-details"><strong>{user?.username || 'Player'}</strong><small>Signed in</small></div>
                <button type="button" className="button button-secondary"
                  onClick={() => void logout()} disabled={signingOut}>
                  {signingOut ? 'Signing out...' : 'Sign out'}
                </button>
              </div>
            ) : sessionStatus === 'loading' ? (
              <span className="muted">Checking session...</span>
            ) : (
              <a className="button button-primary" href="/auth/login">Sign in</a>
            )}
          </div>
        </header>

        <main className="content">
          {actionError && <div className="alert alert-error" role="alert">{actionError}</div>}
          {sessionStatus === 'error' && (
            <div className="alert alert-error" role="alert">Authentication API unavailable: {sessionError}</div>
          )}
          {sessionStatus === 'loading' && <div className="alert" role="status">Checking your session...</div>}
          {sessionStatus === 'anonymous' && (
            <div className="alert">You're browsing as a guest. Sign in to use your personal account.</div>
          )}

          {view === 'overview' && (
            <>
              <section className="welcome-card">
                <div>
                  <span className="eyebrow">TRADING SIMULATION WORKSPACE</span>
                  <h2>{isAuthenticated ? `Welcome back, ${user?.username || 'Player'}` : 'Welcome to QuantDesk'}</h2>
                  <p>Manage your virtual trading account and follow the platform's modules as they become available.</p>
                  <button type="button" className="button button-primary"
                    onClick={() => setView('portfolio')}>View my portfolio</button>
                </div>
                <div className="welcome-symbol" aria-hidden="true">Q</div>
              </section>
              {accountPanel}
              <div className="section-heading"><div><span className="eyebrow">PLATFORM</span><h2>Explore QuantDesk</h2></div><span className="muted">Module roadmap</span></div>
              <div className="workspace-grid">
                {workspaces.map((workspace) => (
                  <article className="workspace-card" key={workspace.name}>
                    <span className="planned-label">IN DEVELOPMENT</span>
                    <h3>{workspace.name}</h3><p>{workspace.description}</p>
                  </article>
                ))}
              </div>
            </>
          )}

          {view === 'portfolio' && (
            <>
              {accountPanel}
              <section className="modules-panel portfolio-positions" aria-label="Portfolio holdings">
                <div className="section-heading">
                  <div><span className="eyebrow">PORTFOLIO / STOCK HOLDINGS</span><h2>Positions & realized performance</h2></div>
                  <span className="muted">Database-backed · long-only</span>
                </div>
                <p className="muted">Values below are sourced from your own portfolio. Cost basis and realized P&L are from executed fills; live valuation and unrealized P&L require market-price integration.</p>
                {personalAccount && positionsStatus === 'ready' && (
                  <div className="portfolio-kpis">
                    <div className="portfolio-kpi"><span>Stock instruments</span><strong>{positions.filter((item) => item.quantity > 0).length}</strong><small>Open holdings</small></div>
                    <div className="portfolio-kpi"><span>Total stock cost basis</span><strong>{formatMoney(positionsCostBasis, positionCurrency)}</strong><small>Historical acquisition cost</small></div>
                    <div className="portfolio-kpi"><span>Realized stock P&L</span><strong className={totalRealizedPnl < 0 ? 'money-negative' : totalRealizedPnl > 0 ? 'money-positive' : ''}>{formatMoney(totalRealizedPnl, positionCurrency)}</strong><small>Before fees</small></div>
                  </div>
                )}
                {!isAuthenticated && <p className="muted">Sign in to see your holdings.</p>}
                {isAuthenticated && !personalAccount && <p className="muted">Create a personal trading account to activate your portfolio.</p>}
                {personalAccount && positionsStatus === 'loading' && <p role="status">Loading positions from PostgreSQL...</p>}
                {personalAccount && positionsStatus === 'error' && <div className="alert alert-error" role="alert">Unable to load positions: {positionsError}</div>}
                {personalAccount && positionsStatus === 'ready' && positions.length === 0 && (
                  <div className="portfolio-empty">
                    <strong>No stock positions yet</strong>
                    <p>Once a validated order is matched and settled, your holdings will appear here automatically. Your virtual cash balance remains unchanged until settlement is integrated.</p>
                  </div>
                )}
                {personalAccount && positionsStatus === 'ready' && positions.length > 0 && (
                  <div className="portfolio-table-scroll">
                    <table className="portfolio-table">
                      <thead><tr><th scope="col">Instrument</th><th scope="col">Shares</th><th scope="col">Avg. acquisition</th><th scope="col">Cost basis</th><th scope="col">Realized P&L</th></tr></thead>
                      <tbody>
                        {positions.map((position) => (
                          <tr key={position.id}>
                            <td><strong className="instrument-symbol">{position.instrumentId}</strong>{position.quantity === 0 && <small className="closed-position">Closed position</small>}</td>
                            <td>{position.quantity.toLocaleString('en-US')}</td>
                            <td>{formatMoney(position.averageAcquisitionCost, positionCurrency)}</td>
                            <td>{formatMoney(position.costBasis, positionCurrency)}</td>
                            <td className={position.realizedPnl < 0 ? 'money-negative' : position.realizedPnl > 0 ? 'money-positive' : ''}>{formatMoney(position.realizedPnl, positionCurrency)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </section>
              <section className="modules-panel portfolio-journal" aria-label="Cash transaction journal">
                <div className="section-heading"><div><span className="eyebrow">AUDIT TRAIL</span><h2>Cash journal</h2></div><span className="muted">Immutable funding records</span></div>
                {!personalAccount && <p className="muted">Your transaction journal becomes available once you have an account.</p>}
                {personalAccount && journalStatus === 'loading' && <p role="status">Loading cash entries...</p>}
                {personalAccount && journalStatus === 'error' && <div className="alert alert-error" role="alert">Unable to load journal: {journalError}</div>}
                {personalAccount && journalStatus === 'ready' && cashJournal.length === 0 && <p className="muted">No cash entries found.</p>}
                {personalAccount && journalStatus === 'ready' && cashJournal.length > 0 && (
                  <div className="portfolio-table-scroll">
                    <table className="portfolio-table">
                      <thead><tr><th scope="col">Date</th><th scope="col">Event</th><th scope="col">Amount</th></tr></thead>
                      <tbody>
                        {cashJournal.map((entry) => (
                          <tr key={entry.id}>
                            <td>{formatDate(entry.createdAtUtc)}</td>
                            <td className="journal-event">{entry.eventKey.startsWith('initial-funding:') ? 'Initial virtual funding' : entry.eventKey}</td>
                            <td className={entry.amount < 0 ? 'money-negative' : 'money-positive'}>{formatMoney(entry.amount, positionCurrency)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </section>
            </>
          )}

          {view === 'profile' && (
            <>
              <section className="identity-panel">
                <div>
                  <span className="eyebrow">PLAYER PROFILE</span>
                  <h2>{user?.username || 'Your QuantDesk identity'}</h2>
                  <p>Your profile is derived from the authenticated Keycloak session. The account balance remains server-controlled.</p>
                  {!isAuthenticated && <a className="button button-primary" href="/auth/login">Sign in to view profile</a>}
                </div>
                <div className="identity-details">
                  <div><span>Session</span><strong>{sessionStatus}</strong></div>
                  <div><span>Username</span><strong>{user?.username || 'Not signed in'}</strong></div>
                  <div><span>Email</span><strong>{user?.email || 'Not provided'}</strong></div>
                  <div><span>Internal user ID</span><code>{user?.quantDeskUserId || '—'}</code></div>
                  <div><span>Real-time connection</span><strong>{liveStatus}</strong></div>
                </div>
              </section>
              {latestNotification && isAuthenticated && (
                <section className="identity-panel" aria-label="Most recent notification">
                  <div><span className="eyebrow">NOTIFICATIONS</span><h2>Latest update</h2></div>
                  <div className="identity-details"><p role="status">{latestNotification}</p></div>
                </section>
              )}
              {isAuthenticated && accountPanel}
            </>
          )}

          {view === 'modules' && (
            <section className="modules-panel">
              <div className="section-heading">
                <div><span className="eyebrow">PLATFORM REGISTRY</span><h2>Registered modules</h2></div>
                <span className="muted">{modules.length} modules</span>
              </div>
              <p className="muted">This registry is loaded from the API. Registration does not imply that a module is ready for trading.</p>
              {modulesLoading && <p role="status">Loading modules...</p>}
              {modulesError && <div className="alert alert-error" role="alert">{modulesError}</div>}
              {!modulesLoading && !modulesError && modules.length === 0 && <p>No registered modules yet.</p>}
              <div className="module-list">
                {modules.map((module) => (
                  <div className="module-row" key={module.code}>
                    <span className="module-code">{module.code}</span>
                    <strong>{module.name}</strong>
                    <span className="module-owner">{module.owner}</span>
                  </div>
                ))}
              </div>
            </section>
          )}
        </main>
      </div>
    </div>
  )
}
