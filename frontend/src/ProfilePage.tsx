import { useEffect, useState, type DragEvent, type FormEvent } from 'react';
import { api, type AiStatus, type Company, type DiscoveryRun, type ImportSummary, type Preferences, type Profile } from './api';
import { csv, timeAgo } from './ui';

type Props = {
  profile: Profile | null;
  onSaved: (p: Profile, isNew: boolean) => void;
  run: <T>(label: string, fn: () => Promise<T>) => Promise<T | undefined>;
  busy: boolean;
  notify: (message: string) => void;
  isAdmin: boolean;
};

const emptyProfile: Omit<Profile, 'id'> = {
  firstName: '', lastName: '', email: '', phone: '', city: '', state: '', country: 'US',
  linkedInUrl: '', resumePath: '', summary: '', skillsCsv: '', yearsOfExperience: null,
};

export default function ProfilePage({ profile, onSaved, run, busy, notify, isAdmin }: Props) {
  const [form, setForm] = useState<Omit<Profile, 'id'>>(profile ?? emptyProfile);
  const [newSkill, setNewSkill] = useState('');
  const [prefs, setPrefs] = useState<Preferences | null>(null);
  const [companies, setCompanies] = useState<Company[]>([]);
  const [runs, setRuns] = useState<DiscoveryRun[]>([]);
  const [companyFilter, setCompanyFilter] = useState('');
  const [newCompanyUrl, setNewCompanyUrl] = useState('');
  const [pendingResume, setPendingResume] = useState<File | null>(null);
  const [autofilled, setAutofilled] = useState<Set<keyof Omit<Profile, 'id'>>>(new Set());
  const [dragging, setDragging] = useState(false);
  const [importSummary, setImportSummary] = useState<ImportSummary | null>(null);
  const [importView, setImportView] = useState<'Added' | 'NotFound' | 'Exists'>('Added');
  const [recentIds, setRecentIds] = useState<Set<string>>(new Set());
  const [showOnlyRecent, setShowOnlyRecent] = useState(false);
  const [adding, setAdding] = useState(false);
  const [addStatus, setAddStatus] = useState<{ ok: boolean; text: string } | null>(null);
  const [ai, setAi] = useState<AiStatus | null>(null);
  const [adzuna, setAdzuna] = useState<{ configured: boolean; appId: string | null } | null>(null);
  const [adzunaId, setAdzunaId] = useState('');
  const [adzunaKey, setAdzunaKey] = useState('');
  const [adzunaSaving, setAdzunaSaving] = useState(false);
  const [adzunaMessage, setAdzunaMessage] = useState<{ ok: boolean; text: string } | null>(null);

  useEffect(() => { api.adzunaStatus().then(setAdzuna).catch(() => setAdzuna(null)); }, []);

  async function saveAdzuna(appId: string, appKey: string) {
    setAdzunaSaving(true);
    setAdzunaMessage(null);
    try {
      const status = await api.saveAdzuna(appId, appKey);
      setAdzuna(status);
      setAdzunaId('');
      setAdzunaKey('');
      setAdzunaMessage({ ok: true, text: status.configured
        ? '✓ Connected. Adzuna jobs are included from the next scan. Click "Save & scan now" in Job preferences to run one.'
        : 'Adzuna disconnected.' });
    } catch (err) {
      setAdzunaMessage({ ok: false, text: err instanceof Error ? err.message : String(err) });
    } finally {
      setAdzunaSaving(false);
    }
  }
  const [aiKey, setAiKey] = useState('');
  const [aiMessage, setAiMessage] = useState<{ ok: boolean; text: string } | null>(null);

  useEffect(() => { api.aiStatus().then(setAi).catch(() => setAi(null)); }, []);

  async function saveAiKey(key: string | null) {
    setAiMessage(null);
    try {
      const status = await api.saveAiKey(key);
      setAi(status);
      setAiKey('');
      setAiMessage({ ok: true, text: key ? '✓ Key saved. Open any job and click "✨ Tailor resume".' : 'Key removed.' });
    } catch (err) {
      setAiMessage({ ok: false, text: err instanceof Error ? err.message : String(err) });
    }
  }

  useEffect(() => { setForm(profile ?? emptyProfile); }, [profile]);

  useEffect(() => {
    if (!profile) return;
    run('Loading', async () => {
      const [p, c, r] = await Promise.all([api.preferences(profile.id), api.companies(), api.discoveryRuns(profile.id)]);
      setPrefs(p); setCompanies(c); setRuns(r);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profile?.id]);

  const set = <K extends keyof typeof form>(k: K, v: (typeof form)[K]) => setForm((f) => ({ ...f, [k]: v }));
  const skills = csv(form.skillsCsv);

  function addSkill() {
    const s = newSkill.trim();
    if (!s || skills.some((x) => x.toLowerCase() === s.toLowerCase())) { setNewSkill(''); return; }
    set('skillsCsv', [...skills, s].join(', '));
    setNewSkill('');
  }

  async function saveProfile(e: FormEvent) {
    e.preventDefault();
    const isNew = !profile;
    let saved = await run(isNew ? 'Creating your profile' : 'Saving profile and re-scoring jobs', () =>
      profile ? api.updateProfile({ ...form, id: profile.id }) : api.createProfile(form));
    if (!saved) return;
    // Attach the resume picked before the profile existed, so applications can upload it.
    if (isNew && pendingResume) {
      const created = saved;
      const upload = await run('Attaching your resume', () => api.uploadResume(created.id, pendingResume));
      if (upload) saved = upload.profile;
      setPendingResume(null);
    }
    setAutofilled(new Set());
    onSaved(saved, isNew);
    notify(isNew ? 'Profile created. Now set your job preferences below; the first scan has started.' : 'Profile saved. Match scores updated.');
  }

  async function handleResume(file: File | undefined) {
    if (!file) return;
    if (!/\.(pdf|docx)$/i.test(file.name)) { notify('Please use a .pdf or .docx resume so it can be read.'); return; }
    if (profile) await uploadResume(file);
    else await prefillFromResume(file);
  }

  // New profile: parse without saving and fill the empty form fields for review.
  async function prefillFromResume(file: File) {
    const parsed = await run('Reading your resume', () => api.parseResume(file));
    if (!parsed) return;
    const filled = new Set<keyof typeof form>();
    const next = { ...form };
    const fill = <K extends keyof typeof form>(k: K, v: (typeof form)[K] | '' | null) => {
      if (v !== '' && v !== null && (next[k] === '' || next[k] === null || (k === 'country' && next[k] === 'US'))) { next[k] = v as (typeof form)[K]; filled.add(k); }
    };
    fill('firstName', parsed.firstName); fill('lastName', parsed.lastName); fill('email', parsed.email);
    fill('phone', parsed.phone); fill('city', parsed.city); fill('state', parsed.state); fill('country', parsed.country);
    fill('linkedInUrl', parsed.linkedInUrl); fill('yearsOfExperience', parsed.yearsOfExperience); fill('summary', parsed.summary);
    const mergedSkills = [...skills, ...parsed.skills.filter((s) => !skills.some((x) => x.toLowerCase() === s.toLowerCase()))];
    if (mergedSkills.length > skills.length) { next.skillsCsv = mergedSkills.join(', '); filled.add('skillsCsv'); }
    setForm(next);
    setAutofilled(filled);
    setPendingResume(file);
    notify(filled.size > 0
      ? `Filled ${filled.size} field${filled.size === 1 ? '' : 's'} from your resume (highlighted in green). Review them, then click Create profile.`
      : 'We couldn\'t find details in that resume. Please fill the form manually.');
  }

  // Existing profile: upload, fill empty fields server-side, re-score jobs.
  async function uploadResume(file: File) {
    if (!profile) return;
    const result = await run('Reading your resume and re-scoring jobs', () => api.uploadResume(profile.id, file));
    if (!result) return;
    onSaved(result.profile, false);
    const parts = [];
    if (result.filledFields.length) parts.push(`filled ${result.filledFields.join(', ')}`);
    if (result.addedSkills.length) parts.push(`added ${result.addedSkills.length} skills (${result.addedSkills.join(', ')})`);
    notify(!result.textExtracted
      ? 'Resume saved, but its text could not be read (is it a scanned image?). Fill your details manually.'
      : parts.length ? `Resume updated: ${parts.join('; ')}.` : 'Resume updated. Your profile already had everything we found.');
  }

  function onDrop(e: DragEvent<HTMLLabelElement>) {
    e.preventDefault();
    setDragging(false);
    handleResume(e.dataTransfer.files?.[0]);
  }

  const af = (k: keyof typeof form) => (autofilled.has(k) ? 'autofilled' : undefined);

  async function savePrefs(e: FormEvent) {
    e.preventDefault();
    if (!profile || !prefs) return;
    const saved = await run('Saving preferences', () => api.savePreferences(profile.id, prefs));
    if (!saved) return;
    setPrefs(saved);
    await run('Starting a scan', () => api.runDiscovery(profile.id));
    notify('Preferences saved. Scanning company career pages now; new jobs will appear on the Jobs tab in a few minutes.');
  }

  async function importFile(file: File | undefined) {
    if (!file) return;
    if (!/\.(xlsx|csv)$/i.test(file.name)) { notify('Please upload an .xlsx or .csv file. For an old .xls file, use Save As → Excel Workbook first.'); return; }
    const summary = await run(`Importing companies from ${file.name}. Checking each company's careers page can take a minute or two`,
      () => api.importCompanies(file));
    if (!summary) return;
    setImportSummary(summary);
    setImportView(summary.added > 0 ? 'Added' : 'NotFound');
    markRecent(summary.results.filter((r) => r.status === 'Added' && r.companyId).map((r) => r.companyId!));
    setCompanies(await api.companies());
    notify(`Imported ${summary.added} new compan${summary.added === 1 ? 'y' : 'ies'}` +
      (summary.notFound ? `; ${summary.notFound} couldn't be added (reasons are in the import results).` : '.'));
  }

  function markRecent(ids: string[]) {
    setRecentIds((prev) => new Set([...ids, ...prev]));
  }

  // Reports inline, next to the input, since the page-level banner is off-screen down here.
  async function addCompany(e: FormEvent) {
    e.preventDefault();
    setAdding(true);
    setAddStatus(null);
    try {
      const { company, message } = await api.addCompany(newCompanyUrl);
      setNewCompanyUrl('');
      markRecent([company.id]);
      setCompanyFilter('');
      setCompanies(await api.companies());
      setAddStatus({ ok: true, text: `✓ Added ${company.name} (${company.atsProvider}). ${message} It's at the top of the list below, marked NEW.` });
    } catch (err) {
      setAddStatus({ ok: false, text: `✕ Not added: ${err instanceof Error ? err.message : String(err)}` });
    } finally {
      setAdding(false);
    }
  }

  async function toggleCompany(c: Company) {
    setCompanies((list) => list.map((x) => (x.id === c.id ? { ...x, enabled: !c.enabled } : x)));
    await run('Updating', () => api.setCompanyEnabled(c.id, !c.enabled));
  }

  async function removeCompany(c: Company) {
    setCompanies((list) => list.filter((x) => x.id !== c.id));
    await run('Removing', () => api.removeCompany(c.id));
  }

  const setP = <K extends keyof Preferences>(k: K, v: Preferences[K]) => setPrefs((p) => (p ? { ...p, [k]: v } : p));
  const visibleCompanies = companies
    .filter((c) => (showOnlyRecent ? recentIds.has(c.id) : `${c.name} ${c.atsProvider}`.toLowerCase().includes(companyFilter.toLowerCase())))
    .sort((a, b) => Number(recentIds.has(b.id)) - Number(recentIds.has(a.id)));
  const importRows = importSummary?.results.filter((r) => r.status === importView) ?? [];

  return (
    <div className="profile-page">
      <section className="card">
        <h2>{profile ? 'Your profile' : 'Create your profile'}</h2>
        <label className={`resume-drop ${dragging ? 'dragging' : ''}`}
          onDragOver={(e) => { e.preventDefault(); setDragging(true); }} onDragLeave={() => setDragging(false)} onDrop={onDrop}>
          <input type="file" accept=".pdf,.docx" onChange={(e) => { handleResume(e.target.files?.[0]); e.target.value = ''; }} />
          <span className="resume-icon" aria-hidden="true">📄</span>
          <span>
            <strong>
              {pendingResume ? pendingResume.name
                : profile?.resumePath ? profile.resumePath.split(/[\\/]/).pop()
                : 'Upload your resume to autofill'}
            </strong>
            <span className="small muted">
              {profile
                ? (profile.resumePath
                  ? 'Drop a new resume to replace it. Empty fields and new skills are filled in automatically.'
                  : 'Drop your resume (.pdf or .docx) to fill empty fields and add your skills.')
                : pendingResume
                  ? 'Details below were filled from this resume. Drop another file to try again.'
                  : 'Drag and drop a .pdf or .docx, or click to choose. We fill in your details and skills; you review before saving.'}
            </span>
          </span>
        </label>
        {autofilled.size > 0 && <p className="small autofill-note">Fields highlighted in green were filled from your resume. Please check them.</p>}
        <form onSubmit={saveProfile} className="grid">
          <label>First name<input required className={af('firstName')} value={form.firstName} onChange={(e) => set('firstName', e.target.value)} /></label>
          <label>Last name<input required className={af('lastName')} value={form.lastName} onChange={(e) => set('lastName', e.target.value)} /></label>
          <label>Email<input required type="email" className={af('email')} value={form.email} onChange={(e) => set('email', e.target.value)} /></label>
          <label>Phone<input className={af('phone')} value={form.phone} onChange={(e) => set('phone', e.target.value)} /></label>
          <label>City<input className={af('city')} value={form.city} onChange={(e) => set('city', e.target.value)} /></label>
          <label>State<input className={af('state')} value={form.state} onChange={(e) => set('state', e.target.value)} /></label>
          <label>Country<input className={af('country')} value={form.country} onChange={(e) => set('country', e.target.value)} /></label>
          <label>LinkedIn URL<input className={af('linkedInUrl')} value={form.linkedInUrl} onChange={(e) => set('linkedInUrl', e.target.value)} /></label>
          <label>Years of experience
            <input type="number" min={0} max={50} className={af('yearsOfExperience')} value={form.yearsOfExperience ?? ''}
              onChange={(e) => set('yearsOfExperience', e.target.value === '' ? null : Number(e.target.value))} />
          </label>
          <div className="wide">
            <div className="label small">Skills <span className="muted">({skills.length}), used to score every job</span></div>
            <div className={`chips editable ${af('skillsCsv') ?? ''}`}>
              {skills.map((s) => (
                <span key={s} className="chip ok">
                  {s}
                  <button type="button" aria-label={`Remove ${s}`} onClick={() => set('skillsCsv', skills.filter((x) => x !== s).join(', '))}>×</button>
                </span>
              ))}
              <input className="chip-input" placeholder="Add a skill and press Enter" value={newSkill}
                onChange={(e) => setNewSkill(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); addSkill(); } }}
                onBlur={addSkill} />
            </div>
          </div>
          <label className="wide">Summary<textarea rows={3} className={af('summary')} value={form.summary} onChange={(e) => set('summary', e.target.value)} /></label>
          <div className="wide row">
            <button type="submit" className="primary" disabled={busy}>{profile ? 'Save profile' : 'Create profile'}</button>
          </div>
        </form>
      </section>

      {profile && isAdmin && (
        <section className="card">
          <div className="section-head">
            <h2>Job search APIs · Adzuna</h2>
            <span className={`pill ${adzuna?.configured ? 'Submitted' : ''}`}>{adzuna?.configured ? `Connected (${adzuna.appId})` : 'Not connected'}</span>
          </div>
          <p className="small muted">
            Adds jobs from Adzuna, which aggregates listings from thousands of US employers and job boards (many of the same
            jobs you see on LinkedIn). They go through your job titles, locations, level, federal and blocked-company filters like
            everything else. Get a free App ID and App Key at <a href="https://developer.adzuna.com/signup" target="_blank" rel="noreferrer">developer.adzuna.com</a>.
            The keys are stored only on this computer.
          </p>
          <form className="row" onSubmit={(e) => { e.preventDefault(); saveAdzuna(adzunaId, adzunaKey); }}>
            <input style={{ flex: 1, minWidth: 160 }} placeholder="App ID" value={adzunaId} onChange={(e) => setAdzunaId(e.target.value)} autoComplete="off" />
            <input style={{ flex: 2, minWidth: 200 }} type="password" placeholder="App Key" value={adzunaKey} onChange={(e) => setAdzunaKey(e.target.value)} autoComplete="off" />
            <button type="submit" className="primary" disabled={adzunaSaving || !adzunaId.trim() || !adzunaKey.trim()}>
              {adzunaSaving ? <><span className="spinner" /> Checking…</> : adzuna?.configured ? 'Replace keys' : 'Connect'}
            </button>
            {adzuna?.configured && <button type="button" className="secondary" onClick={() => saveAdzuna('', '')}>Disconnect</button>}
          </form>
          {adzunaMessage && <div className={`add-status ${adzunaMessage.ok ? 'ok' : 'bad'}`}>{adzunaMessage.text}</div>}
        </section>
      )}

      {profile && (
        <section className="card">
          <div className="section-head">
            <h2>AI resume tailoring</h2>
            <span className={`pill ${ai?.configured ? 'Submitted' : ''}`}>{ai?.configured ? `Connected ${ai.maskedKey ?? ''}` : 'Not connected'}</span>
          </div>
          <p className="small muted">
            Rewrites the summary and tech stack of your resume for a specific job using Claude, keeping your real experience and
            your resume's format, and scores it against the job like an ATS.
            {isAdmin && ' Needs an Anthropic API key (from console.anthropic.com → API keys); each tailored resume costs a few cents. The key is stored on the server, not in the project.'}
          </p>
          {!isAdmin ? (
            <p className="small">
              {ai?.configured ? 'Included with your account.' : 'Not set up yet. Ask the person who runs this app to add the AI key.'}
              {ai?.monthlyLimit != null && ` You've used ${ai.usedThisMonth} of ${ai.monthlyLimit} tailorings this month.`}
            </p>
          ) : ai?.configured && ai.source !== 'saved in this app' ? (
            <p className="small">Using the key from your {ai.source}.</p>
          ) : (
            <form className="row" onSubmit={(e) => { e.preventDefault(); saveAiKey(aiKey); }}>
              <input type="password" autoComplete="off" style={{ flex: 1, minWidth: 220 }} placeholder={ai?.configured ? 'Paste a new key to replace it' : 'sk-ant-…'}
                value={aiKey} onChange={(e) => setAiKey(e.target.value)} />
              <button type="submit" className="primary" disabled={!aiKey.trim()}>Save key</button>
              {ai?.configured && <button type="button" className="secondary" onClick={() => saveAiKey(null)}>Remove</button>}
            </form>
          )}
          {aiMessage && <div className={`add-status ${aiMessage.ok ? 'ok' : 'bad'}`}>{aiMessage.text}</div>}
        </section>
      )}

      {profile && prefs && (
        <section className="card">
          <div className="section-head">
            <h2>Job preferences</h2>
            <label className="check">
              <input type="checkbox" checked={prefs.enabled} onChange={(e) => setP('enabled', e.target.checked)} />
              Scan automatically
            </label>
          </div>
          <form onSubmit={savePrefs} className="grid">
            <label className="wide">Main tech stack <span className="muted">(comma separated; jobs built on these rank first and are searched first)</span>
              <input value={prefs.primarySkillsCsv} onChange={(e) => setP('primarySkillsCsv', e.target.value)} placeholder=".NET, C#, ASP.NET, Angular, React, SQL Server, Azure" />
            </label>
            <label className="wide">Job titles to look for <span className="muted">(comma separated, matched against the title)</span>
              <input value={prefs.roleKeywordsCsv} onChange={(e) => setP('roleKeywordsCsv', e.target.value)} placeholder="software engineer, .net developer, full stack" />
            </label>
            <label className="wide">Skip titles containing
              <input value={prefs.excludeKeywordsCsv} onChange={(e) => setP('excludeKeywordsCsv', e.target.value)} placeholder="manager, director, intern" />
            </label>
            <label className="wide">Locations <span className="muted">(comma separated; include "remote" for remote roles)</span>
              <input value={prefs.locationsCsv} onChange={(e) => setP('locationsCsv', e.target.value)} placeholder="remote, united states, texas" />
            </label>
            <label>Experience level
              <select value={prefs.experienceLevel} onChange={(e) => setP('experienceLevel', e.target.value as Preferences['experienceLevel'])}>
                <option value="Any">Any</option>
                <option value="Entry">Entry / junior</option>
                <option value="Mid">Mid-level</option>
                <option value="Senior">Senior</option>
              </select>
            </label>
            <label>Scan every
              <select value={prefs.scanEveryHours} onChange={(e) => setP('scanEveryHours', Number(e.target.value))}>
                {[1, 2, 3, 6, 12, 24].map((h) => <option key={h} value={h}>{h} hour{h > 1 ? 's' : ''}</option>)}
              </select>
            </label>
            <label>Keep jobs posted within
              <select value={prefs.maxAgeHours} onChange={(e) => setP('maxAgeHours', Number(e.target.value))}>
                <option value={24}>24 hours</option>
                <option value={72}>3 days</option>
                <option value={168}>1 week</option>
              </select>
            </label>
            <div className="stack">
              <label className="check"><input type="checkbox" checked={prefs.remoteOnly} onChange={(e) => setP('remoteOnly', e.target.checked)} /> Remote only</label>
              <label className="check"><input type="checkbox" checked={prefs.needsSponsorship} onChange={(e) => setP('needsSponsorship', e.target.checked)} /> I need visa sponsorship</label>
              <label className="check"><input type="checkbox" checked={prefs.excludeFederalJobs} onChange={(e) => setP('excludeFederalJobs', e.target.checked)} /> Skip federal government &amp; security-clearance jobs</label>
            </div>
            <label className="wide">Never show jobs from <span className="muted">(comma separated; applies to every source, including Adzuna)</span>
              <textarea rows={3} value={prefs.blockedCompaniesCsv} onChange={(e) => setP('blockedCompaniesCsv', e.target.value)}
                placeholder="Google, Meta, Tesla, Boeing" />
            </label>
            <div className="wide row">
              <button type="submit" className="primary" disabled={busy}>Save &amp; scan now</button>
              {runs[0] && (
                <span className="small muted">
                  Last scan {timeAgo(runs[0].startedAt)}: {runs[0].companiesScanned} companies, {runs[0].jobsSeen.toLocaleString()} open roles,
                  {' '}{runs[0].jobsSaved} new matches{runs[0].companiesFailed > 0 && `, ${runs[0].companiesFailed} sites unreachable`}.
                </span>
              )}
            </div>
          </form>
        </section>
      )}

      {profile && (
        <section className="card">
          <div className="section-head">
            <h2>Company career pages</h2>
            <span className="small muted">{companies.filter((c) => c.enabled).length} of {companies.length} active</span>
          </div>
          <form onSubmit={addCompany} className="row">
            <input style={{ flex: 1, minWidth: 220 }} required value={newCompanyUrl}
              onChange={(e) => { setNewCompanyUrl(e.target.value); setAddStatus(null); }}
              placeholder="Paste any company careers page link" />
            <button type="submit" className="primary" disabled={busy || adding}>
              {adding ? <><span className="spinner" /> Checking…</> : 'Add company'}
            </button>
          </form>
          {adding && <p className="small muted">Checking the page for a job board or job listings; this can take up to 30 seconds…</p>}
          {addStatus && <div className={`add-status ${addStatus.ok ? 'ok' : 'bad'}`} role="status">{addStatus.text}</div>}
          {!addStatus && !adding && (
            <p className="small muted">
              Works with job boards (Greenhouse, Lever, Ashby, SmartRecruiters, Workday) and most company or staffing-agency careers pages,
              e.g. https://boards.greenhouse.io/stripe or https://motionrecruitment.com/tech-jobs
            </p>
          )}

          {isAdmin && <div className="import-box">
            <div>
              <strong>Import from Excel or CSV</strong>
              <div className="small muted">
                One company per row: a careers link (any column), a company name, or both. We find each company's job board and add the ones we can scan.
              </div>
            </div>
            <label className={`button secondary ${busy ? 'disabled' : ''}`}>
              <input type="file" accept=".xlsx,.csv" hidden disabled={busy}
                onChange={(e) => { importFile(e.target.files?.[0]); e.target.value = ''; }} />
              ⬆ Upload .xlsx / .csv
            </label>
          </div>}
          {importSummary && (
            <div className="import-results">
              <div className="row">
                <strong>Import finished:</strong>
                {([['Added', `✓ ${importSummary.added} added`], ['NotFound', `✕ ${importSummary.notFound} not added`], ['Exists', `${importSummary.existing} already in list`]] as const).map(([key, label]) => (
                  <button key={key} type="button" className={`chip-btn ${key} ${importView === key ? 'active' : ''}`} onClick={() => setImportView(key)}>{label}</button>
                ))}
                {importSummary.added > 0 && (
                  <button type="button" className="link-btn small" onClick={() => { setShowOnlyRecent(true); setCompanyFilter(''); }}>Show added companies in the list ↓</button>
                )}
                <button type="button" className="link-btn small" onClick={() => setImportSummary(null)}>Hide</button>
              </div>
              {importSummary.truncated && <p className="small muted">Only the first 1,000 rows were imported.</p>}
              {importRows.length === 0 ? <p className="small muted">None.</p> : (
                <div className="import-table">
                  <table>
                    <colgroup><col style={{ width: '3.5rem' }} /><col style={{ width: '38%' }} /><col /></colgroup>
                    <thead><tr><th>Row</th><th>From your sheet</th><th>{importView === 'NotFound' ? 'Why it wasn\'t added' : 'Result'}</th></tr></thead>
                    <tbody>
                      {importRows.map((r) => (
                        <tr key={r.row}>
                          <td className="small muted">{r.row}</td>
                          <td className="small">
                            {r.company && r.company !== r.input && <div><strong>{r.company}</strong></div>}
                            <div className="muted">{r.input}</div>
                          </td>
                          <td className="small">
                            {r.status === 'Added' && <span className="ok-text">✓ Added{r.provider ? ` · ${r.provider}` : ''}</span>}
                            {r.status === 'NotFound' && <span className="error-text">✕ Not added</span>}
                            {r.status === 'Exists' && <span className="muted">Already in list{r.provider ? ` · ${r.provider}` : ''}</span>}
                            <div className="muted">{r.message}</div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
          {showOnlyRecent ? (
            <div className="row recent-bar">
              <span className="small">Showing the {visibleCompanies.length} compan{visibleCompanies.length === 1 ? 'y' : 'ies'} you just added.</span>
              <button type="button" className="link-btn small" onClick={() => setShowOnlyRecent(false)}>Show all companies</button>
            </div>
          ) : (
            <input placeholder="Filter companies" value={companyFilter} onChange={(e) => setCompanyFilter(e.target.value)} style={{ width: '100%', margin: '8px 0' }} />
          )}
          <div className="company-grid">
            {visibleCompanies.map((c) => (
              <div key={c.id} className={`company ${c.enabled ? '' : 'off'} ${recentIds.has(c.id) ? 'recent' : ''}`}>
                <label className="check">
                  <input type="checkbox" checked={c.enabled} disabled={!isAdmin} onChange={() => toggleCompany(c)} />
                  <span title={c.name}>{c.name}</span>
                </label>
                {recentIds.has(c.id) && <span className="new-badge">NEW</span>}
                <span className="small muted" title={c.lastJobCount != null ? `${c.lastJobCount} open jobs at the last scan (before your filters)` : 'Not scanned yet'}>
                  {c.atsProvider}{c.lastJobCount != null && ` · ${c.lastJobCount} jobs`}
                </span>
                {c.lastError && <span className="small error-text" title={c.lastError}>unreachable</span>}
                {isAdmin && <button className="link-btn small" onClick={() => removeCompany(c)} aria-label={`Remove ${c.name}`}>Remove</button>}
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
