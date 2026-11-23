import { useEffect, useState } from 'react'
import type { ChangeEvent } from 'react'

type ManagedUser = { id: string; username: string; displayName: string; role: string; status: string; createdAtUtc: string }
type Props = { currentUserId: string; isSuperuser: boolean }

async function csrf() {
  const response = await fetch('/api/auth/csrf', { credentials: 'same-origin' })
  if (!response.ok) throw new Error(`Cannot verify session (${response.status})`)
  const data = await response.json() as { csrfToken: string }
  if (!data.csrfToken) throw new Error('No CSRF token returned')
  return data.csrfToken
}

export function IdentityAdministration({ currentUserId, isSuperuser }: Props) {
  const [users, setUsers] = useState<ManagedUser[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [revision, setRevision] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError('')
    void fetch(`/api/admin/users/?search=${encodeURIComponent(search.trim())}`, {
      credentials: 'same-origin', signal: controller.signal,
    })
      .then(async (r) => {
        if (!r.ok) throw new Error(r.status === 503
          ? 'Admin management service is not configured. Run the one-time Keycloak setup.'
          : `Failed to load users (HTTP ${r.status})`)
        return r.json() as Promise<ManagedUser[]>
      })
      .then((items) => { if (!controller.signal.aborted) setUsers(items) })
      .catch((e: unknown) => { if (!controller.signal.aborted) setError(String(e)) })
      .finally(() => { if (!controller.signal.aborted) setLoading(false) })
    return () => controller.abort()
  }, [revision, search])

  async function update(id: string, kind: 'grant' | 'revoke' | 'activate' | 'suspend') {
    const verb = kind === 'grant' ? 'grant ADMIN to' : kind === 'revoke' ? 'revoke ADMIN from' : kind === 'suspend' ? 'suspend' : 'reactivate'
    if (!window.confirm(`Are you sure you want to ${verb} this user?`)) return
    setBusy(id); setError(''); setNotice('')
    try {
      const roleChange = kind === 'grant' || kind === 'revoke'
      const endpoint = roleChange
        ? `/api/superuser/users/${encodeURIComponent(id)}/admin`
        : `/api/admin/users/${encodeURIComponent(id)}/status`
      const r = await fetch(endpoint, {
        method: roleChange ? (kind === 'grant' ? 'POST' : 'DELETE') : 'PUT',
        credentials: 'same-origin',
        headers: { 'X-CSRF-TOKEN': await csrf(), ...(roleChange ? {} : { 'Content-Type': 'application/json' }) },
        body: roleChange ? undefined : JSON.stringify({ status: kind === 'suspend' ? 'SUSPENDED' : 'ACTIVE' }),
      })
      if (!r.ok) {
        const body = await r.json().catch(() => ({})) as { error?: string }
        throw new Error(body.error ?? `Server refused change (HTTP ${r.status})`)
      }
      setNotice('Saved. Existing sessions of this user have been invalidated; they must sign in again.')
      setRevision(v => v + 1)
    } catch (e: unknown) { setError(String(e)) }
    finally { setBusy(null) }
  }

  return (
    <section className="modules-panel" aria-label="User administration">
      <div className="section-heading">
        <div><span className="eyebrow">IDENTITY & ACCESS</span><h2>Manage users</h2></div>
        <span className="muted">Keycloak roles · PostgreSQL user status</span>
      </div>
      <p className="muted">All registered users start as PLAYER. Only SUPERUSER can assign ADMIN. Administrators can suspend or reactivate ordinary player accounts.</p>
      <label className="account-select-field">
        <span>SEARCH USERS</span>
        <input type="search" value={search} placeholder="Username or display name" maxLength={100}
          onChange={(e: ChangeEvent<HTMLInputElement>) => setSearch(e.target.value)} />
      </label>
      {loading && <p role="status">Loading users…</p>}
      {error && <div className="alert alert-error" role="alert">{error}</div>}
      {notice && <div className="alert" role="status">{notice}</div>}
      {!loading && !error && users.length === 0 && <p className="muted">No users match this search.</p>}
      <div className="portfolio-table-scroll">
        <table className="portfolio-table">
          <thead><tr><th>Person</th><th>Role</th><th>Status</th><th>Actions</th></tr></thead>
          <tbody>{users.map((u) => (
            <tr key={u.id}>
              <td><strong>{u.displayName || u.username}</strong><div className="muted">@{u.username}</div></td>
              <td><strong>{u.role}</strong></td>
              <td>{u.status}</td>
              <td>
                {u.id !== currentUserId && u.role === 'PLAYER' && (
                  <button className="button button-secondary" type="button" disabled={busy === u.id}
                    onClick={() => void update(u.id, u.status === 'SUSPENDED' ? 'activate' : 'suspend')}>
                    {u.status === 'SUSPENDED' ? 'Reactivate' : 'Suspend'}
                  </button>
                )}
                {isSuperuser && u.id !== currentUserId && u.status === 'ACTIVE' && u.role === 'PLAYER' && (
                  <button className="button button-primary" type="button" disabled={busy === u.id}
                    onClick={() => void update(u.id, 'grant')}>Make ADMIN</button>
                )}
                {isSuperuser && u.id !== currentUserId && u.role === 'ADMIN' && (
                  <button className="button button-secondary" type="button" disabled={busy === u.id}
                    onClick={() => void update(u.id, 'revoke')}>Remove ADMIN</button>
                )}
                {(u.id === currentUserId || u.role === 'SUPERUSER') && <span className="muted">Protected</span>}
              </td>
            </tr>
          ))}</tbody>
        </table>
      </div>
    </section>
  )
}
