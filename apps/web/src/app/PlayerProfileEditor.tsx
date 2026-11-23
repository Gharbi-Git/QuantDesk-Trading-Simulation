import { useEffect, useState } from 'react'
import type { FormEvent, ChangeEvent } from 'react'

export function PlayerProfileEditor({ pseudoPublic, onSaved }: { pseudoPublic: string | null; onSaved: () => void }) {
  const [value, setValue] = useState(pseudoPublic ?? '')
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState('')
  useEffect(() => { setValue(pseudoPublic ?? '') }, [pseudoPublic])

  async function submit(e: FormEvent) {
    e.preventDefault(); setBusy(true); setMessage('')
    try {
      const cs = await fetch('/api/auth/csrf', { credentials: 'same-origin' })
      if (!cs.ok) throw new Error(`Session unavailable (${cs.status})`)
      const token = (await cs.json() as {csrfToken: string}).csrfToken
      const response = await fetch('/api/me/profile', {
        method: 'PUT', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
        body: JSON.stringify({ pseudoPublic: value.trim() }),
      })
      if (!response.ok) {
        const payload = await response.json().catch(() => ({})) as { error?: string }
        throw new Error(payload.error ?? `Unable to update profile (${response.status})`)
      }
      setMessage('Public player name updated.')
      onSaved()
    } catch (error: unknown) { setMessage(String(error)) }
    finally { setBusy(false) }
  }

  return (
    <form className="profile-edit" onSubmit={(e: FormEvent) => void submit(e)}>
      <label htmlFor="public-pseudo">Public player name</label>
      <input id="public-pseudo" value={value} maxLength={32} required minLength={3}
        pattern="[A-Za-z0-9_.-]{3,32}" onChange={(e: ChangeEvent<HTMLInputElement>) => setValue(e.target.value)} />
      <button type="submit" className="button button-secondary" disabled={busy}>Save profile</button>
      {message && <p role="status">{message}</p>}
    </form>
  )
}
