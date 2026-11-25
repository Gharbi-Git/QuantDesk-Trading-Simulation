import './styles/identity-upgrade.css'
import './styles/quantdesk-luxury.css'
import './styles/quantdesk-hero.css'
import { useEffect, useState } from 'react'
import type { ChangeEvent } from 'react'
import { HubConnectionBuilder } from '@microsoft/signalr'
import { productName } from './config'
import { PortfolioInsights } from './PortfolioInsights'
import { IdentityAdministration } from './IdentityAdministration'
import { PlayerProfileEditor } from './PlayerProfileEditor'
import { roleLabel } from './roleLabels'
import type { PortfolioValuation, BalanceHistory, SettledTrade } from './PortfolioInsights'

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
  isPlayer: boolean
  isAdmin: boolean
  isSuperuser: boolean
  role: 'PLAYER' | 'ADMIN' | 'SUPERUSER' | 'NONE'
  displayName?: string | null
  status?: string
  pseudoPublic?: string | null
}

type TradingAccount = {
  id: string
  accountType: string
  currency: string
  cashBalance: number
  createdAtUtc?: string
}

type CashJournalRow = {
  id: string
  compteTradingId: string
  amount: number
  eventKey: string
  createdAtUtc: string
}

type SessionStatus = 'loading' | 'authenticated' | 'anonymous' | 'error'
type AccountStatus = 'idle' | 'loading' | 'ready' | 'error' | 'forbidden'
type View = 'overview' | 'portfolio' | 'profile' | 'modules' | 'administration'
type LiveStatus = 'Offline' | 'Connecting' | 'Connected' | 'Reconnecting' | 'Connection failed' | 'Access limited' | 'Administration mode'

const navigation: { id: View; label: string; symbol: string }[] = [
  { id: 'overview', label: 'Overview', symbol: '◫' },
  { id: 'portfolio', label: 'My portfolio', symbol: '▤' },
  { id: 'profile', label: 'My profile', symbol: '◉' },
  { id: 'modules', label: 'Modules', symbol: '▦' },
  { id: 'administration', label: 'Administration', symbol: '⚙' },
]

