import { createContext, useContext, useEffect, useRef, useState } from "react";
import {
  ApiError,
  getSession,
  imageBlob,
  login,
  request,
  setSession,
} from "./api";
import { ago, fmtDate } from "./format";
import { CONSTITUENCIES, constituencyOf, cr, fundingOf, inr } from "./accountability";
import {
  CATEGORIES,
  checkPhoto,
  type Analysis,
  type Auth,
  type Dashboard,
  type DemoInfo,
  type Detail as DetailDto,
  type Feed,
  type Notification,
  type Page,
  type Role,
  type Scenario,
  type ScenarioResult,
  type Social,
  type SubmitResult,
  type Summary,
  type Worker,
} from "./types";

const Context = createContext<{
  auth: Auth;
  demo: DemoInfo;
  busy: boolean;
  version: number;
  run: (fn: () => Promise<void>) => Promise<boolean>;
  refresh: () => void;
  open: (id: string) => void;
} | null>(null);
const useApp = () => useContext(Context)!;
const words = (s: string) => s.replace(/([a-z])([A-Z])/g, "$1 $2");
const norm = (s: string) =>
  ({
    LOW: "Low",
    MODERATE: "Moderate",
    HIGH: "High",
    VERYHIGH: "VeryHigh",
    CRITICAL: "Critical",
  })[s.replace(/\s/g, "").toUpperCase()] ?? s;
function ErrorText({ message }: { message: string }) {
  return message ? (
    <p role="alert" className="err">
      {message}
    </p>
  ) : null;
}
function Evidence({
  path,
  label,
  className = "",
}: {
  path?: string;
  label: string;
  className?: string;
}) {
  const { auth } = useApp();
  const [url, setUrl] = useState("");
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    setUrl("");
    setFailed(false);
    if (!path) return;
    const c = new AbortController();
    let local = "";
    imageBlob(path, c.signal)
      .then((blob) => {
        if (c.signal.aborted) return;
        local = URL.createObjectURL(blob);
        setUrl(local);
      })
      .catch(() => {
        if (!c.signal.aborted) setFailed(true);
      });
    return () => {
      c.abort();
      if (local) URL.revokeObjectURL(local);
    };
  }, [path, auth.token]);
  if (!path || failed)
    return (
      <div className="evidence-empty">
        {failed ? "Evidence could not be displayed." : "No photo available."}
      </div>
    );
  return url ? (
    <>
    <img
      className={`${className} ${path.startsWith("/demo/") ? "demo-illustration" : ""}`}
      src={url}
      alt={label}
      loading="lazy"
      onError={() => setFailed(true)}
    />
    {path.startsWith("/demo/") && <span className="evidence-caption">Seeded demo illustration · no uploaded photo yet</span>}
    </>
  ) : (
    <div className="evidence-empty">Loading evidence…</div>
  );
}
function Band({ i }: { i: Summary }) {
  const b = norm(i.priorityBand);
  return (
    <span
      className={`pill band-${b}`}
      title="Server-calculated Civic Priority Score"
    >
      {b === "Critical" ? "▲▲" : b === "High" || b === "VeryHigh" ? "▲" : "●"}{" "}
      {i.priorityBand} · {Math.round(i.priorityScore)}/100
    </span>
  );
}
function Status({ i }: { i: Summary }) {
  return (
    <span className="statuses">
      <span className="pill work">Work: {words(i.status)}</span>
      <span
        className={`pill res-${i.resolutionStatus === "VerifiedResolved" ? "Verified" : i.resolutionStatus}`}
      >
        Resolution:{" "}
        {i.resolutionStatus === "None"
          ? "Not yet verified"
          : words(i.resolutionStatus)}
      </span>
    </span>
  );
}

