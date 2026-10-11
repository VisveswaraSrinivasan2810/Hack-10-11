import { useEffect, useState, useSyncExternalStore } from 'react'
import { API_BASE, probeApi, type ApiProbe } from './api'
import { demoAnalyze, demoAssign, demoComment, demoComplete, demoStatus, demoStore, demoSubmit, demoUpvote, demoVote } from './demo'
import { ago, fmtDate } from './format'
import { CATEGORIES, checkPhoto, type Analysis, type Incident, type Role, type SubmitResult } from './types'

const useIncidents = () => useSyncExternalStore(demoStore.subscribe, demoStore.get)
const WORKERS = ['Ravi K. (GHMC Serilingampally)', 'Meena S. (GHMC Sanitation)', 'Suresh V. (HMWSSB)']
const bandIcon = { Critical: '▲▲', High: '▲', Medium: '●', Low: '○' }

function Band({ i }: { i: Incident }) {
  return <span className={`pill band-${i.band}`} title="Civic Priority Score (estimate)">{bandIcon[i.band]} {i.band} · {i.score}/100</span>
}
function Status({ i }: { i: Incident }) {
  return <span className="statuses">
    <span className="pill work">Work: {i.status}</span>
    <span className={`pill res-${i.resolution}`}>Resolution: {i.resolution === 'Unverified' ? 'Not yet verified' : i.resolution === 'AwaitingVerification' ? 'Awaiting verification' : i.resolution}</span>
  </span>
}

export default function App() {
  const [role, setRole] = useState<Role>('Citizen')
  const [openId, setOpenId] = useState<string | null>(null)
  const [reporting, setReporting] = useState(false)
  const [probe, setProbe] = useState<ApiProbe>({ state: 'demo', detail: '' })
  const incidents = useIncidents()
  useEffect(() => { probeApi().then(setProbe) }, [])
  const open = incidents.find((i) => i.id === openId)
  const go = (r: Role) => { setRole(r); setOpenId(null); setReporting(false) }

  return <>
    <a className="skip" href="#main">Skip to content</a>
    <header className="top">
      <div className="brand"><span className="logo" aria-hidden>◎</span><div><b>CivicLens</b><small>Evidence → incident → verified fix</small></div></div>
      <nav aria-label="Preview role (not a login)" className="roles">
        <span className="hint">Preview as</span>
        {(['Citizen', 'Authority', 'FieldWorker'] as Role[]).map((r) => <button key={r} aria-pressed={role === r} onClick={() => go(r)}>{r === 'FieldWorker' ? 'Field worker' : r}</button>)}
      </nav>
      <div className={`mode mode-${probe.state}`} title={probe.detail}>{API_BASE ? (probe.state === 'reachable' ? '● API reachable · demo data' : '● API unreachable · demo data') : '● Demo data (offline)'}</div>
    </header>
    <main id="main">
      {open ? <Detail inc={open} role={role} back={() => setOpenId(null)} />
        : reporting ? <Report done={() => setReporting(false)} open={(id) => { setReporting(false); setOpenId(id) }} />
        : role === 'Citizen' ? <Citizen list={incidents} open={setOpenId} report={() => setReporting(true)} />
        : role === 'Authority' ? <Authority list={incidents} open={setOpenId} />
        : <Worker list={incidents} open={setOpenId} />}
    </main>
    <footer>Scores and AI labels are estimates, not decisions. Public views show observation counts only — never reporter identities. {probe.detail && <em>{probe.detail}</em>}
      <a className="link" href="/operations.html">Open backend-connected workspace →</a>
      <button className="link" onClick={() => demoStore.reset()}>Reset demo data</button></footer>
  </>
}

function Row({ i, open }: { i: Incident; open: (id: string) => void }) {
  return <li><button className="row" onClick={() => open(i.id)}>
    <span className="rowmain"><b>{i.categoryLabel}</b> <small>{i.code}</small><br /><span>{i.location}</span></span>
    <span className="rowmeta"><Band i={i} /><small>{i.observationCount} observations · {ago(i.firstObservedAt)} old</small></span>
  </button></li>
}

const AREAS = ['Madhapur', 'Gachibowli', 'Jubilee Hills', 'Kukatpally', 'Ameerpet', 'Secunderabad', 'Charminar', 'Dilsukhnagar']
const BANDS = ['Critical', 'High', 'Medium', 'Low']
const TABS: [string, string][] = [['all', 'For you'], ['urgent', '🔥 Urgent'], ['votes', '▲ Top voted'], ['fixing', '🛠 Being fixed'], ['fixed', '✅ Fixed']]