const workspaces = [
  { name: 'Market Data', description: 'Equities, quotes, and historical prices' },
  { name: 'Strategy Registry', description: 'Strategy versions and deployments' },
  { name: 'Paper Trading', description: 'Virtual orders and execution workflows' },
  { name: 'Portfolio', description: 'Accounts, positions, and balances' },
  { name: 'Arena', description: '1v1 strategy battles and synchronized market sessions' },
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

function readViewFromHash(): View {
  const raw = window.location.hash.replace(/^#/, '')
  return navigation.some((item) => item.id === raw) ? raw as View : 'overview'
}

function NavigationIcon({ view }: { view: View }) {
  const common = { width: 20, height: 20, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 1.75, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, 'aria-hidden': true as const }
  if (view === 'overview') return <svg {...common}><rect x="3" y="3" width="7" height="8" rx="1.5" /><rect x="14" y="3" width="7" height="5" rx="1.5" /><rect x="14" y="12" width="7" height="9" rx="1.5" /><rect x="3" y="15" width="7" height="6" rx="1.5" /></svg>
  if (view === 'portfolio') return <svg {...common}><path d="M3 18V5m0 13h18M6.5 14l4-4 3 2.5 5-7" /><path d="M15.5 5.5h3v3" /></svg>
  if (view === 'profile') return <svg {...common}><circle cx="12" cy="8" r="3.5" /><path d="M5 20c.2-4.2 3-6 7-6s6.8 1.8 7 6" /></svg>
  if (view === 'modules') return <svg {...common}><rect x="3" y="3" width="7" height="7" rx="1.3" /><rect x="14" y="3" width="7" height="7" rx="1.3" /><rect x="3" y="14" width="7" height="7" rx="1.3" /><rect x="14" y="14" width="7" height="7" rx="1.3" /></svg>
  return <svg {...common}><path d="M12 2.5 20 6v5.5c0 4.5-2.9 7.6-8 10-5.1-2.4-8-5.5-8-10V6z" /><path d="m9 12 2 2 4-4" /></svg>
}

export function App() {
  const [view, setView] = useState<View>(readViewFromHash)
  const [sidebarExpanded, setSidebarExpanded] = useState(true)
  const [showAdvancedProfile, setShowAdvancedProfile] = useState(false)
  const [editingProfile, setEditingProfile] = useState(false)
  useEffect(() => {
    const handle = () => setView(readViewFromHash())
    window.addEventListener('hashchange', handle)
    return () => window.removeEventListener('hashchange', handle)
  }, [])
  function navigate(next: View) {
    window.location.hash = next
    setView(next)
  }

  const [refreshKey, setRefreshKey] = useState(0)
  const [sessionStatus, setSessionStatus] = useState<SessionStatus>('loading')
  const [user, setUser] = useState<User | null>(null)
  const [sessionError, setSessionError] = useState('')
  const [suspendedNotice, setSuspendedNotice] = useState(
    () => new URLSearchParams(window.location.search).get('authNotice') === 'suspended',
  )
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
  const [selectedAccountId, setSelectedAccountId] = useState<string | null>(null)

  const [cashJournal, setCashJournal] = useState<CashJournalRow[]>([])
  const [journalStatus, setJournalStatus] = useState<AccountStatus>('idle')
  const [journalError, setJournalError] = useState('')
  const [valuation, setValuation] = useState<PortfolioValuation | null>(null)
  const [balanceHistory, setBalanceHistory] = useState<BalanceHistory | null>(null)
  const [recentTrades, setRecentTrades] = useState<SettledTrade[]>([])
  const [insightsStatus, setInsightsStatus] = useState<AccountStatus>('idle')
  const [insightsError, setInsightsError] = useState('')
  const [liveRetryKey, setLiveRetryKey] = useState(0)

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
          // A revoked session is still unauthenticated. Ask the API whether to show
          // a short-lived suspension notice instead of the generic guest message.
          try {
            const noticeResponse = await fetch('/api/auth/session-notice', {
              credentials: 'same-origin', signal: controller.signal,
            })
            if (noticeResponse.ok) {
              const notice = (await noticeResponse.json()) as { suspended?: boolean }
              if (!controller.signal.aborted && notice.suspended) setSuspendedNotice(true)
            }
          } catch {
            // The notice is informational; never restore access on fetch failure.
          }
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
          setSuspendedNotice(false)
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
    if (!user.isPlayer) {
      setAccounts([])
      setAccountStatus('forbidden')
      setAccountError(user?.isAdmin ? 'Admin and Session Organizer accounts use the Administration workspace; only Players own trading accounts.' : 'Your account needs the Player role. Please sign out and contact support if it persists.')
      return
    }

    const controller = new AbortController()
    setAccountStatus('loading')
    setAccountError('')

    void fetch('/api/accounts', { credentials: 'same-origin', signal: controller.signal })
      .then(async (response) => {
        if (response.status === 403) {
          throw new Error('Player permission denied (HTTP 403). Sign out and sign back in to refresh Keycloak roles.')
        }
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
          const message = String(error)
          if (message.includes('HTTP 403')) setAccounts([])
          setAccountError(message)
          setAccountStatus(message.includes('HTTP 403') ? 'forbidden' : 'error')
        }
      })

    return () => controller.abort()
  }, [sessionStatus, user?.quantDeskUserId, user?.isPlayer, refreshKey, accountsRevision])

  const personalAccountId = accounts.find((a) => a.id === selectedAccountId)?.id
    ?? accounts.find((a) => a.accountType === 'Personal')?.id
    ?? accounts[0]?.id ?? null

  useEffect(() => {
    if (sessionStatus !== 'authenticated' || !personalAccountId) {
      setCashJournal([])
      setJournalStatus('idle')
      return
    }

    const controller = new AbortController()
    setCashJournal([])
    setJournalStatus('loading')
    setJournalError('')

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

    void loadJournal()
    return () => controller.abort()
  }, [sessionStatus, personalAccountId, refreshKey, accountsRevision])

  useEffect(() => {
    if (sessionStatus !== 'authenticated' || !personalAccountId) {
      setValuation(null)
      setBalanceHistory(null)
      setRecentTrades([])
      setInsightsStatus('idle')
      setInsightsError('')
      return
    }
    const controller = new AbortController()
    setValuation(null)
    setBalanceHistory(null)
    setRecentTrades([])
    setInsightsError('')
    setInsightsStatus('loading')

    const load = async () => {
      const base = `/api/accounts/${encodeURIComponent(personalAccountId)}`
      try {
        const urls = [`${base}/valuation`, `${base}/balance-history`, `${base}/trades`]
        const responses = await Promise.all(urls.map((url) => fetch(url, {
          credentials: 'same-origin', signal: controller.signal,
        })))
        for (let i = 0; i < responses.length; i++) {
          if (!responses[i].ok) throw new Error(`${urls[i]}: HTTP ${responses[i].status}`)
        }
        const [summary, history, trades] = await Promise.all(responses.map((r) => r.json()))
        if (!Array.isArray(trades) || !Array.isArray(history.history) || !Array.isArray(summary.positions) || summary.accountId !== personalAccountId ||
          !Number.isFinite(summary.availableCash) || !Number.isFinite(summary.reservedCash) ||
          summary.positions.some((p: PortfolioValuation['positions'][number]) => !Number.isFinite(p.availableShares) || !Number.isFinite(p.reservedShares))) {
          throw new Error('Unexpected portfolio analytics API response')
        }
        if (!controller.signal.aborted) {
          setValuation(summary as PortfolioValuation)
          setBalanceHistory(history as BalanceHistory)
          setRecentTrades(trades as SettledTrade[])
          setInsightsStatus('ready')
        }
      } catch (error: unknown) {
        if (!controller.signal.aborted) {
          setInsightsStatus('error')
          setInsightsError(String(error))
        }
      }
    }
    void load()
    return () => controller.abort()
  }, [sessionStatus, personalAccountId, refreshKey, accountsRevision])

  useEffect(() => {
    if (sessionStatus !== 'authenticated') {
      setLiveStatus('Offline')
      setLatestNotification(null)
      return
    }
    if (!user?.isPlayer) {
      // Not a network outage: the hub explicitly requires PLAYER authorization.
      setLiveStatus(user?.isAdmin ? 'Administration mode' : 'Access limited')
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
  }, [sessionStatus, user?.quantDeskUserId, user?.isPlayer, liveRetryKey])

  function refresh() {
    setActionError('')
    setAccountMessage('')
    setRefreshKey((value) => value + 1)
  }

  async function createPersonalAccount() {
    if (sessionStatus !== 'authenticated' || !user?.isPlayer || creatingAccount) return
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

  const personalAccount = accounts.find((account) => account.id === personalAccountId) ?? null
  const currentValuation = valuation?.accountId === personalAccountId ? valuation : null
  const positionCurrency = personalAccount?.currency ?? 'USD'
  const isAuthenticated = sessionStatus === 'authenticated'
  const accountPanel = (
    <section className="identity-panel" aria-label="Selected trading account">
      <div>
        <span className="eyebrow">YOUR ACCOUNT</span>
        <h2>Trading account</h2>
        <p>Each account has its own cash ledger and stock positions. Review your funds, holdings and account performance in one place.</p>
        {isAuthenticated && accounts.length > 0 && (
          <label className="account-select-field">
            <span>ACTIVE ACCOUNT</span>
            <select aria-label="Active trading account" value={personalAccountId ?? ''}
              onChange={(event: ChangeEvent<HTMLSelectElement>) => setSelectedAccountId(event.target.value)}>
              {accounts.map((account) => <option key={account.id} value={account.id}>
                {account.accountType} · {account.currency} · {account.id.slice(0, 8)}
              </option>)}
            </select>
          </label>
        )}
        {accountMessage && <p role="status">{accountMessage}</p>}
      </div>
      <div className="identity-details">
        {!isAuthenticated ? (
          <p>Sign in to view or create your virtual trading account.</p>
        ) : accountStatus === 'loading' ? (
          <p role="status">Loading your account...</p>
        ) : accountStatus === 'forbidden' ? (
          <div className="alert alert-error" role="alert">
            <strong>{user?.isAdmin ? 'Administration account' : 'Trading access unavailable'}</strong>
            <p>{accountError}</p>
          </div>
        ) : accountStatus === 'error' ? (
          <div className="alert alert-error" role="alert">Unable to load account: {accountError}</div>
        ) : personalAccount ? (
          <>
            <div><span>Cash balance</span><strong>{formatMoney(personalAccount.cashBalance, personalAccount.currency)}</strong></div>
            <div><span>Account type</span><strong>{personalAccount.accountType}</strong></div>
            <div><span>Currency</span><strong>{personalAccount.currency}</strong></div>
            <div><span>Created</span><strong>{formatDate(personalAccount.createdAtUtc)}</strong></div>
            <details className="account-reference"><summary>Account reference</summary><code>{personalAccount.id}</code></details>
          </>
        ) : (
          <>
            <p>You don't have a trading account yet. Create a personal account to start with virtual funding.</p>
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
    <div className={`app-shell luxury-shell ${sidebarExpanded ? 'sidebar-expanded' : 'sidebar-collapsed'}`}>
      <aside className="sidebar" aria-label="Main sidebar">
        <div className="sidebar-brand-row">
          <div className="brand">
            <div className="brand-icon"><img className="qd-brand-globe" src="/quantdesk-globe-gold.png" alt="" draggable={false} /></div>
            <div className="brand-copy"><strong>{productName}</strong><small>THE TRADING STUDIO</small></div>
          </div>
          <button type="button" className="sidebar-toggle" aria-label={sidebarExpanded ? 'Collapse sidebar' : 'Expand sidebar'}
            aria-expanded={sidebarExpanded} title={sidebarExpanded ? 'Collapse sidebar' : 'Expand sidebar'}
            onClick={() => setSidebarExpanded(value => !value)}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
              {sidebarExpanded ? <path d="m14.5 6-6 6 6 6" strokeLinecap="round" strokeLinejoin="round" /> : <path d="m9.5 6 6 6-6 6" strokeLinecap="round" strokeLinejoin="round" />}
            </svg>
          </button>
        </div>
        <div className="sidebar-section-label">WORKSPACE</div>
        <nav className="navigation" aria-label="Main navigation">
          {navigation.filter(item => item.id !== 'administration' || user?.isAdmin).map((item) => (
            <button key={item.id} type="button"
              className={view === item.id ? 'nav-item active' : 'nav-item'}
                title={sidebarExpanded ? undefined : item.label}
              aria-current={view === item.id ? 'page' : undefined}
              onClick={() => navigate(item.id)}>
              <span className="nav-symbol"><NavigationIcon view={item.id} /></span><span className="nav-label">{item.label}</span>
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="environment-label"><span className="status-dot" />Simulation mode</div>
          <small>Virtual funds only · No live trading</small>
        </div>
      </aside>

      <div className="main-area">
        <header className="topbar">
          <div>
            <span className="eyebrow">YOUR PRIVATE TRADING SPACE</span>
            <h1>{navigation.find((item) => item.id === view)?.label}</h1>
          </div>
          <div className="topbar-actions">
            {isAuthenticated && (
              <div className="connection-control" aria-live="polite">
                <span className={liveStatus === 'Connected' ? 'connection-dot on' : 'connection-dot'} />
                <span>{liveStatus === 'Connected' ? 'Live connection' : liveStatus}</span>
                {(liveStatus === 'Connection failed' || liveStatus === 'Offline') && user?.isPlayer && (
                  <button type="button" className="connection-retry" onClick={() => setLiveRetryKey((v) => v + 1)}>Retry</button>
                )}
              </div>
            )}
            <button type="button" className="button button-secondary" onClick={refresh}>Refresh</button>
            {isAuthenticated ? (
              <div className="user-menu">
                <div className="avatar" aria-hidden="true">{(user?.username || 'U')[0].toUpperCase()}</div>
                <div className="user-details"><strong>{user?.displayName || user?.username || 'Player'}</strong><small>{user?.role ? roleLabel(user.role) : 'Signed in'} · {user?.status || 'ACTIVE'}</small></div>
                <button type="button" className="button button-secondary"
                  onClick={() => void logout()} disabled={signingOut}>
                  {signingOut ? 'Signing out...' : 'Sign out'}
                </button>
              </div>
            ) : sessionStatus === 'loading' ? (
              <span className="muted">Checking session...</span>
            ) : suspendedNotice ? (
              <div className="auth-actions">
                <button type="button" className="button button-secondary"
                  onClick={() => void logout()} disabled={signingOut}>
                  {signingOut ? 'Signing out...' : 'Sign out / Switch account'}
                </button>
              </div>
            ) : (
              <div className="auth-actions">
                <a className="button button-secondary" href="/auth/login">Sign in</a>
                <a className="button button-primary" href="/auth/register">Create account</a>
              </div>
            )}
          </div>
        </header>

        <main className="content">
          {actionError && <div className="alert alert-error" role="alert">{actionError}</div>}
          {sessionStatus === 'error' && (
            <div className="alert alert-error" role="alert">Authentication API unavailable: {sessionError}</div>
          )}
          {sessionStatus === 'loading' && <div className="alert" role="status">Checking your session...</div>}
          {sessionStatus === 'anonymous' && suspendedNotice && (
            <div className="alert alert-error" role="alert">
              <strong>Account suspended.</strong> Your QuantDesk account has been suspended by an administrator.
              Contact an administrator for reactivation. To use a different account, select Sign out / Switch account.
              Keycloak may ask you to confirm sign-out before you can sign in again.
            </div>
          )}
          {sessionStatus === 'anonymous' && !suspendedNotice && (
            <div className="alert">You're browsing as a guest. Sign in to access your virtual accounts and portfolio.</div>
          )}

          {view === 'overview' && (
            <>
              <section className="qd-hero" aria-label="Welcome to QuantDesk">
                <div className="qd-hero__content">
                  <span className="eyebrow qd-hero__eyebrow">INTELLIGENT TRADING, ELEVATED</span>
                  <h2 className="qd-hero__title">{isAuthenticated ? `Welcome back, ${user?.pseudoPublic || user?.displayName || user?.username || 'Player'}` : 'Welcome to QuantDesk'}</h2>
                  <p className="qd-hero__description">Build your strategies, explore market opportunities, and follow your virtual portfolio with confidence.</p>
                  <button type="button" className="button button-primary qd-hero__button"
                    onClick={() => navigate(user?.isAdmin ? 'administration' : 'portfolio')}>{user?.isAdmin ? 'Open administration' : 'View my portfolio'}</button>
                </div>
                <div className="qd-hero__art" aria-hidden="true">
                  <img className="qd-hero__globe" src="/quantdesk-globe-gold.png" alt="" draggable={false} />
                </div>
              </section>
              {accountPanel}
              {isAuthenticated && personalAccount && (
                <PortfolioInsights currency={personalAccount.currency} cash={personalAccount.cashBalance}
                  valuation={currentValuation} history={balanceHistory} trades={recentTrades}
                  loading={insightsStatus === 'loading'} error={insightsError} compact />
              )}
              <div className="section-heading"><div><span className="eyebrow">PLATFORM</span><h2>Explore QuantDesk</h2></div><span className="muted">Explore what is coming next</span></div>
              <div className="workspace-grid">
                {workspaces.map((workspace) => (
                  <article className="workspace-card" key={workspace.name}>
                    <span className="planned-label">COMING SOON</span>
                    <h3>{workspace.name}</h3><p>{workspace.description}</p>
                  </article>
                ))}
              </div>
            </>
          )}

          {view === 'portfolio' && (
            <>
              {accountPanel}
              {personalAccount && (
                <PortfolioInsights currency={personalAccount.currency} cash={personalAccount.cashBalance}
                  valuation={currentValuation} history={balanceHistory} trades={recentTrades}
                  loading={insightsStatus === 'loading'} error={insightsError} />
              )}
              <section className="modules-panel portfolio-positions" aria-label="Portfolio holdings">
                <div className="section-heading">
                  <div><span className="eyebrow">YOUR HOLDINGS</span><h2>Portfolio positions</h2></div>
                  <span className="muted">Your equity holdings</span>
                </div>
                <p className="muted">Explore the investments you own, their average purchase cost and how they have performed. Estimated values use recent trading activity, not live market quotes.</p>
                {!isAuthenticated && <p className="muted">Sign in to see your holdings.</p>}
                {isAuthenticated && user?.isPlayer && accountStatus === 'ready' && !personalAccount && <p className="muted">Create a personal trading account to activate your portfolio.</p>}
                {isAuthenticated && accountStatus === 'forbidden' && <p className="muted">Portfolio access is unavailable until the Player role is active in this session.</p>}
                {personalAccount && insightsStatus === 'loading' && <p role="status">Loading your positions...</p>}
                {personalAccount && insightsStatus === 'error' && <div className="alert alert-error" role="alert">Unable to load positions: {insightsError}</div>}
                {personalAccount && currentValuation && currentValuation.positions.length === 0 && (
                  <div className="portfolio-empty">
                    <strong>No stock positions yet</strong>
                    <p>Your holdings will appear here after your first completed stock trade.</p>
                  </div>
                )}
                {personalAccount && currentValuation && currentValuation.positions.length > 0 && (
                  <div className="portfolio-table-scroll">
                    <table className="portfolio-table">
                      <thead><tr><th scope="col">Instrument</th><th scope="col">Owned shares</th><th scope="col">Available</th><th scope="col">Reserved</th><th scope="col">Avg. acquisition</th><th scope="col">Cost basis</th><th scope="col">Last trade mark</th><th scope="col">Unrealized P&L</th><th scope="col">Realized P&L</th></tr></thead>
                      <tbody>
                        {currentValuation.positions.map((position) => {
                          const marked = position
                          return <tr key={position.instrumentId}>
                            <td><strong className="instrument-symbol">{position.instrumentId}</strong>{position.quantity === 0 && <small className="closed-position">Closed position</small>}</td>
                            <td>{position.quantity.toLocaleString('en-US')}</td>
                            <td>{position.availableShares.toLocaleString('en-US')}</td>
                            <td>{position.reservedShares.toLocaleString('en-US')}</td>
                            <td>{formatMoney(position.averageAcquisitionCost, positionCurrency)}</td>
                            <td>{formatMoney(position.costBasis, positionCurrency)}</td>
                            <td>{marked?.markPrice == null ? '—' : formatMoney(marked.markPrice, positionCurrency)}</td>
                            <td className={marked && (marked.unrealizedPnl ?? 0) < 0 ? 'money-negative' : marked && (marked.unrealizedPnl ?? 0) > 0 ? 'money-positive' : ''}>{marked?.unrealizedPnl == null ? '—' : formatMoney(marked.unrealizedPnl, positionCurrency)}</td>
                            <td className={position.realizedPnl < 0 ? 'money-negative' : position.realizedPnl > 0 ? 'money-positive' : ''}>{formatMoney(position.realizedPnl, positionCurrency)}</td>
                          </tr>
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </section>
              <section className="modules-panel portfolio-journal" aria-label="Cash transaction journal">
                <div className="section-heading"><div><span className="eyebrow">YOUR ACTIVITY</span><h2>Transaction history</h2></div><span className="muted">Deposits and completed trades</span></div>
                {accountStatus === 'ready' && !personalAccount && <p className="muted">Your transaction journal becomes available once you have an account.</p>}
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
                            <td className="journal-event">{entry.eventKey.startsWith('initial-funding:') ? 'Initial virtual funding' : entry.eventKey.startsWith('stock-fill:') ? (entry.amount < 0 ? 'Stock trade · buy settlement' : 'Stock trade · sell settlement') : entry.eventKey}</td>
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
              <section className="profile-hero" aria-label="Your profile">
                <div className="profile-hero-avatar" aria-hidden="true">{(user?.displayName || user?.username || 'Q').slice(0, 1).toUpperCase()}</div>
                <div className="profile-hero-content">
                  <span className="eyebrow">YOUR IDENTITY</span>
                  <h2>{user?.pseudoPublic || user?.displayName || user?.username || 'Your profile'}</h2>
                  <p>Your space to manage your account and the name you share with other players.</p>
                  {isAuthenticated && <div className="profile-badges">
                    <span className="profile-badge gold">{user?.role ? roleLabel(user.role) : 'Member'}</span>
                    <span className={`profile-badge ${user?.status === 'SUSPENDED' ? 'inactive' : 'active'}`}>
                      {user?.status === 'SUSPENDED' ? 'Suspended' : 'Active account'}
                    </span>
                  </div>}
                  {!isAuthenticated && <a className="button button-primary" href="/auth/login">Sign in to view your profile</a>}
                </div>
              </section>
              {isAuthenticated && <section className="modules-panel profile-summary-card" aria-label="Personal information">
                <div className="profile-summary-header">
                  <div>
                    <span className="eyebrow">ACCOUNT DETAILS</span>
                    <h2>Personal information</h2>
                    <p>Your membership and public identity, in one place.</p>
                  </div>
                  {user?.isPlayer && <button type="button"
                    className="button button-secondary profile-edit-toggle"
                    aria-expanded={editingProfile} aria-controls={editingProfile ? 'profile-nickname-editor' : undefined}
                    onClick={() => setEditingProfile(value => !value)}>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"
                      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                      <path d="m15 5 4 4M4 20l4.4-1 11-11a2.1 2.1 0 0 0-3-3l-11 11L4 20Z" />
                    </svg>
                    {editingProfile ? 'Close editor' : 'Edit nickname'}
                  </button>}
                </div>
                <div className="profile-information-grid">
                  <div className="profile-info-item"><span>Username</span><strong>{user?.username || 'Not available'}</strong></div>
                  <div className="profile-info-item"><span>Email address</span><strong>{user?.email || 'Not provided'}</strong></div>
                  <div className="profile-info-item"><span>Membership</span><strong>{user?.role ? roleLabel(user.role) : 'Not available'}</strong></div>
                  <div className="profile-info-item"><span>Public nickname</span><strong>{user?.pseudoPublic || 'Not set yet'}</strong></div>
                </div>
                {user?.isPlayer && editingProfile && <div className="profile-inline-editor" id="profile-nickname-editor">
                  <h3>Update your public nickname</h3>
                  <p>Your nickname is visible to other players. You can change it here.</p>
                  <PlayerProfileEditor pseudoPublic={user.pseudoPublic ?? null}
                    onSaved={() => { setEditingProfile(false); refresh() }} />
                </div>}
                <button type="button" className="profile-advanced-toggle" aria-expanded={showAdvancedProfile}
                  onClick={() => setShowAdvancedProfile(value => !value)}>
                  {showAdvancedProfile ? 'Hide technical details' : 'Show account details'}
                </button>
                {showAdvancedProfile && <div className="profile-information-grid profile-advanced-details">
                  <div className="profile-info-item"><span>Account ID</span><code>{user?.quantDeskUserId || 'Not available'}</code></div>
                  <div className="profile-info-item"><span>Connection</span><strong>{liveStatus}</strong></div>
                </div>}
              </section>}
              {latestNotification && isAuthenticated && <section className="modules-panel profile-notification-card" aria-label="Latest update">
                <div className="section-heading"><div><span className="eyebrow">ACTIVITY</span><h2>Latest update</h2></div></div>
                <p role="status">{latestNotification}</p>
              </section>}
            </>
          )}

          {view === 'administration' && (
            user?.isAdmin && user.quantDeskUserId
              ? <IdentityAdministration currentUserId={user.quantDeskUserId} isSuperuser={user.isSuperuser} />
              : <div className="alert alert-error">You need Admin or Session Organizer permissions to access administration.</div>
          )}

          {view === 'modules' && (
            <section className="modules-panel">
              <div className="section-heading">
                <div><span className="eyebrow">PLATFORM FEATURES</span><h2>Explore modules</h2></div>
                <span className="muted">{modules.length} modules</span>
              </div>
              <p className="muted">Explore the tools and workspaces available across QuantDesk. Some features are still in development.</p>
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