export default function App() {
  const [auth, setAuth] = useState<Auth | null>(getSession());
  const [demo, setDemo] = useState<DemoInfo>({ demoMode: false });
  const [boot, setBoot] = useState(true);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const lock = useRef(false);
  const [version, setVersion] = useState(0);
  const [page, setPage] = useState(1);
  const [feed, setFeed] = useState<Page<Feed>>({
    items: [],
    total: 0,
    page: 1,
    pageSize: 30,
  });
  const [loading, setLoading] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);
  const [view, setView] = useState("feed");
  const refresh = () => setVersion((v) => v + 1);
  useEffect(() => {
    const listener = () => {
      setAuth(getSession());
      setOpenId(null);
      setView("feed");
      setFeed({ items: [], total: 0, page: 1, pageSize: 30 });
      setPage(1);
    };
    window.addEventListener("session-changed", listener);
    return () => window.removeEventListener("session-changed", listener);
  }, []);
  useEffect(() => {
    const c = new AbortController();
    request<DemoInfo>("/api/demo/info", { signal: c.signal })
      .then(setDemo)
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      })
      .finally(() => {
        if (!c.signal.aborted) setBoot(false);
      });
    return () => c.abort();
  }, []);
  useEffect(() => {
    if (!auth) return;
    const c = new AbortController();
    setLoading(true);
    request<Page<Feed>>(`/api/feed?page=${page}&pageSize=30`, {
      signal: c.signal,
    })
      .then(setFeed)
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      })
      .finally(() => {
        if (!c.signal.aborted) setLoading(false);
      });
    return () => c.abort();
  }, [auth?.token, page, version]);
  async function run(fn: () => Promise<void>) {
    if (lock.current) return false;
    lock.current = true;
    setBusy(true);
    setError("");
    try {
      await fn();
      return true;
    } catch (e) {
      const x = e as Error;
      setError(
        x.message +
          (e instanceof ApiError && e.correlationId
            ? ` (reference ${e.correlationId})`
            : ""),
      );
      return false;
    } finally {
      setBusy(false);
      lock.current = false;
    }
  }
  const open = (id: string) => {
    setOpenId(id);
    setView("feed");
  };
  return (
    <>
      <a className="skip" href="#main">
        Skip to content
      </a>
      <header className="top">
        <div className="brand">
          <span className="logo" aria-hidden>
            ◎
          </span>
          <div>
            <b>CivicLens</b>
            <small>Evidence → incident → verified fix</small>
          </div>
        </div>
        {auth && (
          <nav className="roles" aria-label="Workspace">
            <button
              onClick={() => {
                setOpenId(null);
                setView("feed");
              }}
            >
              Feed
            </button>
            <button
              onClick={() => {
                setOpenId(null);
                setView("budget");
              }}
            >
              🏛 Constituencies &amp; budget
            </button>
            <button
              onClick={() => {
                setOpenId(null);
                setView("notifications");
              }}
            >
              Notifications
            </button>
            <button
              onClick={() => {
                setOpenId(null);
                setView("account");
              }}
            >
              Account
            </button>
            {demo.demoMode && auth.user.role === "Authority" && (
              <button
                onClick={() => {
                  setOpenId(null);
                  setView("demo");
                }}
              >
                Demo studio
              </button>
            )}
          </nav>
        )}
        <div className="mode">
          {demo.demoMode ? "● Seeded demo · API connected" : "● Live API"}
          {auth && (
            <>
              {" "}
              · {words(auth.user.role)}{" "}
              <button disabled={busy} onClick={() => setSession(null)}>
                Sign out
              </button>
            </>
          )}
        </div>
      </header>
      <main id="main">
        <ErrorText message={error} />
        {boot ? (
          <p className="empty">Connecting to CivicLens…</p>
        ) : !auth ? (
          <Login
            demo={demo}
            busy={busy}
            run={run}
            retry={() => {
              setBoot(true);
              request<DemoInfo>("/api/demo/info")
                .then((d) => {
                  setDemo(d);
                  setError("");
                })
                .catch((e) => setError(e.message))
                .finally(() => setBoot(false));
            }}
          />
        ) : (
          <Context.Provider
            value={{ auth, demo, busy, version, run, refresh, open }}
          >
            {demo.demoMode && (
              <div className="demo-banner">
                <span>
                  Hyderabad demo · database-backed actions · simulated AI · data
                  resets on server restart
                </span>
                <div className="roles">
                  {(["Citizen", "Authority", "FieldWorker"] as Role[]).map(
                    (role) => (
                      <button
                        key={role}
                        disabled={busy}
                        aria-pressed={auth.user.role === role}
                        onClick={() =>
                          run(async () => {
                            const a = demo.accounts?.find(
                              (a) => a.role === role,
                            );
                            if (!a) throw new Error("Demo account unavailable");
                            await login(a.email, a.password);
                          })
                        }
                      >
                        {words(role)}
                      </button>
                    ),
                  )}
                </div>
              </div>
            )}
            {openId ? (
              <Detail
                key={`${auth.user.id}:${openId}`}
                id={openId}
                back={() => setOpenId(null)}
              />
            ) : view === "report" ? (
              <Report done={() => setView("feed")} />
            ) : view === "budget" ? (
              <Budget />
            ) : view === "notifications" ? (
              <Notifications />
            ) : view === "account" ? (
              <Account />
            ) : view === "demo" ? (
              <DemoStudio />
            ) : (
              <>
                {auth.user.role === "Authority" && <Authority />}
                <Citizen
                  list={feed.items}
                  total={feed.total}
                  loading={loading}
                  report={() => setView("report")}
                />
                <div className="pagination">
                  <button
                    disabled={loading || busy || page <= 1}
                    onClick={() => setPage((p) => p - 1)}
                  >
                    ← Previous
                  </button>
                  <span>
                    Page {page} · {feed.total} incidents
                  </span>
                  <button
                    disabled={loading || busy || page * 30 >= feed.total}
                    onClick={() => setPage((p) => p + 1)}
                  >
                    Next →
                  </button>
                  <button disabled={loading || busy} onClick={refresh}>
                    Refresh
                  </button>
                </div>
              </>
            )}
          </Context.Provider>
        )}
      </main>
      <footer>
        Priority, evidence, workflow permissions and social activity come from
        the API. AI suggestions require human review.
      </footer>
    </>
  );
}
function Login({
  demo,
  busy,
  run,
  retry,
}: {
  demo: DemoInfo;
  busy: boolean;
  run: (fn: () => Promise<void>) => Promise<boolean>;
  retry: () => void;
}) {
  const [email, setEmail] = useState(""),
    [password, setPassword] = useState(""),
    [name, setName] = useState(""),
    [register, setRegister] = useState(false);
  return (
    <section className="card login">
      <h1>Your city. Your voice.</h1>
      <p>Sign in to report, prioritize and verify civic issues.</p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          run(async () => {
            if (register) {
              const a = await request<Auth>("/api/auth/register", {
                method: "POST",
                body: { email, password, name },
              });
              setSession(a);
            } else await login(email, password);
          });
        }}
      >
        {register && (
          <label>
            Name
            <input
              required
              maxLength={80}
              value={name}
              onChange={(e) => setName(e.target.value)}
              autoComplete="name"
            />
          </label>
        )}
        <label>
          Email
          <input
            required
            type="email"
            maxLength={160}
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            autoComplete="email"
          />
        </label>
        <label>
          Password
          <input
            required
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete={register ? "new-password" : "current-password"}
          />
        </label>
        {register && (
          <p className="note">
            Use at least 8 characters and at most 72 UTF-8 bytes, with a letter
            and a number. Registration creates a citizen account.
          </p>
        )}
        <button className="primary" disabled={busy}>
          {busy
            ? "Signing in…"
            : register
              ? "Create citizen account"
              : "Sign in"}
        </button>
      </form>
      <button
        disabled={busy}
        className="link"
        onClick={() => setRegister(!register)}
      >
        {register ? "Already registered? Sign in" : "Create an account"}
      </button>
      {demo.demoMode && (
        <div className="card">
          <h2>Try the Hyderabad demo</h2>
          <p className="note">
            These sign in to real seeded accounts. AI is simulated.
          </p>
          {(["Citizen", "Authority", "FieldWorker"] as Role[]).map((role) => (
            <button
              key={role}
              disabled={busy}
              onClick={() =>
                run(async () => {
                  const a = demo.accounts?.find((a) => a.role === role);
                  if (!a) throw new Error("Demo account unavailable");
                  await login(a.email, a.password);
                })
              }
            >
              {words(role)}
            </button>
          ))}
        </div>
      )}
      <button disabled={busy} className="link" onClick={retry}>
        Reconnect to server
      </button>
    </section>
  );
}
function Citizen({
  list,
  total,
  loading,
  report,
}: {
  list: Feed[];
  total: number;
  loading: boolean;
  report: () => void;
}) {
  const { auth } = useApp();
  const [tab, setTab] = useState("all"),
    [q, setQ] = useState(""),
    [panel, setPanel] = useState(false),
    [area, setArea] = useState(""),
    [cat, setCat] = useState(""),
    [band, setBand] = useState("");
  const shown = list
    .filter(
      ({ summary: i }) =>
        (!area || i.zoneName === area) &&
        (!cat || i.category === cat) &&
        (!band || norm(i.priorityBand) === band) &&
        `${i.locationLabel} ${i.zoneName} ${i.categoryLabel} ${i.code}`
          .toLowerCase()
          .includes(q.toLowerCase()) &&
        (tab === "urgent"
          ? i.priorityScore >= 50
          : tab === "fixing"
            ? ["Assigned", "Accepted", "InProgress"].includes(i.status)
            : tab === "fixed"
              ? i.status === "VerifiedResolved"
              : true),
    )
    .sort((a, b) =>
      tab === "votes"
        ? b.votes - a.votes
        : b.summary.priorityScore - a.summary.priorityScore,
    );
  return (
    <section className="enter">
      <div className="hero">
        <h1>
          {auth.user.role === "Citizen"
            ? "Hyderabad, what’s broken? 👀"
            : auth.user.role === "FieldWorker"
              ? "Your assigned work 🛠"
              : "Priority workspace ⚡"}
        </h1>
        <p>One incident. Shared evidence. A fix that can be verified.</p>
        {auth.user.role === "Citizen" && (
          <button className="primary" onClick={report}>
            + Report an issue
          </button>
        )}
      </div>
      <div className="tabs" role="tablist" aria-label="Feed view">
        {[
          ["all", "For you"],
          ["urgent", "🔥 Urgent"],
          ["votes", "▲ Top voted"],
          ["fixing", "🛠 Being fixed"],
          ["fixed", "✅ Verified fixed"],
        ].map(([k, l]) => (
          <button
            key={k}
            role="tab"
            aria-selected={tab === k}
            onClick={() => setTab(k)}
          >
            {l}
          </button>
        ))}
      </div>
      <div className="searchbar">
        <input
          type="search"
          aria-label="Search loaded incidents"
          placeholder="Search this page by area, road or category…"
          value={q}
          onChange={(e) => setQ(e.target.value)}
        />
        <button aria-expanded={panel} onClick={() => setPanel(!panel)}>
          ⚙ Filters
        </button>
      </div>
      {panel && (
        <div className="panel">
          <label>
            Zone
            <select value={area} onChange={(e) => setArea(e.target.value)}>
              <option value="">Any</option>
              {[
                ...new Set(list.map((x) => x.summary.zoneName).filter(Boolean)),
              ].map((z) => (
                <option key={z}>{z}</option>
              ))}
            </select>
          </label>
          <label>
            Category
            <select value={cat} onChange={(e) => setCat(e.target.value)}>
              <option value="">Any</option>
              {CATEGORIES.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            Priority
            <select value={band} onChange={(e) => setBand(e.target.value)}>
              <option value="">Any</option>
              {["Low", "Moderate", "High", "VeryHigh", "Critical"].map((b) => (
                <option key={b} value={b}>
                  {words(b)}
                </option>
              ))}
            </select>
          </label>
          <button
            onClick={() => {
              setArea("");
              setCat("");
              setBand("");
              setQ("");
              setTab("all");
            }}
          >
            Clear filters
          </button>
        </div>
      )}
      <p className="note" aria-live="polite">
        {loading
          ? "Loading incidents…"
          : `${shown.length} shown on this page · ${total} incidents total. Filtering and vote ranking apply to this page.`}
      </p>
      {!loading && !shown.length && (
        <p className="empty">
          No matching incidents. Try clearing filters or another page.
        </p>
      )}
      <ul className="feed">
        {shown.map((f, k) => (
          <Post key={f.summary.id} item={f} idx={k} />
        ))}
      </ul>
    </section>
  );
}
function Post({ item: f, idx }: { item: Feed; idx: number }) {
  const { auth, busy, run, refresh, open } = useApp();
  const i = f.summary;
  const [talk, setTalk] = useState(false);
  return (
    <li
      className="post"
      style={{ animationDelay: `${Math.min(idx, 8) * 60}ms` }}
    >
      <div className="phead">
        <span className="avatar" aria-hidden>
          {i.categoryLabel[0]}
        </span>
        <div>
          <b>{i.categoryLabel}</b>{" "}
          <small>
            {i.code} · {ago(i.firstObservedAt)} ago
          </small>
          <br />
          <small>
            📍 {i.locationLabel || "Location provided"},{" "}
            {i.zoneName || "Unzoned"}
          </small>
        </div>
      </div>
      <button
        className="imgbtn"
        onClick={() => open(i.id)}
        aria-label={`Open ${i.code}`}
      >
        <Evidence
          path={i.primaryImageUrl}
          label={`${i.categoryLabel} evidence`}
        />
      </button>
      <div className="tags">
        <Band i={i} />
        <Status i={i} />
      </div>
      <p className="note">
        {i.observationCount} observations · {i.confirmationCount} confirmations
        · {f.fixers.length} assigned fixer{f.fixers.length === 1 ? "" : "s"}
      </p>
      <div className="actions">
        <button
          className={`act vote ${f.voted ? "on" : ""}`}
          disabled={busy || auth.user.role !== "Citizen"}
          aria-pressed={f.voted}
          onClick={() =>
            run(async () => {
              await request(`/api/incidents/${i.id}/upvote`, {
                method: "PUT",
                body: { active: !f.voted },
              });
              refresh();
            })
          }
        >
          ▲ {f.votes} votes
        </button>
        <button
          className="act"
          aria-expanded={talk}
          onClick={() => setTalk(!talk)}
        >
          💬 {f.commentCount}
        </button>
        <button
          className="act"
          onClick={() => open(i.id)}
          title={f.fixers.join(", ") || "No worker assigned"}
        >
          🛠 {f.fixers.length} fixers
        </button>
      </div>
      {talk && <Comments id={i.id} />}
    </li>
  );
}
function Comments({ id }: { id: string }) {
  const { version, busy, run, refresh } = useApp();
  const [data, setData] = useState<Social | null>(null),
    [text, setText] = useState(""),
    [page, setPage] = useState(1),
    [error, setError] = useState("");
  useEffect(() => {
    const c = new AbortController();
    request<Social>(`/api/incidents/${id}/social?page=${page}`, {
      signal: c.signal,
    })
      .then((d) => {
        setData(d);
        setError("");
      })
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      });
    return () => c.abort();
  }, [id, page, version]);
  return (
    <div className="comments">
      <ErrorText message={error} />
      {!data ? (
        <p>Loading comments…</p>
      ) : (
        <>
          {data.comments.items.map((c) => (
            <p key={c.id}>
              <b>{c.authorLabel}</b> {c.text}{" "}
              <small>{fmtDate(c.createdAt)}</small>
              {c.canDelete && (
                <button
                  className="link"
                  disabled={busy}
                  onClick={() =>
                    run(async () => {
                      await request(`/api/incidents/${id}/comments/${c.id}`, {
                        method: "DELETE",
                      });
                      refresh();
                    })
                  }
                >
                  Delete
                </button>
              )}
            </p>
          ))}
          {!data.commentCount && <small>No comments yet.</small>}
          <div className="pagination">
            <button
              disabled={busy || page <= 1}
              onClick={() => setPage((p) => p - 1)}
            >
              Previous
            </button>
            <span>{data.commentCount} comments</span>
            <button
              disabled={busy || page * 30 >= data.commentCount}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </button>
          </div>
        </>
      )}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (text.trim())
            run(async () => {
              await request(`/api/incidents/${id}/comments`, {
                method: "POST",
                body: { text: text.trim() },
              });
              setText("");
              setPage(1);
              refresh();
            });
        }}
      >
        <input
          required
          maxLength={1000}
          aria-label="Add a comment"
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="Add a comment…"
        />
        <button className="primary" disabled={busy}>
          Post
        </button>
      </form>
    </div>
  );
}
function Report({ done }: { done: () => void }) {
  const { demo, busy, run, refresh, open } = useApp();
  const [file, setFile] = useState<File | null>(null),
    [desc, setDesc] = useState(""),
    [label, setLabel] = useState(demo.city?.reportLocationLabel || ""),
    [lat, setLat] = useState(String(demo.city?.reportLatitude ?? "")),
    [lon, setLon] = useState(String(demo.city?.reportLongitude ?? "")),
    [hint, setHint] = useState("Pothole"),
    [error, setError] = useState("");
  const [analysis, setAnalysis] = useState<Analysis | null>(null),
    [category, setCategory] = useState(""),
    [matches, setMatches] = useState<Analysis["matches"]>([]),
    [result, setResult] = useState<SubmitResult | null>(null);
  async function analyze() {
    const bad = checkPhoto(file);
    if (bad) {
      setError(bad);
      return;
    }
    if (
      !lat ||
      !lon ||
      !Number.isFinite(Number(lat)) ||
      !Number.isFinite(Number(lon))
    ) {
      setError("Enter valid coordinates.");
      return;
    }
    setError("");
    await run(async () => {
      const body = new FormData();
      body.append("image", file!);
      body.append("latitude", lat);
      body.append("longitude", lon);
      body.append("locationLabel", label);
      body.append(
        "description",
        (demo.demoMode
          ? `${CATEGORIES.find((c) => c.value === hint)?.label}: `
          : "") + desc,
      );
      const a = await request<Analysis>("/api/observations/analyze", {
        method: "POST",
        body,
      });
      setAnalysis(a);
      setCategory(a.detectedCategory);
      setMatches(a.matches);
    });
  }
  async function submit(incidentId?: string) {
    await run(async () => {
      const r = await request<SubmitResult>("/api/observations", {
        method: "POST",
        body: {
          draftId: analysis!.draftId,
          selectedCategory: category,
          description: desc,
          action: incidentId ? "AttachToExisting" : "CreateNew",
          incidentId: incidentId ?? null,
        },
      });
      setResult(r);
      refresh();
    });
  }
  if (result)
    return (
      <section className="card">
        <h1>✓ {result.message}</h1>
        <p>
          {result.code} · priority{" "}
          {result.isNewIncident ? "" : `${result.priorityBefore} → `}
          {result.priorityAfter}/100
        </p>
        <ul>
          {result.reasons.map((r, k) => (
            <li key={k}>{r}</li>
          ))}
        </ul>
        <button className="primary" onClick={() => open(result.incidentId)}>
          Track incident
        </button>
        <button onClick={done}>Done</button>
      </section>
    );
  return (
    <section className="card">
      <button className="link" disabled={busy} onClick={done}>
        ← Back
      </button>
      <h1>Report an issue</h1>
      <ErrorText message={error} />
      {!analysis ? (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            analyze();
          }}
        >
          <label>
            Photo (JPEG, PNG or WebP)
            <input
              required
              type="file"
              accept="image/jpeg,image/png,image/webp"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </label>
          <div className="panel">
            <label>
              Latitude
              <input
                required
                type="number"
                step="any"
                min={-90}
                max={90}
                value={lat}
                onChange={(e) => setLat(e.target.value)}
              />
            </label>
            <label>
              Longitude
              <input
                required
                type="number"
                step="any"
                min={-180}
                max={180}
                value={lon}
                onChange={(e) => setLon(e.target.value)}
              />
            </label>
            <button
              type="button"
              disabled={busy}
              onClick={() => {
                if (!navigator.geolocation) {
                  setError("Location is unavailable in this browser.");
                  return;
                }
                navigator.geolocation.getCurrentPosition(
                  (p) => {
                    setLat(String(p.coords.latitude));
                    setLon(String(p.coords.longitude));
                  },
                  () =>
                    setError(
                      "Location permission denied or unavailable. Enter coordinates manually.",
                    ),
                  { timeout: 10000 },
                );
              }}
            >
              Use my location
            </button>
          </div>
          <label>
            Location label
            <input
              maxLength={160}
              value={label}
              onChange={(e) => setLabel(e.target.value)}
            />
          </label>
          {demo.demoMode && (
            <label>
              Demo classifier hint
              <select value={hint} onChange={(e) => setHint(e.target.value)}>
                {CATEGORIES.map((c) => (
                  <option key={c.value} value={c.value}>
                    {c.label}
                  </option>
                ))}
              </select>
            </label>
          )}
          <label>
            Description
            <textarea
              maxLength={500}
              value={desc}
              onChange={(e) => setDesc(e.target.value)}
            />
          </label>
          <button className="primary" disabled={busy}>
            {busy ? "Analysing…" : "Analyse photo"}
          </button>
        </form>
      ) : (
        <>
          <Evidence
            path={analysis.imageUrl}
            label="Uploaded observation"
            className="hdrimg"
          />
          <div className="ai">
            <b>
              {analysis.detectedCategoryLabel} · {analysis.confidencePercent}%
              confidence
            </b>
            <p>{analysis.reason}</p>
            <small>
              Provider: {analysis.provider} · severity: {analysis.severityLabel}
            </small>
            {analysis.warnings.map((w, k) => (
              <p className="warn" key={k}>
                {w}
              </p>
            ))}
            <label>
              Confirm category
              <select
                value={category}
                disabled={busy}
                onChange={(e) => {
                  const value = e.target.value;
                  run(async () => {
                    const m = await request<Analysis["matches"]>(
                      `/api/observations/drafts/${analysis.draftId}/matches?category=${encodeURIComponent(value)}`,
                    );
                    setCategory(value);
                    setMatches(m);
                  });
                }}
              >
                {CATEGORIES.map((c) => (
                  <option key={c.value} value={c.value}>
                    {c.label}
                  </option>
                ))}
              </select>
            </label>
            <p>
              Context: {analysis.context.locationRiskLabel} ·{" "}
              {analysis.context.reasons.join(" · ")}
            </p>
          </div>
          {matches.map((m) => (
            <div key={m.incidentId} className="match">
              <b>
                {m.code} · {m.sameIncidentPercent}% match · {m.distanceMeters} m
                away
              </b>
              <p>{m.reason}</p>
              <button
                className="primary"
                disabled={busy}
                onClick={() => submit(m.incidentId)}
              >
                Same issue — join report
              </button>
            </div>
          ))}
          <button className="primary" disabled={busy} onClick={() => submit()}>
            Create a separate incident
          </button>
          <button
            disabled={busy}
            onClick={() => {
              setAnalysis(null);
              setMatches([]);
            }}
          >
            Change photo or location
          </button>
        </>
      )}
    </section>
  );
}
function Budget() {
  const { open } = useApp();
  const [items, setItems] = useState<Summary[] | null>(null),
    [err, setErr] = useState(""),
    [sel, setSel] = useState("");
  useEffect(() => {
    const c = new AbortController();
    request<Page<Feed>>("/api/feed?page=1&pageSize=100", { signal: c.signal })
      .then((p) => setItems(p.items.map((f) => f.summary)))
      .catch((e) => { if (!c.signal.aborted) setErr(e.message); });
    return () => c.abort();
  }, []);
  if (err) return <ErrorText message={err} />;
  if (!items) return <p className="empty">Loading constituencies…</p>;
  const rows = CONSTITUENCIES.map((c) => {
    const inc = items.filter((i) => constituencyOf(i.zoneName ?? "").name === c.name),
      act = inc.filter((i) => !["VerifiedResolved", "Rejected"].includes(i.status));
    return { c, inc, act, need: act.reduce((a, i) => a + fundingOf(i).estCost, 0) };
  }).sort((a, b) => b.act.length - a.act.length);
  return (
    <section className="enter">
      <h1>Constituencies &amp; budget</h1>
      <p className="warn">
        Demo data: MLA names, contractors, budgets and repair costs are fictional placeholders and rough estimates — the
        API doesn’t provide them yet. Replace with official records before any real use.
      </p>
      <ul className="cgrid">
        {rows.map(({ c, inc, act, need }) => (
          <li key={c.name} className="ccard">
            <button className="cbtn" aria-expanded={sel === c.name} onClick={() => setSel(sel === c.name ? "" : c.name)}>
              <b>{c.name}</b>
              <small>MLA (demo): {c.mla}</small>
              <div className="frow">
                <span>Spent {cr(c.spentCr)} of {cr(c.budgetCr)}</span>
                <b>{Math.round((c.spentCr / c.budgetCr) * 100)}%</b>
              </div>
              <div className="bar" aria-hidden><i style={{ width: `${(c.spentCr / c.budgetCr) * 100}%` }} /></div>
              <small>{act.length} open of {inc.length} incidents · est. {inr(need)} outstanding · {cr(c.budgetCr - c.spentCr)} left</small>
            </button>
            {sel === c.name &&
              (inc.length ? (
                <ul className="list">
                  {inc.map((i) => (
                    <li key={i.id}>
                      <button className="row" onClick={() => open(i.id)}>
                        <span className="rowmain"><b>{i.categoryLabel}</b> <small>{i.code}</small><br />{i.locationLabel}</span>
                        <span className="rowmeta"><Band i={i} /><small>{inr(fundingOf(i).estCost)} est.</small></span>
                      </button>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="note">No incidents recorded here yet.</p>
              ))}
          </li>
        ))}
      </ul>
    </section>
  );
}

function Funding({ s }: { s: Summary }) {
  const f = fundingOf(s),
    when = (d: Date) => d.toLocaleDateString("en-IN", { month: "short", year: "numeric" });
  return (
    <div className="card">
      <h2>Funding &amp; accountability <small>demo data</small></h2>
      <p><b>{f.constituency.name}</b> · MLA (demo): {f.constituency.mla}</p>
      <p>
        Estimated repair: <b>{inr(f.estCost)}</b>{" "}
        <small>from budget head “{f.head}” · {cr(f.constituency.budgetCr - f.constituency.spentCr)} unspent this year</small>
      </p>
      <p>Contractor of record: <b>{f.contractor}</b></p>
      {f.warranty && (
        <p className={f.warranty.active ? "warn" : "note"}>
          {f.warranty.active
            ? `Possibly within the defect-liability period (until ${when(f.warranty.until)}) — the contractor may be responsible for this repair at no extra cost. Check the work order.`
            : `The defect-liability period may have ended in ${when(f.warranty.until)}.`}
        </p>
      )}
    </div>
  );
}

function Detail({ id, back }: { id: string; back: () => void }) {
  const { auth, version, busy, run, refresh, demo, open } = useApp();
  const [data, setData] = useState<DetailDto | null>(null),
    [workers, setWorkers] = useState<Worker[]>([]),
    [worker, setWorker] = useState(""),
    [reason, setReason] = useState(""),
    [category, setCategory] = useState(""),
    [severity, setSeverity] = useState(""),
    [after, setAfter] = useState<File | null>(null),
    [note, setNote] = useState(""),
    [error, setError] = useState("");
  useEffect(() => {
    const c = new AbortController();
    setData(null);
    Promise.all([
      request<DetailDto>(`/api/incidents/${id}`, { signal: c.signal }),
      auth.user.role === "Authority"
        ? request<Worker[]>("/api/users/workers", { signal: c.signal })
        : Promise.resolve([]),
    ])
      .then(([d, w]) => {
        setData(d);
        setWorkers(w);
        setWorker(d.summary.assignedWorkerId ?? w[0]?.id ?? "");
        setError("");
      })
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      });
    return () => c.abort();
  }, [id, version, auth.token]);
  async function mutate(action: string, body: unknown = {}) {
    await run(async () => {
      await request(`/api/incidents/${id}/${action}`, { method: "POST", body });
      refresh();
    });
  }
  if (!data)
    return (
      <section>
        <button className="link" onClick={back}>
          ← Back
        </button>
        <ErrorText message={error} />
        <p>{error ? "Unable to load this incident." : "Loading incident…"}</p>
        <button onClick={refresh}>Retry</button>
      </section>
    );
  const i = data.summary;
  const r = data.resolution;
  const factors: [string, number][] = [
    ["Severity", data.priority.severity],
    ["Citizen evidence", data.priority.citizenEvidence],
    ["Location risk", data.priority.locationRisk],
    ["Incident age", data.priority.incidentAge],
    ["Exposure", data.priority.exposure],
    ["Recurrence", data.priority.recurrence],
  ];
  return (
    <section className="detail enter">
      <button className="link" disabled={busy} onClick={back}>
        ← Back
      </button>
      <h1>
        {i.categoryLabel} <small>{i.code}</small>
      </h1>
      <p>
        {i.locationLabel || "Location provided"} · {i.zoneName || "Unzoned"}
      </p>
      <Evidence
        path={i.primaryImageUrl}
        label={`${i.categoryLabel} evidence`}
        className="hdrimg"
      />
      <p>
        <Band i={i} /> <Status i={i} />
      </p>
      <p className="note">
        {i.observationCount} observations · {i.confirmationCount} confirmations
        · first seen {fmtDate(i.firstObservedAt)}
      </p>
      <p className="warn">{data.statusBanner}</p>
      <p>Assigned to: {i.assigneeName || "Not assigned"}</p>
      <h2>Why this priority?</h2>
      <ul className="factors">
        {factors.map(([label, value]) => (
          <li key={label}>
            <div className="frow">
              <span>{label}</span>
              <b>{value}/100</b>
            </div>
            <div className="bar" aria-hidden>
              <i style={{ width: `${Math.max(0, Math.min(100, value))}%` }} />
            </div>
          </li>
        ))}
      </ul>
      {data.priority.contextAmplifier > 0 && (
        <p className="note">
          Context amplifier ×{(1 + data.priority.contextAmplifier).toFixed(2)}
        </p>
      )}
      <ul>
        {data.priority.reasons.map((r, k) => (
          <li key={k}>{r}</li>
        ))}
      </ul>
      {data.recurrence.isRecurring && (
        <p className="warn">⚑ {data.recurrence.summary}</p>
      )}
      {data.canConfirm && (
        <button
          className="primary"
          disabled={busy}
          onClick={() => mutate("confirm")}
        >
          I also see this issue
        </button>
      )}
      {auth.user.role === "Authority" && (
        <div className="card">
          <h2>Authority actions</h2>
          <label>
            Reason / assignment note
            <textarea
              maxLength={500}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            />
          </label>
          {["Reported", "Validated", "Reopened", "RequiresReview"].includes(
            i.status,
          ) && (
            <>
              <label>
                Assign an active field worker
                <select
                  value={worker}
                  onChange={(e) => setWorker(e.target.value)}
                >
                  {workers.map((w) => (
                    <option key={w.id} value={w.id}>
                      {w.name} · {w.activeAssignments} active assignments
                    </option>
                  ))}
                </select>
              </label>
              <button
                className="primary"
                disabled={busy || !worker}
                onClick={() =>
                  mutate("assign", { workerId: worker, note: reason })
                }
              >
                Assign
              </button>
            </>
          )}
          <label>
            Category override
            <select
              value={category}
              onChange={(e) => setCategory(e.target.value)}
            >
              <option value="">Keep current category</option>
              {CATEGORIES.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            Severity override (0–100)
            <input
              type="number"
              min={0}
              max={100}
              value={severity}
              onChange={(e) => setSeverity(e.target.value)}
            />
          </label>
          <button
            disabled={busy || !reason.trim() || (!category && !severity)}
            onClick={() =>
              mutate("override", {
                category: category || null,
                severity: severity === "" ? null : Number(severity),
                reason,
              })
            }
          >
            Save audited override
          </button>
        </div>
      )}
      {data.allowedNextStatuses.length > 0 && (
        <div className="card">
          <h2>Allowed next steps</h2>
          {data.allowedNextStatuses
            .filter((s) => s !== "Assigned")
            .map((status) => (
              <button
                key={status}
                disabled={busy}
                onClick={() =>
                  mutate("status", {
                    status,
                    reason: reason || "Updated by the assigned operator",
                  })
                }
              >
                {words(status)}
              </button>
            ))}
        </div>
      )}
      {auth.user.role === "FieldWorker" && i.status === "InProgress" && (
        <form
          className="card"
          onSubmit={(e) => {
            e.preventDefault();
            const bad = checkPhoto(after);
            if (bad) {
              setError(bad);
              return;
            }
            setError("");
            run(async () => {
              const body = new FormData();
              body.append("afterImage", after!);
              body.append("notes", note);
              await request(`/api/incidents/${id}/resolution`, {
                method: "POST",
                body,
              });
              setAfter(null);
              setNote("");
              refresh();
            });
          }}
        >
          <h2>Submit completion evidence</h2>
          <label>
            After photo
            <input
              required
              type="file"
              accept="image/jpeg,image/png,image/webp"
              onChange={(e) => setAfter(e.target.files?.[0] ?? null)}
            />
          </label>
          <label>
            Work notes
            <textarea
              maxLength={1000}
              value={note}
              onChange={(e) => setNote(e.target.value)}
            />
          </label>
          <ErrorText message={error} />
          <button className="primary" disabled={busy}>
            Submit for verification
          </button>
          <p className="note">
            Work completed is not the same as verified resolved.
          </p>
        </form>
      )}
      {r && (
        <div className="card">
          <h2>Resolution check</h2>
          <Evidence
            path={r.afterImageUrl}
            label="After photo"
            className="hdrimg"
          />
          <p>{r.workerNotes}</p>
          <p>
            <b>{r.outcomeLabel}</b> · {r.resolutionConfidencePercent}%
            resolution confidence
          </p>
          <p>{r.aiReason || r.neutralNote}</p>
          <p>
            Citizen checks: {r.citizens.fixed} fixed · {r.citizens.stillExists}{" "}
            still exists · {r.citizens.unsure} unsure
          </p>
          {data.canVerifyResolution && (
            <>
              {["Fixed", "StillExists", "Unsure"].map((vote) => (
                <button
                  key={vote}
                  className={vote === "Fixed" ? "primary" : ""}
                  disabled={busy}
                  aria-pressed={data.myVote === vote}
                  onClick={() => {
                    if (demo.demoMode || data.isContributor) {
                      mutate("verify", {
                        vote,
                        latitude: demo.demoMode ? i.latitude : null,
                        longitude: demo.demoMode ? i.longitude : null,
                      });
                      return;
                    }
                    if (!navigator.geolocation) {
                      setError(
                        "Browser location is required for nearby verification.",
                      );
                      return;
                    }
                    navigator.geolocation.getCurrentPosition(
                      (p) =>
                        mutate("verify", {
                          vote,
                          latitude: p.coords.latitude,
                          longitude: p.coords.longitude,
                        }),
                      () =>
                        setError(
                          "Allow location access to verify as a nearby citizen.",
                        ),
                      { timeout: 10000 },
                    );
                  }}
                >
                  {words(vote)}
                </button>
              ))}
            </>
          )}
        </div>
      )}
      <h2>Infrastructure memory</h2>
      {data.memory.length ? (
        data.memory.map((m, k) => (
          <p key={k}>
            <button className="link" onClick={() => open(m.incidentId)}>
              {m.code} · {m.categoryLabel}
            </button>{" "}
            · {fmtDate(m.at)} · {words(m.status)}
          </p>
        ))
      ) : (
        <p>No earlier incidents around this location.</p>
      )}
      {data.correlations.length > 0 && (
        <>
          <h2>Possible relationships</h2>
          {data.correlations.map((c, k) => (
            <p key={k}>
              <button className="link" onClick={() => open(c.targetIncidentId)}>
                {c.targetCode}
              </button>{" "}
              · {c.confidenceLabel}: {c.explanation}
            </p>
          ))}
        </>
      )}
      <h2>Evidence observations</h2>
      <div className="observation-grid">
        {data.observations.map((o) => (
          <div key={o.id}>
            <Evidence path={o.imageUrl} label="Citizen evidence" />
            <small>{fmtDate(o.createdAt)}</small>
          </div>
        ))}
      </div>
      <h2>Discussion</h2>
      <Comments id={id} />
      <Funding s={data.summary} />
      <h2>Timeline</h2>
      <ol className="timeline">
        {[...data.timeline].reverse().map((t, k) => (
          <li key={k}>
            <time>{fmtDate(t.at)}</time> {t.message}
          </li>
        ))}
      </ol>
    </section>
  );
}
function Authority() {
  const { version, open } = useApp();
  const [data, setData] = useState<Dashboard | null>(null),
    [hotspots, setHotspots] = useState<
      { name: string; title: string; incidentCount: number }[]
    >([]),
    [queues, setQueues] = useState<{ summary: Summary }[][]>([[], []]),
    [analytics, setAnalytics] = useState<
      Record<string, { label: string; value: number }[]>
    >({}),
    [error, setError] = useState("");
  useEffect(() => {
    const c = new AbortController();
    Promise.all([
      request<Dashboard>("/api/dashboard/summary", { signal: c.signal }),
      request<{ name: string; title: string; incidentCount: number }[]>(
        "/api/dashboard/hotspots",
        { signal: c.signal },
      ),
      request<{ summary: Summary }[]>("/api/dashboard/verification-queue", {
        signal: c.signal,
      }),
      request<{ summary: Summary }[]>("/api/dashboard/disputed", {
        signal: c.signal,
      }),
      request<Record<string, { label: string; value: number }[]>>(
        "/api/dashboard/analytics",
        { signal: c.signal },
      ),
    ])
      .then(([d, h, q, r, a]) => {
        setData(d);
        setHotspots(h);
        setQueues([q, r]);
        setAnalytics(a);
        setError("");
      })
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      });
    return () => c.abort();
  }, [version]);
  return (
    <section>
      <h1>Authority intelligence</h1>
      <ErrorText message={error} />
      {data ? (
        <>
          <div className="stats">
            <div>
              <b>{data.activeIncidents}</b>active incidents
            </div>
            <div>
              <b>{data.totalObservations}</b>observations
            </div>
            <div>
              <b>{data.criticalIncidents}</b>critical priority
            </div>
            <div>
              <b>{data.awaitingVerification}</b>awaiting verification
            </div>
          </div>
          <p className="note">
            City health: {data.civicHealthScore}/100 ·{" "}
            {data.disputedResolutions} disputed resolutions
          </p>
          {queues.map((q, k) => (
            <details className="card" key={k}>
              <summary>
                {k ? "Disputed closures" : "Verification queue"} · {q.length}
              </summary>
              {q.length ? (
                q.map(({ summary: i }) => (
                  <p key={i.id}>
                    <button className="link" onClick={() => open(i.id)}>
                      {i.code} · {i.categoryLabel}
                    </button>{" "}
                    · {words(i.status)}
                  </p>
                ))
              ) : (
                <p>Queue is empty.</p>
              )}
            </details>
          ))}
          {hotspots.length > 0 && (
            <div className="panel">
              {hotspots.slice(0, 5).map((h, k) => (
                <div key={k}>
                  <b>{h.name}</b>
                  <p>
                    {h.title} · {h.incidentCount} incidents
                  </p>
                </div>
              ))}
            </div>
          )}
          <details className="card">
            <summary>City analytics</summary>
            <div className="panel">
              {["byCategory", "byStatus", "cpsDistribution", "zoneHealth"].map(
                (key) => (
                  <div key={key}>
                    <h3>{words(key)}</h3>
                    {analytics[key]?.map((v, k) => (
                      <p key={k}>
                        {v.label}: <b>{Math.round(v.value * 10) / 10}</b>
                      </p>
                    ))}
                  </div>
                ),
              )}
            </div>
          </details>
        </>
      ) : (
        <p>Loading city intelligence…</p>
      )}
    </section>
  );
}
function Notifications() {
  const { version, busy, run, refresh, open } = useApp();
  const [items, setItems] = useState<Notification[]>([]),
    [error, setError] = useState("");
  useEffect(() => {
    const c = new AbortController();
    request<Notification[]>("/api/users/me/notifications", { signal: c.signal })
      .then((d) => {
        setItems(d);
        setError("");
      })
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      });
    return () => c.abort();
  }, [version]);
  return (
    <section>
      <h1>Notifications</h1>
      <ErrorText message={error} />
      <button
        disabled={busy}
        onClick={() =>
          run(async () => {
            await request("/api/users/me/notifications/read", {
              method: "POST",
            });
            refresh();
          })
        }
      >
        Mark all read
      </button>
      {!items.length ? (
        <p>No notifications yet.</p>
      ) : (
        items.map((n) => (
          <article className="card" key={n.id}>
            <h2>
              {n.isRead ? "" : "● "}
              {n.title}
            </h2>
            <p>{n.message}</p>
            <small>{fmtDate(n.createdAt)}</small>
            {n.incidentId && (
              <button onClick={() => open(n.incidentId!)}>Open incident</button>
            )}
          </article>
        ))
      )}
    </section>
  );
}
function Account() {
  const { auth, busy, run } = useApp();
  const [current, setCurrent] = useState(""),
    [next, setNext] = useState("");
  return (
    <section className="card">
      <h1>{auth.user.name}</h1>
      <p>
        {auth.user.email} · {words(auth.user.role)}
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          run(async () => {
            await request("/api/account/password", {
              method: "POST",
              body: { currentPassword: current, newPassword: next },
            });
            setSession(null);
          });
        }}
      >
        <h2>Change password</h2>
        <label>
          Current password
          <input
            required
            type="password"
            autoComplete="current-password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
          />
        </label>
        <label>
          New password
          <input
            required
            type="password"
            autoComplete="new-password"
            value={next}
            onChange={(e) => setNext(e.target.value)}
          />
        </label>
        <p className="note">
          After changing your password, sign in again. Existing sessions will be
          revoked.
        </p>
        <button className="primary" disabled={busy}>
          Update password
        </button>
      </form>
    </section>
  );
}
function DemoStudio() {
  const { busy, run, refresh, open } = useApp();
  const [scenarios, setScenarios] = useState<Scenario[]>([]),
    [result, setResult] = useState<ScenarioResult | null>(null),
    [error, setError] = useState("");
  useEffect(() => {
    const c = new AbortController();
    request<Scenario[]>("/api/demo/scenarios", { signal: c.signal })
      .then(setScenarios)
      .catch((e) => {
        if (!c.signal.aborted) setError(e.message);
      });
    return () => c.abort();
  }, []);
  return (
    <section>
      <h1>Demo studio</h1>
      <p>
        Run 2 → 3 → 7 → 8 to show deduplication, context, verified resolution
        and a disputed closure. Scenarios call real services with simulated
        photo/AI inputs.
      </p>
      <ErrorText message={error} />
      <button
        disabled={busy}
        onClick={() => {
          if (
            window.confirm(
              "Delete all disposable demo data and reseed Hyderabad?",
            )
          )
            run(async () => {
              await request("/api/demo/reset", { method: "POST" });
              setResult(null);
              setSession(null);
            });
        }}
      >
        Reset seeded demo
      </button>
      {result && (
        <div className="card">
          <h2>{result.name}</h2>
          <p>{result.summary}</p>
          <ul>
            {result.highlights.map((h, k) => (
              <li key={k}>{h}</li>
            ))}
          </ul>
          {result.incidentId && (
            <button
              className="primary"
              onClick={() => open(result.incidentId!)}
            >
              Explore incident
            </button>
          )}
        </div>
      )}
      <div className="scenario-grid">
        {scenarios.map((s) => (
          <div className="card" key={s.id}>
            <h2>
              {s.id}. {s.name}
            </h2>
            <p>{s.description}</p>
            <button
              className="primary"
              disabled={busy}
              onClick={() =>
                run(async () => {
                  setResult(
                    await request<ScenarioResult>(
                      `/api/demo/scenarios/${s.id}/run`,
                      { method: "POST" },
                    ),
                  );
                  refresh();
                })
              }
            >
              Run scenario
            </button>
          </div>
        ))}
      </div>
    </section>
  );
}