function Pick({ label, all, value, set }: { label: string; all: string[]; value: string; set: (v: string) => void }) {
  return <label>{label}<select value={value} onChange={(e) => set(e.target.value)}><option value="">Any</option>{all.map((v) => <option key={v}>{v}</option>)}</select></label>
}

function Post({ i, open, idx }: { i: Incident; open: (id: string) => void; idx: number }) {
  const [talk, setTalk] = useState(false)
  const [txt, setTxt] = useState('')
  return <li className="post" style={{ animationDelay: `${Math.min(idx, 8) * 60}ms` }}>
    <div className="phead"><span className="avatar" aria-hidden>{i.categoryLabel[0]}</span>
      <div><b>{i.categoryLabel}</b> <small>{i.code} · {ago(i.firstObservedAt)} ago</small><br /><small>📍 {i.location}, {i.zone}</small></div></div>
    <button className="imgbtn" onClick={() => open(i.id)} aria-label={`Open ${i.categoryLabel} at ${i.location}`}><img src={i.image} alt={`${i.categoryLabel} at ${i.location}`} loading="lazy" /></button>
    <div className="tags"><Band i={i} /><Status i={i} /></div>
    <p className="note">{i.observationCount} people observed this · {i.fixers.length ? `${i.fixers.length} authorised to fix` : 'No one assigned yet'}</p>
    <div className="actions">
      <button key={i.votes} className={`act vote ${i.voted ? 'on' : ''}`} aria-pressed={!!i.voted} onClick={() => demoUpvote(i.id)}>▲ {i.votes} <span>votes</span></button>
      <button className="act" aria-expanded={talk} onClick={() => setTalk(!talk)}>💬 {i.comments.length}</button>
      <button className="act" onClick={() => open(i.id)} title={i.fixers.join(', ') || 'No authorised workers yet'}>🛠 {i.fixers.length} <span>fixers</span></button>
    </div>
    {talk && <div className="comments">
      {i.comments.length === 0 && <small>No comments yet — be the first.</small>}
      {i.comments.map((c, k) => <p key={k}><b>{c.who}</b> {c.text}</p>)}
      <form onSubmit={(e) => { e.preventDefault(); if (txt.trim()) { demoComment(i.id, txt.trim()); setTxt('') } }}>
        <input aria-label="Add a comment" value={txt} onChange={(e) => setTxt(e.target.value)} placeholder="Add a comment…" /><button className="primary">Post</button></form></div>}
  </li>
}

function Citizen({ list, open, report }: { list: Incident[]; open: (id: string) => void; report: () => void }) {
  const [tab, setTab] = useState('all'), [q, setQ] = useState(''), [panel, setPanel] = useState(false)
  const [area, setArea] = useState(''), [cat, setCat] = useState(''), [bd, setBd] = useState('')
  const active = ([[area, setArea], [cat, setCat], [bd, setBd]] as [string, (v: string) => void][]).filter(([v]) => v)
  const ql = q.trim().toLowerCase()
  const shown = list.filter((i) => (!area || i.zone === area) && (!cat || i.categoryLabel === cat) && (!bd || i.band === bd)
    && (!ql || `${i.location} ${i.zone} ${i.categoryLabel} ${i.code}`.toLowerCase().includes(ql))
    && (tab === 'urgent' ? i.band === 'Critical' || i.band === 'High' : tab === 'fixing' ? ['Assigned', 'InProgress'].includes(i.status) : tab === 'fixed' ? i.status === 'WorkCompleted' : true))
    .sort((a, b) => tab === 'votes' ? b.votes - a.votes : b.score - a.score)
  const clear = () => { setArea(''); setCat(''); setBd(''); setQ(''); setTab('all') }
  return <section className="enter">
    <div className="hero"><h1>Hyderabad, what’s broken? 👀</h1><p>Snap it. Your photo joins other observations as evidence for one real incident — no duplicate pile-ups.</p>
      <button className="primary" onClick={report}>+ Report an issue</button></div>
    <div className="tabs" role="tablist" aria-label="Feed view">{TABS.map(([k, l]) => <button key={k} role="tab" aria-selected={tab === k} onClick={() => setTab(k)}>{l}</button>)}</div>
    <div className="searchbar">
      <input type="search" aria-label="Search incidents" placeholder="Search area, road or category…" value={q} onChange={(e) => setQ(e.target.value)} />
      <button aria-expanded={panel} onClick={() => setPanel(!panel)}>⚙ Filters{active.length ? ` (${active.length})` : ''}</button></div>
    {panel && <div className="panel"><Pick label="Area" all={AREAS} value={area} set={setArea} /><Pick label="Category" all={CATEGORIES.map((c) => c.label)} value={cat} set={setCat} /><Pick label="Priority" all={BANDS} value={bd} set={setBd} /></div>}
    {active.length > 0 && <div className="active">{active.map(([v, set]) => <button key={v} className="chip" onClick={() => set('')} aria-label={`Remove filter ${v}`}>{v} ✕</button>)}</div>}
    <p className="note" aria-live="polite">{shown.length} of {list.length} incidents</p>
    {shown.length === 0 ? <p className="empty">Nothing matches. <button className="link" onClick={clear}>Reset everything</button></p>
      : <ul className="feed">{shown.map((i, k) => <Post key={i.id} i={i} open={open} idx={k} />)}</ul>}
  </section>
}

