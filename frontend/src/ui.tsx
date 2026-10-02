export function timeAgo(iso: string | null | undefined) {
  if (!iso) return '';
  const minutes = (Date.now() - new Date(iso).getTime()) / 6e4;
  if (minutes < 60) return `${Math.max(1, Math.round(minutes))}m ago`;
  const hours = minutes / 60;
  if (hours < 48) return `${Math.round(hours)}h ago`;
  return `${Math.round(hours / 24)}d ago`;
}

/** Workday only reports "Posted Today / Yesterday / N Days Ago", so show day precision for it. */
export function postedLabel(postedAt: string | null | undefined, atsProvider: string) {
  if (!postedAt) return '';
  if (atsProvider !== 'Workday') return timeAgo(postedAt);
  const days = Math.floor((Date.now() - new Date(postedAt).getTime()) / 864e5);
  return days <= 0 ? 'Today' : days === 1 ? 'Yesterday' : `${days}d ago`;
}

export const csv = (s: string) => s.split(',').map((x) => x.trim()).filter(Boolean);

export function scoreTone(score: number) {
  return score >= 80 ? 'great' : score >= 60 ? 'good' : score >= 40 ? 'fair' : 'low';
}

export function scoreLabel(score: number) {
  return score >= 80 ? 'Strong match' : score >= 60 ? 'Good match' : score >= 40 ? 'Fair match' : 'Weak match';
}

export function MatchRing({ score, size = 56 }: { score: number; size?: number }) {
  const stroke = size >= 80 ? 7 : 5;
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  return (
    <div className={`ring ${scoreTone(score)}`} style={{ width: size, height: size }} aria-label={`${score}% match`}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`}>
        <circle cx={size / 2} cy={size / 2} r={r} className="ring-track" strokeWidth={stroke} fill="none" />
        <circle cx={size / 2} cy={size / 2} r={r} className="ring-value" strokeWidth={stroke} fill="none"
          strokeDasharray={c} strokeDashoffset={c * (1 - score / 100)} strokeLinecap="round"
          transform={`rotate(-90 ${size / 2} ${size / 2})`} />
      </svg>
      <span className="ring-text" style={{ fontSize: size >= 80 ? 20 : 14 }}>{score}%</span>
    </div>
  );
}

const avatarHues = [210, 160, 280, 20, 340, 45, 190, 120, 250, 0];

export function CompanyAvatar({ name, size = 44 }: { name: string; size?: number }) {
  const hue = avatarHues[[...name].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) >>> 0, 7) % avatarHues.length];
  return (
    <div className="avatar" style={{ width: size, height: size, fontSize: size * 0.42, ['--hue' as string]: hue }}>
      {name.trim().charAt(0).toUpperCase() || '?'}
    </div>
  );
}

export function Bar({ label, value, note }: { label: string; value: number; note?: string }) {
  return (
    <div className="bar">
      <div className="bar-head"><span>{label}</span><strong>{value}%</strong></div>
      <div className="bar-track"><div className={`bar-fill ${scoreTone(value)}`} style={{ width: `${value}%` }} /></div>
      {note && <div className="small muted">{note}</div>}
    </div>
  );
}
