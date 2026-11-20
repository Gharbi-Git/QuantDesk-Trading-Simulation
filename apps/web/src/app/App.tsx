
import { useEffect, useState } from 'react'
import { productName } from './config'
import { HubConnectionBuilder } from '@microsoft/signalr'
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

type SessionStatus = 'loading' | 'authenticated' | 'anonymous' | 'error'
type View = 'overview' | 'modules'

const workspaces = [
  { name: 'Market Data', description: 'Equities and market information' },
  { name: 'Strategy Registry', description: 'Trading strategies and versions' },
  { name: 'Paper Trading', description: 'Virtual trading and executions' },
  { name: 'Portfolio', description: 'Positions, balances, and performance' },
  { name: 'Arena', description: 'Trading challenges and matches' },
  { name: 'Administration', description: 'Platform administration tools' },
]

export function App() {
  const [sessionStatus, setSessionStatus] = useState<SessionStatus>('loading')
  const [user, setUser] = useState<User | null>(null)
  const [modules, setModules] = useState<Module[]>([])
  const [modulesError, setModulesError] = useState('')
  const [sessionError, setSessionError] = useState('')
  const [actionError, setActionError] = useState('')
  const [signingOut, setSigningOut] = useState(false)
  const [view, setView] = useState<View>('overview')
  const [refreshKey, setRefreshKey] = useState(0)  
  const [liveStatus, setLiveStatus] = useState('Offline')
  const [latestNotification, setLatestNotification] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()

    fetch('/api/me', {
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

        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`)
        }

        const identity = (await response.json()) as User

        if (!controller.signal.aborted) {
          setUser(identity)
          setSessionStatus('authenticated')
        }
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) return
        setSessionError(String(error))
        setSessionStatus('error')
      })

    fetch('/api/meta/modules', {
      signal: controller.signal,
    })
      .then(async (response) => {
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`)
        }

        return (await response.json()) as Module[]
      })
      .then((items) => {
        if (!controller.signal.aborted) {
          setModules(items)
          setModulesError('')
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) {
          setModulesError(String(error))
        }
      })

    return () => controller.abort()
  }, [refreshKey])
  
  
