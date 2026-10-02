import { useState, type FormEvent } from 'react';
import { api, auth, type AuthUser } from './api';

type Props = { onSignedIn: (user: AuthUser) => void };

export default function AuthPage({ onSignedIn }: Props) {
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [inviteCode, setInviteCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [working, setWorking] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setWorking(true);
    try {
      const result = mode === 'login'
        ? await api.login(email, password)
        : await api.register({ email, password, firstName, lastName, inviteCode });
      auth.save(result.token);
      onSignedIn(result.user);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setWorking(false);
    }
  }

  return (
    <main className="auth-main">
      <section className="card auth-card">
        <div className="brand"><span className="logo">J</span> JobAgent</div>
        <h2>{mode === 'login' ? 'Log in' : 'Create your account'}</h2>
        <p className="small muted">
          {mode === 'login'
            ? 'Welcome back. Your jobs, resume and preferences are waiting.'
            : 'Upload your resume after signing up and we\'ll match jobs to your tech stack.'}
        </p>
        {error && <div className="banner error" role="alert">{error}</div>}
        <form onSubmit={submit} className="auth-form">
          {mode === 'register' && (
            <div className="row">
              <input style={{ flex: 1 }} placeholder="First name" autoComplete="given-name" required value={firstName} onChange={(e) => setFirstName(e.target.value)} />
              <input style={{ flex: 1 }} placeholder="Last name" autoComplete="family-name" required value={lastName} onChange={(e) => setLastName(e.target.value)} />
            </div>
          )}
          <input type="email" placeholder="Email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
          <input type="password" placeholder={mode === 'register' ? 'Password (at least 8 characters)' : 'Password'}
            autoComplete={mode === 'login' ? 'current-password' : 'new-password'} required minLength={mode === 'register' ? 8 : undefined}
            value={password} onChange={(e) => setPassword(e.target.value)} />
          {mode === 'register' && (
            <input placeholder="Invite code (if you were given one)" autoComplete="off" value={inviteCode} onChange={(e) => setInviteCode(e.target.value)} />
          )}
          <button type="submit" className="primary" disabled={working}>
            {working ? <><span className="spinner" /> One moment…</> : mode === 'login' ? 'Log in' : 'Create account'}
          </button>
        </form>
        <p className="small">
          {mode === 'login' ? 'New here? ' : 'Already have an account? '}
          <button type="button" className="link-btn" onClick={() => { setMode(mode === 'login' ? 'register' : 'login'); setError(null); }}>
            {mode === 'login' ? 'Create an account' : 'Log in'}
          </button>
        </p>
      </section>
    </main>
  );
}