function Report({ done, open }: { done: () => void; open: (id: string) => void }) {
  const [file, setFile] = useState<File | null>(null)
  const [desc, setDesc] = useState('')
  const [hint, setHint] = useState('Pothole')
  const [busy, setBusy] = useState(false)
  const [an, setAn] = useState<Analysis | null>(null)
  const [cat, setCat] = useState('')
  const [res, setRes] = useState<SubmitResult | null>(null)
  const [err, setErr] = useState('')

  const analyze = async () => {
    const bad = checkPhoto(file); if (bad) return setErr(bad)
    setErr(''); setBusy(true)
    const a = await demoAnalyze(hint, hint === 'Pothole' ? 'i1' : hint === 'Garbage' ? 'i2' : 'i3'); setAn(a); setCat(a.category); setBusy(false)
  }
  const submit = async (action: 'attach' | 'new', id?: string) => { setBusy(true); setRes(await demoSubmit(action, cat, id, file ? URL.createObjectURL(file) : undefined)); setBusy(false) }

  if (res) return <section className="card"><h1>✓ {res.message}</h1>
    <p>Incident <b>{res.code}</b> · priority {res.isNew ? '' : `${res.before} → `}<b>{res.after}/100</b> (estimate)</p>
    <ul>{res.reasons.map((r) => <li key={r}>{r}</li>)}</ul>
    <button className="primary" onClick={() => open(res.incidentId)}>Track this incident</button> <button onClick={done}>Done</button></section>

  return <section className="card"><button className="link" onClick={done}>← Back</button><h1>Report an issue</h1>
    {!an ? <>
      <label>Photo (required)<input type="file" accept="image/*" capture="environment" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></label>
      <label>What does it look like? <small>(demo: pick a type so the stand-in classifier has something to work with)</small>
        <select value={hint} onChange={(e) => setHint(e.target.value)}>{CATEGORIES.slice(0, 3).map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}</select></label>
      <label>Note (optional)<textarea value={desc} onChange={(e) => setDesc(e.target.value)} rows={2} placeholder="e.g. Deep pothole, two-wheelers swerving" /></label>
      {err && <p role="alert" className="err">{err}</p>}
      <button className="primary" disabled={busy} onClick={analyze}>{busy ? 'Analysing…' : 'Analyse photo'}</button>
    </> : <>
      <div className="ai"><b>AI suggestion · {an.confidencePercent}% confident</b><p>{an.reason}</p><small>{an.provider} · severity: {an.severityLabel}</small>
        <label>Category (you can change it)<select value={cat} onChange={(e) => setCat(e.target.value)}>
          <option value={an.category}>{an.categoryLabel} (suggested)</option>{an.alternatives.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}</select></label></div>
      {an.matches.length > 0 && cat === an.category ? an.matches.map((m) => <div key={m.incidentId} className="match"><b>Possible duplicate · {m.sameIncidentPercent}% similar</b><p>{m.reason}</p>
        <button className="primary" disabled={busy} onClick={() => submit('attach', m.incidentId)}>Yes, same issue — add my photo</button>
        <button disabled={busy} onClick={() => submit('new')}>No, it’s a different issue</button></div>)
        : <button className="primary" disabled={busy} onClick={() => submit('new')}>Create new incident</button>}
    </>}
  </section>
}

function Authority({ list, open }: { list: Incident[]; open: (id: string) => void }) {
  const active = [...list].sort((a, b) => b.score - a.score)
  const need = list.filter((i) => i.resolution === 'AwaitingVerification' || i.resolution === 'Disputed').length
  return <section><h1>Priority queue</h1>
    <div className="stats">
      <div><b>{list.length}</b>incidents</div><div><b>{list.reduce((a, i) => a + i.observationCount, 0)}</b>observations</div>
      <div><b>{list.filter((i) => i.band === 'Critical' || i.band === 'High').length}</b>high / critical</div><div><b>{need}</b>awaiting verification</div></div>
    <p className="note">Ranked by Civic Priority Score — an estimate. Open an incident to see why, and to assign or override.</p>
    <ul className="list">{active.map((i) => <Row key={i.id} i={i} open={open} />)}</ul></section>
}

