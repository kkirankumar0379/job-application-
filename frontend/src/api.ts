export type Profile = {
  id: string; firstName: string; lastName: string; email: string; phone: string;
  city: string; state: string; country: string; linkedInUrl: string; resumePath: string;
  summary: string; skillsCsv: string; yearsOfExperience: number | null; updatedAt?: string;
};

export type FeedJob = {
  id: string; title: string; company: string; location: string; isRemote: boolean; applyUrl: string; careersUrl: string;
  atsProvider: string; postedAt: string | null; isSaved: boolean; matchScore: number; skillScore: number;
  experienceScore: number; titleScore: number; stackScore: number; requiredYears: number | null; matchedSkillsCsv: string; missingSkillsCsv: string; matchReason: string;
  otherLocations?: string[];
};

export type JobDetail = FeedJob & { description: string; createdAt: string };

export type DiscoveryRun = {
  id: string; startedAt: string; finishedAt: string | null; companiesScanned: number; companiesFailed: number;
  jobsSeen: number; jobsMatched: number; jobsSaved: number; error: string; funnelJson: string;
};

export type ScanProgress = { companiesTotal: number; companiesDone: number; jobsSeen: number; jobsSaved: number };

export type FeedSource = 'all' | 'boards' | 'adzuna';

export type Feed = { jobs: FeedJob[]; counts: { all: number; boards: number; adzuna: number }; lastRun: DiscoveryRun | null; runningSince: string | null; progress: ScanProgress | null };

export type FeedQuery = { hours: number; minScore: number; remote: boolean; mainStack: boolean; q: string; sort: 'match' | 'recent'; view: 'all' | 'saved'; source: FeedSource };

export type Preferences = {
  id: string; candidateProfileId: string; enabled: boolean; roleKeywordsCsv: string; excludeKeywordsCsv: string;
  locationsCsv: string; remoteOnly: boolean; experienceLevel: 'Any' | 'Entry' | 'Mid' | 'Senior'; needsSponsorship: boolean; excludeFederalJobs: boolean; blockedCompaniesCsv: string; primarySkillsCsv: string;
  maxAgeHours: number; scanEveryHours: number; lastRunAt: string | null;
};

export type Company = {
  id: string; name: string; atsProvider: string; boardToken: string; enabled: boolean;
  lastScannedAt: string | null; lastError: string; lastJobCount: number | null;
};

export type ImportResult = { row: number; input: string; status: 'Added' | 'Exists' | 'NotFound'; message: string; company: string | null; provider: string | null; companyId: string | null };

export type ImportSummary = { added: number; existing: number; notFound: number; truncated: boolean; results: ImportResult[] };

export type AtsReport = {
  score: number; keywordScore: number; titleScore: number; sectionScore: number; contactScore: number;
  matchedKeywords: string[]; missingKeywords: string[]; missingSections: string[]; targetTitle: string;
};

export type TailoredContent = {
  headline: string; summary: string;
  skills: { category: string; items: string[] }[];
  experience: { title: string; company: string; location: string; dates: string; bullets: string[] }[];
  projects: { name: string; bullets: string[] }[];
  education: { degree: string; school: string; dates: string }[];
  certifications: string[]; changes: string[];
};

export type Tailored = {
  id: string; jobPostingId: string; atsScoreBefore: number; atsScoreAfter: number; createdAt: string;
  confirmedSkillsCsv: string; fileName: string; content: TailoredContent;
};

export type AiStatus = {
  configured: boolean; source: string | null; maskedKey: string | null; canManageKey: boolean;
  usedThisMonth: number; monthlyLimit: number | null;
};

export type AuthUser = { id: string; email: string; isAdmin: boolean };
type AuthResult = { token: string; user: AuthUser };

const TOKEN_KEY = 'jobagent.token';
export const auth = {
  token: () => localStorage.getItem(TOKEN_KEY),
  save: (token: string) => localStorage.setItem(TOKEN_KEY, token),
  clear: () => localStorage.removeItem(TOKEN_KEY),
};
// Called when the server says the login is no longer valid (expired or removed), so the app can show the login screen.
let onUnauthorized: () => void = () => {};
export const setUnauthorizedHandler = (fn: () => void) => { onUnauthorized = fn; };

export type Session = {
  sessionId: string; applyUrl: string; atsProvider: string; status: string;
  filledFields: string[]; unknownFields: string[]; message: string | null;
};

export type Application = {
  id: string; jobPostingId: string; status: string; atsProvider: string; notes: string; submittedAt: string | null;
  updatedAt: string; jobTitle: string; company: string; applyUrl: string;
};

export type ResumeUpload = { profile: Profile; detectedSkills: string[]; addedSkills: string[]; filledFields: string[]; textExtracted: boolean };

export type ParsedResume = {
  firstName: string; lastName: string; email: string; phone: string; city: string; state: string; country: string;
  linkedInUrl: string; yearsOfExperience: number | null; summary: string; skills: string[];
};

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = init?.body instanceof FormData ? {} : { 'Content-Type': 'application/json' };
  const token = auth.token();
  if (token) headers.Authorization = `Bearer ${token}`;
  let res: Response;
  try {
    res = await fetch(`/api${path}`, { ...init, headers });
  } catch {
    throw new Error('Can\'t reach the server. Check your internet connection (or, if you run this on your own computer, that the backend is running).');
  }
  if (res.status === 502 || res.status === 503 || res.status === 504) {
    throw new Error('Can\'t reach the server right now. Try again in a moment.');
  }
  if (res.status === 401 && token) {
    auth.clear();
    onUnauthorized();
    throw new Error('Your session has expired. Please log in again.');
  }
  if (res.status === 429) throw new Error('Too many attempts. Wait a minute and try again.');
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text.replace(/^"|"$/g, '') || `${res.status} ${res.statusText}`);
  }
  return res.status === 204 || res.status === 202 ? (undefined as T) : res.json();
}

