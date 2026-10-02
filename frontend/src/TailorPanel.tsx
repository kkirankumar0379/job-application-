import { useEffect, useState } from 'react';
import { api, type AiStatus, type AtsReport, type FeedJob, type Tailored } from './api';
import { Bar, MatchRing } from './ui';

type Props = { job: FeedJob; onClose: () => void; onGoToProfile: () => void };

const TARGET = 90;

export default function TailorPanel({ job, onClose, onGoToProfile }: Props) {
  const [ai, setAi] = useState<AiStatus | null>(null);
  const [report, setReport] = useState<AtsReport | null>(null);
  const [tailored, setTailored] = useState<Tailored | null>(null);
  const [after, setAfter] = useState<AtsReport | null>(null);
  const [confirmed, setConfirmed] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [generating, setGenerating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    Promise.all([api.aiStatus(), api.ats(job.id)])
      .then(([status, ats]) => {
        if (cancelled) return;
        setAi(status);
        setReport(ats.report);
        setTailored(ats.tailored);
        if (ats.tailored) setConfirmed(new Set(ats.tailored.confirmedSkillsCsv.split(',').map((s) => s.trim()).filter(Boolean)));
      })
      .catch((e) => !cancelled && setError(e instanceof Error ? e.message : String(e)))
      .finally(() => !cancelled && setLoading(false));
    return () => { cancelled = true; };
  }, [job.id]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !generating) onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose, generating]);

  async function generate() {
    setGenerating(true);
    setError(null);
    try {
      const result = await api.tailor(job.id, [...confirmed]);
      setTailored(result.tailored);
      setAfter(result.after);
      setReport(result.before);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setGenerating(false);
    }
  }

  const toggle = (k: string) => setConfirmed((prev) => {
    const next = new Set(prev);
    if (next.has(k)) next.delete(k); else next.add(k);
    return next;
  });

  // Best score reachable truthfully: keywords already in the resume plus the ones the user confirms.
  const reachable = report
    ? Math.round(0.7 * (report.matchedKeywords.length + confirmed.size) / Math.max(1, report.matchedKeywords.length + report.missingKeywords.length) * 100 + 30)
    : 0;
  const finalScore = after?.score ?? tailored?.atsScoreAfter;

  return (
    <div className="modal-backdrop" onClick={() => !generating && onClose()}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Tailor resume" onClick={(e) => e.stopPropagation()}>
        <div className="section-head">
          <div>
            <h2>Tailor your resume</h2>
            <div className="small muted">{job.title} · {job.company}</div>
          </div>
          <button className="secondary sm" onClick={onClose} disabled={generating}>Close</button>
        </div>

        {loading && <div className="empty"><span className="spinner" /> Checking your resume against this job…</div>}
        {error && <div className="banner error">{error}</div>}

        {!loading && ai && !ai.configured && (
          <div className="banner warn">
            <strong>Connect AI to rewrite resumes.</strong> Add your Anthropic API key under <em>Profile → AI resume tailoring</em>.
            You can still see your current ATS score below.
            <div style={{ marginTop: 8 }}><button className="primary sm" onClick={onGoToProfile}>Go to Profile</button></div>
          </div>
        )}

        {!loading && report && (
          <>
            <section className="ats-grid">
              <div className="ats-scores">
                <div className="ats-score">
                  <MatchRing score={report.score} size={88} />
                  <div className="small muted">Your resume now</div>
                </div>
                {finalScore != null && (
                  <>
                    <div className="ats-arrow" aria-hidden="true">→</div>
                    <div className="ats-score">
                      <MatchRing score={finalScore} size={88} />
                      <div className="small muted">Tailored</div>
                    </div>
                  </>
                )}
              </div>
              <div className="bars">
                <Bar label="Job keywords" value={(after ?? report).keywordScore} note="70% of the score" />
                <Bar label="Job title" value={(after ?? report).titleScore} note={`"${report.targetTitle}"`} />
                <Bar label="Standard sections" value={(after ?? report).sectionScore}
                  note={(after ?? report).missingSections.length ? `Missing: ${(after ?? report).missingSections.join(', ')}` : 'Summary, Skills, Experience, Education'} />
                <Bar label="Contact details" value={(after ?? report).contactScore} note="Email and phone" />
              </div>
            </section>

            {report.missingKeywords.length > 0 && (
              <section>
                <h3>Keywords this job asks for that your resume doesn't show</h3>
                <p className="small muted">
                  Tick only the ones you genuinely have experience with. They'll be worked into your resume;
                  unticked ones are left out, because claiming skills you don't have backfires in interviews.
                </p>
                <div className="chips">
                  {report.missingKeywords.map((k) => (
                    <label key={k} className={`chip-toggle ${confirmed.has(k) ? 'on' : ''}`}>
                      <input type="checkbox" checked={confirmed.has(k)} onChange={() => toggle(k)} disabled={generating} />
                      {confirmed.has(k) ? '✓ ' : ''}{k}
                    </label>
                  ))}
                </div>
                <p className="small muted">
                  With these choices the best honest score is about <strong>{Math.min(100, reachable)}%</strong>
                  {reachable < TARGET ? `, below the ${TARGET}% target: the gap is skills this job wants that you haven't ticked.` : '.'}
                </p>
              </section>
            )}
            {report.missingKeywords.length === 0 && (
              <p className="small ok-text">Your resume already mentions every technical keyword this job lists. Tailoring will sharpen the title, summary and ordering.</p>
            )}

            <div className="row">
              <button className="primary" onClick={generate} disabled={generating || !ai?.configured}>
                {generating ? <><span className="spinner" /> Rewriting… this takes about a minute</> : tailored ? '✨ Regenerate tailored resume' : '✨ Generate tailored resume'}
              </button>
              {tailored && (
                <button className="secondary" onClick={() => api.downloadTailored(tailored.id, tailored.fileName).catch((e) => setError(e instanceof Error ? e.message : String(e)))}>⬇ Download .docx</button>
              )}
            </div>

            {tailored && (
              <section className="tailored-preview">
                {finalScore != null && finalScore >= TARGET && <p className="ok-text">✓ ATS score {finalScore}% meets the {TARGET}% target.</p>}
                {finalScore != null && finalScore < TARGET && (
                  <p className="small">
                    ATS score {finalScore}%. It's below {TARGET}% because the job asks for skills that aren't on your resume
                    {after?.missingKeywords.length ? ` (${after.missingKeywords.join(', ')})` : ''}. If you do have any of them, tick them above and regenerate.
                  </p>
                )}
                <p className="small muted">"Autofill &amp; apply" on this job will upload this tailored resume ({tailored.fileName}).</p>
                {tailored.content.changes.length > 0 && (
                  <>
                    <h3>What changed</h3>
                    <ul className="small">{tailored.content.changes.map((c) => <li key={c}>{c}</li>)}</ul>
                  </>
                )}
                <h3>Preview</h3>
                <div className="resume-preview">
                  <strong>{tailored.content.headline}</strong>
                  <p>{tailored.content.summary}</p>
                  {tailored.content.skills.map((g) => <div key={g.category} className="small"><strong>{g.category}:</strong> {g.items.join(', ')}</div>)}
                  {tailored.content.experience.map((e) => (
                    <div key={`${e.company}-${e.dates}`} className="preview-role">
                      <div><strong>{e.title}</strong> · {e.company} <span className="muted small">{e.dates}</span></div>
                      <ul className="small">{e.bullets.map((b) => <li key={b}>{b}</li>)}</ul>
                    </div>
                  ))}
                </div>
              </section>
            )}
          </>
        )}
      </div>
    </div>
  );
}
