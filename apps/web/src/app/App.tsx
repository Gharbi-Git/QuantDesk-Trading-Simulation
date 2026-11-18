import { useEffect, useState } from 'react'

type Module = { code: string; name: string; owner: string }

export function App() {
  const [modules, setModules] = useState<Module[]>([])
  const [status, setStatus] = useState('Connecting to API...')

  useEffect(() => {
    const controller = new AbortController()
    fetch('/api/meta/modules', { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        return (await response.json()) as Module[]
      })
      .then((items) => { setModules(items); setStatus('Scaffold API connected') })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setStatus(`API not available: ${String(error)}`)
      })
    return () => controller.abort()
  }, [])

  return <main>
    <h1>QuantDesk</h1>
    <p>Engineering workspace - no trading functions have been implemented.</p>
    <p aria-live="polite">{status}</p>
    <h2>Planned functional modules ({modules.length})</h2>
    <ul>{modules.map((m) => <li key={m.code}>{m.code}: {m.name} ({m.owner})</li>)}</ul>
  </main>
}