function Worker({ list, open }: { list: Incident[]; open: (id: string) => void }) {
  const mine = list.filter((i) => i.status === 'Assigned' || i.status === 'InProgress')
  return <section><h1>My tasks</h1>
    {mine.length === 0 ? <p className="empty">No assigned tasks. Assigned incidents will appear here.</p> : <ul className="list">{mine.map((i) => <Row key={i.id} i={i} open={open} />)}</ul>}</section>
}

function Detail({ inc: i, role, back }: { inc: Incident; role: Role; back: () => void }) {
  const [after, setAfter] = useState<File | null>(null)
  const [note, setNote] = useState('')
  const [err, setErr] = useState('')
  const [worker, setWorker] = useState(WORKERS[0])
  return <section className="detail enter"><button className="link" onClick={back}>← Back</button>
    <h1>{i.categoryLabel} <small>{i.code}</small></h1><p>{i.location} · {i.zone}</p><img className="hdrimg" src={i.image} alt={`${i.categoryLabel} at ${i.location}`} />
    <p><Band i={i} /> <Status i={i} /></p>
    <p className="note">{i.observationCount} observations (people’s identities are never shown) corroborate this one incident · {i.confirmationCount} confirmations · first seen {fmtDate(i.firstObservedAt)}</p>
    <p>▲ {i.votes} votes · 💬 {i.comments.length} comments · 🛠 <b>{i.fixers.length}</b> authorised to fix{i.fixers.length ? `: ${i.fixers.join(', ')}` : ''}</p>

    <h2>Why this priority? <small>estimate, not a verdict</small></h2>
    <ul className="factors">{i.factors.map((f) => <li key={f.label}><div className="frow"><span>{f.label}</span><b>{f.points}/{f.max}</b></div>
      <div className="bar" aria-hidden><i style={{ width: `${(f.points / f.max) * 100}%` }} /></div><small>{f.reason}</small></li>)}</ul>
    {i.amplifier > 0 && <p className="note">Context amplifier ×{(1 + i.amplifier).toFixed(2)}: sensitive surroundings (school / hospital / metro nearby) make the same hazard more urgent. Applied after the factors above.</p>}
    {i.recurrence && <p className="warn">⚑ {i.recurrence}</p>}
    {i.context.length > 0 && <p className="note">Nearby context: {i.context.join(' · ')} <em>(seeded demo context)</em></p>}

    {role === 'Authority' && i.status !== 'WorkCompleted' && <div className="card"><h2>Authority action</h2>
      <label>Assign to<select value={worker} onChange={(e) => setWorker(e.target.value)}>{WORKERS.map((w) => <option key={w}>{w}</option>)}</select></label>
      <button className="primary" onClick={() => demoAssign(i.id, worker)}>Assign</button></div>}

    {role === 'FieldWorker' && (i.status === 'Assigned' || i.status === 'InProgress') && <div className="card"><h2>Field work</h2>
      {i.status === 'Assigned' && <button onClick={() => demoStatus(i.id, 'InProgress')}>Start work</button>}
      <label>After-photo (required)<input type="file" accept="image/*" capture="environment" onChange={(e) => setAfter(e.target.files?.[0] ?? null)} /></label>
      <label>Notes<textarea rows={2} value={note} onChange={(e) => setNote(e.target.value)} /></label>
      {err && <p role="alert" className="err">{err}</p>}
      <button className="primary" onClick={() => (after ? demoComplete(i.id, note || 'Work completed.') : setErr('Attach an after-photo as proof of work.'))}>Submit completion proof</button>
      <small>This marks work as completed — it is not a verified resolution until checked.</small></div>}

    {i.resolution !== 'Unverified' && <div className={`card res-${i.resolution}`}><h2>Resolution check</h2>
      <p>Work is marked complete{i.afterNote ? `: “${i.afterNote}”` : ''}. <b>{i.resolution === 'Verified' ? 'Verified by citizen evidence.' : i.resolution === 'Disputed' ? 'Conflicting evidence — requires review.' : 'Not yet verified.'}</b></p>
      <p>Citizen checks: {i.citizenVotes.fixed} say fixed · {i.citizenVotes.stillExists} say still there</p>
      {role === 'Citizen' && <><button className="primary" onClick={() => demoVote(i.id, 'fixed')}>Looks fixed</button> <button onClick={() => demoVote(i.id, 'stillExists')}>Still exists</button></>}</div>}

    <h2>Timeline</h2><ol className="timeline">{[...i.timeline].reverse().map((t, k) => <li key={k}><time>{fmtDate(t.at)}</time> {t.message}</li>)}</ol>
  </section>
}
