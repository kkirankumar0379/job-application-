import { useCallback, useEffect, useRef, useState } from 'react';
import { api, type Feed, type FeedJob, type FeedQuery, type JobDetail, type Profile } from './api';
import TailorPanel from './TailorPanel';
import { Bar, CompanyAvatar, MatchRing, csv, postedLabel, scoreLabel, timeAgo } from './ui';

type Props = {
  profile: Profile;
  view: 'all' | 'saved';
  onApply: (job: FeedJob) => void;
  onError: (message: string) => void;
  onGoToProfile: () => void;
  busy: boolean;
};

const FILTERS_KEY = 'jobagent.filters';
const defaultFilters: Omit<FeedQuery, 'view'> = { hours: 24, minScore: 0, remote: false, mainStack: false, q: '', sort: 'match', source: 'all' };

function loadFilters(): Omit<FeedQuery, 'view'> {
  try {
    const saved = { ...defaultFilters, ...JSON.parse(localStorage.getItem(FILTERS_KEY) ?? '{}'), q: '' };
    // Map earlier thresholds onto the current dropdown options so nothing lands on an unselectable value.
    saved.minScore = saved.minScore >= 80 ? 80 : saved.minScore >= 65 ? 65 : saved.minScore >= 50 ? 50 : 0;
    return saved;
  } catch {
    return defaultFilters;
  }
}

