import { useCallback, useEffect, useState } from 'react';
import { api, auth, setUnauthorizedHandler, type Application, type AuthUser, type FeedJob, type Profile, type Session } from './api';
import AuthPage from './AuthPage';
import JobFeed from './JobFeed';
import ProfilePage from './ProfilePage';
import { timeAgo } from './ui';

type Tab = 'jobs' | 'saved' | 'applied' | 'profile';

// Shows the login screen until there is a valid session, then the app itself.
export default function App() {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [checking, setChecking] = useState(!!auth.token());

  useEffect(() => { setUnauthorizedHandler(() => setUser(null)); }, []);
  useEffect(() => {
    if (!auth.token()) return;
    api.me().then(setUser).catch(() => auth.clear()).finally(() => setChecking(false));
  }, []);

  if (checking) return <main><p className="muted">Loading…</p></main>;
  if (!user) return <AuthPage onSignedIn={setUser} />;
  return <Workspace key={user.id} user={user} onLogout={() => { auth.clear(); setUser(null); }} />;
}

function Workspace({ user, onLogout }: { user: AuthUser; onLogout: () => void }) {
  const [tab, setTab] = useState<Tab>('jobs');
  const [profile, setProfile] = useState<Profile | null>(null);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [toast, setToast] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [session, setSession] = useState<Session | null>(null);
  const [approved, setApproved] = useState(false);
  const [applications, setApplications] = useState<Application[]>([]);

  const showError = useCallback((message: string) => setError(message), []);

  const run = useCallback(async <T,>(label: string, fn: () => Promise<T>): Promise<T | undefined> => {
    setBusy(label);
    setError(null);
    try {
      return await fn();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }, []);

  useEffect(() => {
    run('Loading', async () => {
      const profiles = await api.profiles();
      setProfile(profiles[0] ?? null);
      // A new account has no resume yet: send them straight to the profile to upload it.
      if (!profiles.length || !profiles[0].resumePath) setTab('profile');
    }).finally(() => setLoaded(true));
  }, [run]);

  useEffect(() => {
    if (tab === 'applied') run('Loading applications', async () => setApplications(await api.applications()));
  }, [tab, run]);

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), 6000);
    return () => clearTimeout(t);
  }, [toast]);

  function onProfileSaved(p: Profile, isNew: boolean) {
    const firstResume = !!p.resumePath && !profile?.resumePath;
    setProfile(p);
    if (isNew || firstResume) {
      // Save default preferences so scheduled scans pick this profile up, then start the first scan.
      run('Starting your first scan', async () => {
        await api.savePreferences(p.id, await api.preferences(p.id));
        await api.runDiscovery(p.id);
      });
    }
  }

  async function apply(job: FeedJob) {
    if (!profile) return;
    setApproved(false);
    const s = await run('Opening the application in a browser window and filling what we know', () =>
      api.startAutomation(job.applyUrl, profile.id, job.id));
    if (s) setSession(s);
  }

  async function unmarkApplied(a: Application) {
    const ok = await run('Updating', () => api.markApplied(a.jobPostingId, false).then(() => true));
    if (ok) setApplications((list) => list.filter((x) => x.id !== a.id));
  }

  async function submit() {
    if (!session) return;
    const s = await run('Submitting', () => api.submit(session.sessionId));
    if (s) setSession(s);
    setApproved(false);
  }

  async function closeSession() {
    if (!session) return;
    await run('Closing browser', () => api.closeSession(session.sessionId));
    setSession(null);
  }

  const canSubmit = session && session.status !== 'Submitted' && session.status !== 'Failed';
  const tabs: [Tab, string][] = [['jobs', 'Jobs'], ['saved', 'Saved'], ['applied', 'Applied'], ['profile', 'Profile']];

  return (
    <>
      <header className="topnav">
        <div className="brand"><span className="logo">J</span> JobAgent</div>
        <nav>
          {tabs.map(([key, label]) => (
            <button key={key} className={`tab ${tab === key ? 'active' : ''}`} disabled={!profile && key !== 'profile'} onClick={() => setTab(key)}>
              {label}
            </button>
          ))}
        </nav>
        <div className="nav-right small muted">
          <span title={user.email}>{profile && (profile.firstName || profile.lastName) ? `${profile.firstName} ${profile.lastName}` : user.email}</span>
          <button className="link-btn small" onClick={onLogout}>Log out</button>
        </div>
      </header>

      <main className={tab === 'jobs' || tab === 'saved' ? 'wide-main' : ''}>
        {error && <div className="banner error" role="alert">{error}<button className="link-btn" onClick={() => setError(null)}>Dismiss</button></div>}
        {busy && busy !== 'Loading' && <div className="banner"><span className="spinner" /> {busy}…</div>}

        {loaded && profile && (tab === 'jobs' || tab === 'saved') && (
          <JobFeed key={tab} profile={profile} view={tab === 'saved' ? 'saved' : 'all'} onApply={apply} onError={showError} onGoToProfile={() => setTab('profile')} busy={!!busy} />
        )}

        {loaded && tab === 'profile' && (
          <ProfilePage profile={profile} onSaved={onProfileSaved} run={run} busy={!!busy} notify={setToast} isAdmin={user.isAdmin} />
        )}

        {loaded && tab === 'applied' && (
          <section className="card">
            <h2>Applications</h2>
            {applications.length === 0 ? <p className="muted">Jobs you apply to with "Apply", or mark as "already applied", are tracked here.</p> : (
              <div className="table-wrap">
                <table>
                  <thead><tr><th>Job</th><th>Status</th><th>ATS</th><th>Updated</th><th></th></tr></thead>
                  <tbody>
                    {applications.map((a) => (
                      <tr key={a.id}>
                        <td><a href={a.applyUrl} target="_blank" rel="noreferrer">{a.jobTitle}</a><div className="muted small">{a.company}</div></td>
                        <td><span className={`pill ${a.status}`}>{a.status.replace(/([a-z])([A-Z])/g, '$1 $2')}</span></td>
                        <td className="small">{a.atsProvider}</td>
                        <td className="small">{timeAgo(a.updatedAt)}</td>
                        <td><button className="icon-btn" title="Remove from Applied and show this job in your feed again" onClick={() => unmarkApplied(a)}>Undo</button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        )}
      </main>

      {session && (
        <div className="session-dock" role="dialog" aria-label="Application in progress">
          <div className="section-head">
            <strong>Application in progress · {session.atsProvider}</strong>
            <span className={`pill ${session.status}`}>{session.status.replace(/([a-z])([A-Z])/g, '$1 $2')}</span>
          </div>
          {session.message && <p className="small">{session.message}</p>}
          {session.filledFields.length > 0 && <p className="small"><strong>Filled:</strong> {session.filledFields.join(', ')}</p>}
          {session.unknownFields.length > 0 && (
            <div className="banner warn small">
              <strong>Needs your input in the browser window:</strong>
              <ul>{session.unknownFields.map((f) => <li key={f}>{f}</li>)}</ul>
            </div>
          )}
          {canSubmit && (
            <label className="check small">
              <input type="checkbox" checked={approved} onChange={(e) => setApproved(e.target.checked)} />
              I reviewed the form in the browser and approve submitting it
            </label>
          )}
          <div className="row">
            <button className="secondary sm" disabled={!!busy} onClick={() => run('Checking the form', async () => setSession(await api.session(session.sessionId)))}>Re-check</button>
            {canSubmit && <button className="danger sm" disabled={!!busy || !approved} onClick={submit}>Submit application</button>}
            <button className="secondary sm" disabled={!!busy} onClick={closeSession}>Close</button>
          </div>
        </div>
      )}

      {toast && <div className="toast" role="status">{toast}</div>}
    </>
  );
}