const post = <T,>(path: string, body: unknown) => request<T>(path, { method: 'POST', body: JSON.stringify(body) });
const put = <T,>(path: string, body: unknown) => request<T>(path, { method: 'PUT', body: JSON.stringify(body) });

export const api = {
  register: (body: { email: string; password: string; firstName: string; lastName: string; inviteCode: string }) =>
    post<AuthResult>('/auth/register', body),
  login: (email: string, password: string) => post<AuthResult>('/auth/login', { email, password }),
  me: () => request<AuthUser>('/auth/me'),

  profiles: () => request<Profile[]>('/profiles'),
  createProfile: (p: Omit<Profile, 'id'>) => post<Profile>('/profiles', p),
  updateProfile: (p: Profile) => put<Profile>(`/profiles/${p.id}`, p),
  uploadResume: (id: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<ResumeUpload>(`/profiles/${id}/resume`, { method: 'POST', body: form });
  },

  parseResume: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<ParsedResume>('/resume/parse', { method: 'POST', body: form });
  },

  feed: (profileId: string, q: FeedQuery) => {
    const params = new URLSearchParams({
      hours: String(q.hours), minScore: String(q.minScore), remote: String(q.remote), mainStack: String(q.mainStack), q: q.q, sort: q.sort, view: q.view, source: q.source,
    });
    return request<Feed>(`/profiles/${profileId}/feed?${params}`);
  },
  job: (id: string) => request<JobDetail>(`/jobs/${id}`),
  saveJob: (id: string, saved: boolean) => request<void>(`/jobs/${id}/save?saved=${saved}`, { method: 'POST' }),
  dismissJob: (id: string) => request<void>(`/jobs/${id}/dismiss`, { method: 'POST' }),
  markApplied: (id: string, applied: boolean) => request<void>(`/jobs/${id}/applied?applied=${applied}`, { method: 'POST' }),

  preferences: (profileId: string) => request<Preferences>(`/profiles/${profileId}/preferences`),
  savePreferences: (profileId: string, p: Preferences) => put<Preferences>(`/profiles/${profileId}/preferences`, p),
  runDiscovery: (profileId: string) => post<void>(`/profiles/${profileId}/discovery/run`, {}),
  discoveryStatus: () => request<{ runningSince: string | null }>('/discovery/status'),
  discoveryRuns: (profileId: string) => request<DiscoveryRun[]>(`/profiles/${profileId}/discovery/runs`),

  companies: () => request<Company[]>('/companies'),
  addCompany: (careersUrl: string) => post<{ company: Company; message: string }>('/companies', { careersUrl }),
  importCompanies: (file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<ImportSummary>('/companies/import', { method: 'POST', body: form });
  },
  setCompanyEnabled: (id: string, enabled: boolean) => request<void>(`/companies/${id}/enabled?enabled=${enabled}`, { method: 'PUT' }),
  removeCompany: (id: string) => request<void>(`/companies/${id}`, { method: 'DELETE' }),

  adzunaStatus: () => request<{ configured: boolean; appId: string | null }>('/sources/adzuna'),
  saveAdzuna: (appId: string, appKey: string) => post<{ configured: boolean; appId: string | null }>('/sources/adzuna', { appId, appKey }),
  aiStatus: () => request<AiStatus>('/ai/status'),
  saveAiKey: (apiKey: string | null) => post<AiStatus>('/ai/key', { apiKey }),
  ats: (jobId: string) => request<{ report: AtsReport; tailored: Tailored | null }>(`/jobs/${jobId}/ats`),
  tailor: (jobId: string, confirmedSkills: string[]) =>
    post<{ before: AtsReport; after: AtsReport; passes: number; tailored: Tailored; text: string }>(`/jobs/${jobId}/tailor`, { confirmedSkills }),
  // A plain link can't carry the login token, so fetch the file with it and save the blob.
  downloadTailored: async (id: string, fileName: string) => {
    const res = await fetch(`/api/tailored/${id}/download`, { headers: { Authorization: `Bearer ${auth.token() ?? ''}` } });
    if (!res.ok) throw new Error('Couldn\'t download that resume. Tailor it again to regenerate the file.');
    const url = URL.createObjectURL(await res.blob());
    const a = Object.assign(document.createElement('a'), { href: url, download: fileName });
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(url);
  },

  applications: () => request<Application[]>('/applications'),
  startAutomation: (applyUrl: string, candidateProfileId: string, jobPostingId: string | null) =>
    post<Session>('/automation/start', { applyUrl, candidateProfileId, jobPostingId }),
  session: (id: string) => request<Session>(`/automation/${id}`),
  submit: (id: string) => post<Session>(`/automation/${id}/submit`, { approved: true }),
  closeSession: (id: string) => request<void>(`/automation/${id}`, { method: 'DELETE' }),
};