useEffect(() => {
  if (sessionStatus !== 'authenticated') {
    setLiveStatus('Offline')
    return
  }

  let active = true

  const connection = new HubConnectionBuilder()
    .withUrl('/hubs/player', { withCredentials: true })
    .withAutomaticReconnect()
    .build()

  connection.on('PlayerNotification', (message: string) => {
    if (active) setLatestNotification(message)
  })

  connection.onreconnecting(() => {
    if (active) setLiveStatus('Reconnecting')
  })

  connection.onreconnected(() => {
    if (active) setLiveStatus('Connected')
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

      if (active) {
        setLiveStatus(reply === 'pong' ? 'Connected' : 'Unexpected response')
      }
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
}, [sessionStatus])


  function refresh() {
    setSessionStatus('loading')
    setSessionError('')
    setModulesError('')
    setRefreshKey((current) => current + 1)
  }

  async function logout() {
    setActionError('')
    setSigningOut(true)

    try {
      const response = await fetch('/api/auth/csrf', {
        credentials: 'same-origin',
      })

      if (!response.ok) {
        throw new Error(`Cannot get CSRF token: HTTP ${response.status}`)
      }

      const data = (await response.json()) as { csrfToken: string }

      if (!data.csrfToken) {
        throw new Error('CSRF token is missing')
      }

      const form = document.createElement('form')
      form.method = 'POST'
      form.action = '/auth/logout'
      form.style.display = 'none'

      const field = document.createElement('input')
      field.type = 'hidden'
      field.name = '__RequestVerificationToken'
      field.value = data.csrfToken

      form.appendChild(field)
      document.body.appendChild(form)
      form.submit()
    } catch (error: unknown) {
      setActionError(String(error))
      setSigningOut(false)
    }
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-icon">Q</div>
          <div>
            <strong>{productName}</strong>
            <small>Trading Simulation</small>
          </div>
        </div>

        <div className="sidebar-section-label">WORKSPACE</div>

        <nav className="navigation" aria-label="Main navigation">
          <button
            className={view === 'overview' ? 'nav-item active' : 'nav-item'}
            onClick={() => setView('overview')}
            type="button"
          >
            <span>◫</span> Overview
          </button>

          <button
            className={view === 'modules' ? 'nav-item active' : 'nav-item'}
            onClick={() => setView('modules')}
            type="button"
          >
            <span>▦</span> Modules
          </button>
        </nav>

        <div className="sidebar-bottom">
          <div className="environment-label">
            <span className="status-dot" />
            Development environment
          </div>
          <small>Virtual funds only</small>
        </div>
      </aside>

      <div className="main-area">
        <header className="topbar">
          <div>
            <span className="eyebrow">QUANTDESK PLATFORM</span>
            <h1>{view === 'overview' ? 'Overview' : 'Modules'}</h1>
          </div>

          <div className="topbar-actions">
            <button
              type="button"
              className="button button-secondary"
              onClick={refresh}
            >
              Refresh
            </button>

            {sessionStatus === 'authenticated' ? (
              <div className="user-menu">
                <div className="avatar">
                  {(user?.username || 'U').charAt(0).toUpperCase()}
                </div>

                <div className="user-details">
                  <strong>{user?.username || 'Player'}</strong>
                  <small>Signed in</small>
                </div>

                <button
                  type="button"
                  className="button button-secondary"
                  onClick={() => void logout()}
                  disabled={signingOut}
                >
                  {signingOut ? 'Signing out...' : 'Sign out'}
                </button>
              </div>
            ) : (
              <a className="button button-primary" href="/auth/login">
                Sign in
              </a>
            )}
          </div>
        </header>

        <main className="content">
          {actionError && (
            <div className="alert alert-error" role="alert">{actionError}</div>
          )}

          {sessionStatus === 'error' && (
            <div className="alert alert-error" role="alert">
              Unable to reach the authentication API. {sessionError}
            </div>
          )}

          {sessionStatus === 'loading' && (
            <div className="alert" role="status">
              Checking your QuantDesk session...
            </div>
          )}

          {sessionStatus === 'anonymous' && (
            <div className="alert">
              You're browsing as a guest. Sign in to access your player session.
            </div>
          )}

          {view === 'overview' ? (
            <>
              <section className="welcome-card">
                <div>
                  <span className="eyebrow">TRADING SIMULATION WORKSPACE</span>
                  <h2>
                    {sessionStatus === 'authenticated'
                      ? `Welcome back, ${user?.username || 'Player'}`
                      : 'Welcome to QuantDesk'}
                  </h2>
                  <p>
                    Explore platform modules and manage your simulation workspace.
                    Trading features will become available as their modules are implemented.
                  </p>
                  <button
                    type="button"
                    className="button button-primary"
                    onClick={() => setView('modules')}
                  >
                    Explore modules
                  </button>
                </div>
                <div className="welcome-symbol" aria-hidden="true">Q</div>
              </section>

              <div className="section-heading">
                <div>
                  <span className="eyebrow">YOUR PLATFORM</span>
                  <h2>Workspace modules</h2>
                </div>
                <span className="muted">Development roadmap</span>
              </div>

              <div className="workspace-grid">
                {workspaces.map((workspace) => (
                  <article className="workspace-card" key={workspace.name}>
                    <span className="planned-label">PLANNED</span>
                    <h3>{workspace.name}</h3>
                    <p>{workspace.description}</p>
                  </article>
                ))}
              </div>

              <section className="identity-panel">
                <div>
                  <span className="eyebrow">AUTHENTICATION</span>
                  <h2>Player session</h2>
                  <p>
                    Your session information comes directly from the QuantDesk API.
                  </p>
                </div>

                <div className="identity-details">
                  <div>
                    <span>Status</span>
                    <strong>{sessionStatus}</strong>
                  </div>
                  <div>
                    <span>Username</span>
                    <strong>{user?.username || 'Not signed in'}</strong>
                  </div>
                  <div>
                    <span>Internal user ID</span>
                    <code>{user?.quantDeskUserId || '—'}</code>
                  </div>
                  <div>
  <span>Real-time connection</span>
  <strong>{liveStatus}</strong>
</div>

{latestNotification && (
  <div>
    <span>Latest notification</span>
    <strong>{latestNotification}</strong>
  </div>
)}
                </div>
              </section>
            </>
          ) : (
            <section className="modules-panel">
              <div className="section-heading">
                <div>
                  <span className="eyebrow">PLATFORM REGISTRY</span>
                  <h2>Registered modules</h2>
                </div>
                <span className="muted">{modules.length} modules</span>
              </div>

              <p className="muted">
                This list is loaded from the backend module catalogue.
                Registration does not mean that trading functionality is complete.
              </p>

              {modulesError && (
                <div className="alert alert-error" role="alert">
                  Failed to load modules: {modulesError}
                </div>
              )}

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