export default function JobFeed({ profile, view, onApply, onError, onGoToProfile, busy }: Props) {
  const [filters, setFilters] = useState(loadFilters);
  const [search, setSearch] = useState('');
  const [feed, setFeed] = useState<Feed | null>(null);
  const [loading, setLoading] = useState(true);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [detail, setDetail] = useState<JobDetail | null>(null);
  const [showDetailMobile, setShowDetailMobile] = useState(false);
  const [tailorJob, setTailorJob] = useState<FeedJob | null>(null);
  const detailCache = useRef(new Map<string, JobDetail>());

  const load = useCallback(async () => {
    try {
      const data = await api.feed(profile.id, { ...filters, view });
      setFeed(data);
      setSelectedId((current) => (current && data.jobs.some((j) => j.id === current) ? current : data.jobs[0]?.id ?? null));
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [profile.id, filters, view, onError]);

  // Profile edits re-score jobs server-side, so updatedAt is a dependency too.
  useEffect(() => {
    try { localStorage.setItem(FILTERS_KEY, JSON.stringify({ ...filters, q: '' })); } catch { /* storage unavailable */ }
    detailCache.current.clear();
    load();
  }, [load, filters, profile.updatedAt]);

  // Debounce the search box.
  useEffect(() => {
    const t = setTimeout(() => setFilters((f) => (f.q === search ? f : { ...f, q: search })), 300);
    return () => clearTimeout(t);
  }, [search]);

  // While a scan runs, poll so new jobs appear when it finishes.
  const scanning = !!feed?.runningSince;
  useEffect(() => {
    if (!scanning) return;
    const t = setInterval(load, 3000);
    return () => clearInterval(t);
  }, [scanning, load]);

  useEffect(() => {
    if (!selectedId) { setDetail(null); return; }
    const cached = detailCache.current.get(selectedId);
    if (cached) { setDetail(cached); return; }
    let cancelled = false;
    api.job(selectedId).then((d) => {
      detailCache.current.set(d.id, d);
      if (!cancelled) setDetail(d);
    }).catch((e) => onError(e instanceof Error ? e.message : String(e)));
    return () => { cancelled = true; };
  }, [selectedId, onError]);

  async function scanNow() {
    try {
      await api.runDiscovery(profile.id);
      setFeed((f) => (f ? { ...f, runningSince: new Date().toISOString() } : f));
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e));
    }
  }

  function patchJob(id: string, patch: Partial<FeedJob>) {
    setFeed((f) => (f ? { ...f, jobs: f.jobs.map((j) => (j.id === id ? { ...j, ...patch } : j)) } : f));
    setDetail((d) => (d && d.id === id ? { ...d, ...patch } : d));
    const cached = detailCache.current.get(id);
    if (cached) detailCache.current.set(id, { ...cached, ...patch });
  }

  async function toggleSave(job: FeedJob) {
    patchJob(job.id, { isSaved: !job.isSaved });
    try {
      await api.saveJob(job.id, !job.isSaved);
      if (view === 'saved' && job.isSaved) removeJob(job.id);
    } catch (e) {
      patchJob(job.id, { isSaved: job.isSaved });
      onError(e instanceof Error ? e.message : String(e));
    }
  }

  function removeJob(id: string) {
    setFeed((f) => {
      if (!f) return f;
      const idx = f.jobs.findIndex((j) => j.id === id);
      const jobs = f.jobs.filter((j) => j.id !== id);
      if (selectedId === id) setSelectedId(jobs[Math.min(idx, jobs.length - 1)]?.id ?? null);
      return { ...f, jobs };
    });
  }

  async function dismiss(job: FeedJob) {
    removeJob(job.id);
    try { await api.dismissJob(job.id); } catch (e) { onError(e instanceof Error ? e.message : String(e)); load(); }
  }

  // "I already applied": the job leaves the feed and stays out after refreshes; undo it from the Applied tab.
  async function markApplied(job: FeedJob) {
    removeJob(job.id);
    try { await api.markApplied(job.id, true); } catch (e) { onError(e instanceof Error ? e.message : String(e)); load(); }
  }

  const set = <K extends keyof typeof filters>(k: K, v: (typeof filters)[K]) => setFilters((f) => ({ ...f, [k]: v }));
  const jobs = feed?.jobs ?? [];
  const selected = jobs.find((j) => j.id === selectedId) ?? null;
  const lastRun = feed?.lastRun;
  const hasSkills = csv(profile.skillsCsv).length > 0;

  return (
    <div className="feed-page">
      {tailorJob && <TailorPanel job={tailorJob} onClose={() => setTailorJob(null)} onGoToProfile={() => { setTailorJob(null); onGoToProfile(); }} />}
      <div className="feed-header">
        <div>
          <h1>{view === 'saved' ? 'Saved jobs' : 'Recommended for you'}</h1>
          <p className="muted small">
            {scanning ? (
              <span className="scanning"><span className="spinner" /> {feed?.progress
                ? <>Scanning… {feed.progress.companiesDone} of {feed.progress.companiesTotal} companies · {feed.progress.jobsSaved} new job{feed.progress.jobsSaved === 1 ? '' : 's'} so far (they appear as they're found)</>
                : <>Scanning company career pages… started {timeAgo(feed?.runningSince)}</>}</span>
            ) : lastRun ? (
              <>Updated {timeAgo(lastRun.finishedAt)} · {lastRun.companiesScanned} company career pages · {lastRun.jobsSeen.toLocaleString()} open roles checked</>
            ) : (
              <>No scan yet. Click <strong>Scan now</strong> to search company career pages.</>
            )}
          </p>
        </div>
        {view === 'all' && (
          <button className="secondary" onClick={scanNow} disabled={scanning}>{scanning ? 'Scanning…' : 'Scan now'}</button>
        )}
      </div>

      {view === 'all' && !scanning && lastRun?.funnelJson && <FunnelDetails json={lastRun.funnelJson} />}

      {!hasSkills && view === 'all' && (
        <div className="banner warn">Add your skills or upload your resume on the <strong>Profile</strong> tab. Match scores are based on them.</div>
      )}

      {view === 'all' && (
        <div className="source-tabs" role="tablist" aria-label="Job source">
          {([['all', 'All jobs'], ['boards', 'Company career pages'], ['universities', 'University career pages'], ['adzuna', 'Job boards']] as const).map(([key, label]) => (
            <button key={key} role="tab" aria-selected={filters.source === key}
              className={`source-tab ${filters.source === key ? 'active' : ''}`} onClick={() => set('source', key)}>
              {label} <span className="count-pill">{feed?.counts?.[key] ?? '–'}</span>
            </button>
          ))}
        </div>
      )}

      {view === 'all' && filters.source === 'universities' && (
        <p className="muted small">University and college job sites across the US, ranked by how well each role fits your skills, main stack and job titles (Profile tab). Turn on <strong>Main stack</strong> to keep only roles built on your primary stack.</p>
      )}

      <div className="filters">
        <input className="search" placeholder="Search title, company or skill" value={search} onChange={(e) => setSearch(e.target.value)} />
        {view === 'all' && (
          <>
            <select value={filters.hours} onChange={(e) => set('hours', Number(e.target.value))} aria-label="Posted within">
              <option value={24}>Past 24 hours</option>
              <option value={72}>Past 3 days</option>
              <option value={168}>Past week</option>
              <option value={720}>Past month</option>
            </select>
            <select value={filters.minScore} onChange={(e) => set('minScore', Number(e.target.value))} aria-label="Minimum match">
              <option value={0}>Any match</option>
              <option value={50}>Fair match (50%+)</option>
              <option value={65}>Good match (65%+)</option>
              <option value={80}>Strong match (80%+)</option>
            </select>
          </>
        )}
        <select value={filters.sort} onChange={(e) => set('sort', e.target.value as FeedQuery['sort'])} aria-label="Sort">
          <option value="match">Best match</option>
          <option value="recent">Most recent</option>
        </select>
        <label className={`toggle ${filters.mainStack ? 'on' : ''}`} title="Only jobs built on your main tech stack (Profile → Job preferences)">
          <input type="checkbox" checked={filters.mainStack} onChange={(e) => set('mainStack', e.target.checked)} /> Main stack
        </label>
        <label className={`toggle ${filters.remote ? 'on' : ''}`}>
          <input type="checkbox" checked={filters.remote} onChange={(e) => set('remote', e.target.checked)} /> Remote
        </label>
        <span className="muted small count">{jobs.length} job{jobs.length === 1 ? '' : 's'}</span>
      </div>

      <div className="feed-body">
        <div className="job-list">
          {loading && <div className="empty"><span className="spinner" /> Loading jobs…</div>}
          {!loading && jobs.length === 0 && (
            <div className="empty">
              {view === 'saved' ? 'Jobs you save will show up here.'
                : scanning ? 'Scanning… matching jobs appear here as each company is checked.'
                : 'No jobs match these filters yet. Try widening the time range or lowering the minimum match.'}
            </div>
          )}
          {jobs.map((job) => (
            <article key={job.id} className={`job-card ${job.id === selectedId ? 'selected' : ''}`}
              onClick={() => { setSelectedId(job.id); setShowDetailMobile(true); }}>
              <CompanyAvatar name={job.company} />
              <div className="job-main">
                <div className="job-meta small muted">
                  <span className="posted">{postedLabel(job.postedAt, job.atsProvider)}</span>
                  {job.isRemote && <span className="tag">Remote</span>}
                  {job.requiredYears && <span className="tag">{job.requiredYears}+ yrs</span>}
                </div>
                <h3>{job.title}</h3>
                <div className="small">{job.company} · <span className="muted">{shortLocation(job.location)}</span></div>
                {!!job.otherLocations?.length && (
                  <div className="small muted" title={job.otherLocations.join('; ')}>Also posted in {job.otherLocations.length} other location{job.otherLocations.length === 1 ? '' : 's'}</div>
                )}
                {job.matchedSkillsCsv && (
                  <div className="chips">
                    {csv(job.matchedSkillsCsv).slice(0, 4).map((s) => <span key={s} className="chip ok">{s}</span>)}
                    {csv(job.matchedSkillsCsv).length > 4 && <span className="chip">+{csv(job.matchedSkillsCsv).length - 4}</span>}
                  </div>
                )}
                <div className="card-actions" onClick={(e) => e.stopPropagation()}>
                  <button className="icon-btn" title={job.isSaved ? 'Unsave' : 'Save'} onClick={() => toggleSave(job)}>{job.isSaved ? '★ Saved' : '☆ Save'}</button>
                  <button className="icon-btn" title="Hide this job: you already applied" onClick={() => markApplied(job)}>✓ Already applied</button>
                  <button className="icon-btn" title="Not interested" onClick={() => dismiss(job)}>✕ Not interested</button>
                  <a className="icon-btn" href={job.applyUrl} target="_blank" rel="noreferrer" title={`Open this job on ${job.company}'s careers page`}>Company site ↗</a>
                  <button className="primary sm" disabled={busy} onClick={() => onApply(job)}>Apply</button>
                </div>
              </div>
              <div className="job-score">
                <MatchRing score={job.matchScore} />
                <span className="small muted">{scoreLabel(job.matchScore)}</span>
              </div>
            </article>
          ))}
        </div>

        <aside className={`job-detail ${showDetailMobile ? 'open' : ''}`}>
          {selected && (
            <>
              <button className="back-btn secondary" onClick={() => setShowDetailMobile(false)}>← Back to jobs</button>
              <div className="detail-head">
                <CompanyAvatar name={selected.company} size={56} />
                <div>
                  <div className="muted small">{selected.company}</div>
                  <h2>{selected.title}</h2>
                  <div className="job-meta small muted">
                    <span>{selected.location || 'Location not listed'}</span>
                    {selected.isRemote && <span className="tag">Remote</span>}
                    <span>Posted {postedLabel(selected.postedAt, selected.atsProvider).toLowerCase()}</span>
                    <span>via {selected.atsProvider}</span>
                  </div>
                </div>
              </div>
              <div className="detail-actions">
                <button className="primary" disabled={busy} onClick={() => onApply(selected)}>Autofill &amp; apply</button>
                <button className="secondary" onClick={() => setTailorJob(selected)}>✨ Tailor resume</button>
                <a className="button secondary" href={selected.applyUrl} target="_blank" rel="noreferrer">Apply on company site ↗</a>
                {selected.careersUrl && (
                  <a className="button secondary" href={selected.careersUrl} target="_blank" rel="noreferrer">All {selected.company} jobs ↗</a>
                )}
                <button className="secondary" onClick={() => toggleSave(selected)}>{selected.isSaved ? '★ Saved' : '☆ Save'}</button>
                <button className="secondary" onClick={() => markApplied(selected)}>✓ I already applied</button>
                <button className="secondary" onClick={() => dismiss(selected)}>Not interested</button>
              </div>

              <section className="match-box">
                <div className="match-summary">
                  <MatchRing score={selected.matchScore} size={88} />
                  <div>
                    <strong>{scoreLabel(selected.matchScore)}</strong>
                    <p className="small muted">{selected.matchReason}</p>
                  </div>
                </div>
                <div className="bars">
                  <Bar label="Skills" value={selected.skillScore} note="Languages & frameworks count most" />
                  {selected.stackScore > 0 && (
                    <Bar label="Main stack" value={selected.stackScore}
                      note={selected.stackScore >= 100 ? 'Title names your main stack' : selected.stackScore >= 55 ? 'Uses part of your main stack' : selected.stackScore >= 50 ? 'Stack not stated' : 'Built on other technologies'} />
                  )}
                  <Bar label="Title fit" value={selected.titleScore} note={selected.titleScore >= 100 ? 'Title names your stack' : selected.titleScore <= 15 ? 'A specialty outside your profile' : 'General software title'} />
                  <Bar label="Experience" value={selected.experienceScore}
                    note={selected.requiredYears
                      ? `Asks for ${selected.requiredYears}+ years${profile.yearsOfExperience != null ? `; you have ${profile.yearsOfExperience}` : ' (add your years on the Profile tab)'}`
                      : 'No explicit years requirement found'} />
                </div>
                {selected.matchedSkillsCsv && (
                  <div>
                    <div className="small label">Skills you have</div>
                    <div className="chips">{csv(selected.matchedSkillsCsv).map((s) => <span key={s} className="chip ok">✓ {s}</span>)}</div>
                  </div>
                )}
                {selected.missingSkillsCsv && (
                  <div>
                    <div className="small label">Skills to brush up on or highlight</div>
                    <div className="chips">{csv(selected.missingSkillsCsv).map((s) => <span key={s} className="chip miss">{s}</span>)}</div>
                  </div>
                )}
              </section>

              <section>
                <h3>Job description</h3>
                {detail?.id === selected.id
                  ? <div className="description">{detail.description || 'The company did not publish a description through its job board. Open the original posting to read it.'}</div>
                  : <div className="empty"><span className="spinner" /> Loading…</div>}
              </section>
            </>
          )}
          {!selected && !loading && <div className="empty">Select a job to see details.</div>}
        </aside>
      </div>
    </div>
  );
}

function shortLocation(location: string) {
  const first = location.split(';')[0].trim();
  const more = location.split(';').length - 1;
  return (first || 'Location not listed') + (more > 0 ? ` +${more}` : '');
}

type Funnel = {
  steps: { step: string; count: number }[];
  droppedAfterReadingJobPage: { reason: string; count: number }[];
};

/** "Why this many jobs?": how many of the roles checked survived each step of the last scan. */
function FunnelDetails({ json }: { json: string }) {
  let funnel: Funnel;
  try { funnel = JSON.parse(json); } catch { return null; }
  if (!funnel.steps?.length) return null;
  return (
    <details className="funnel">
      <summary className="small muted">Why this many jobs? See how the last scan narrowed things down</summary>
      <table>
        <tbody>
          {funnel.steps.map((s) => <tr key={s.step}><td className="num">{s.count.toLocaleString()}</td><td>{s.step}</td></tr>)}
        </tbody>
      </table>
      {funnel.droppedAfterReadingJobPage.length > 0 && (
        <p className="small muted">
          Dropped after reading the job page: {funnel.droppedAfterReadingJobPage.map((d) => `${d.count} ${d.reason}`).join(', ')}.
        </p>
      )}
      <p className="small muted">
        To see more jobs, add job titles or locations in Profile → Job preferences, or widen "Posted within".
        Jobs already in your feed are counted once, when first found.
      </p>
    </details>
  );
}
