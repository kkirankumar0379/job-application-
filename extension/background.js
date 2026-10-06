// Fetches your profile, saved answers and resume from the JobAgent server (signed in with your account), then runs
// fill.js in the page. Fetching happens here (not in the page) so the server's CORS rules don't apply.

const DEFAULT_BASE = 'http://localhost:5000';

async function getSettings() {
  const { backendUrl, token, email } = await chrome.storage.local.get(['backendUrl', 'token', 'email']);
  return { base: (backendUrl || DEFAULT_BASE).replace(/\/$/, ''), token, email };
}

async function authedFetch(url, token) {
  const res = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {} });
  if (res.status === 401) {
    await chrome.storage.local.remove('token');
    throw new Error('Please log in again.');
  }
  return res;
}

async function getJson(url, token) {
  const res = await authedFetch(url, token);
  if (!res.ok) throw new Error(`${url} → ${res.status}`);
  return res.json();
}

function toBase64(buffer) {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(binary);
}

async function loadResume(base, token, profileId, pageUrl) {
  const res = await authedFetch(`${base}/api/profiles/${profileId}/resume/file?jobUrl=${encodeURIComponent(pageUrl)}`, token);
  if (!res.ok) return null;
  const disposition = res.headers.get('content-disposition') || '';
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1];
  return {
    name: name ? decodeURIComponent(name) : 'resume.pdf',
    type: res.headers.get('content-type') || 'application/pdf',
    b64: toBase64(await res.arrayBuffer()),
    tailored: res.headers.get('x-tailored') === 'true',
  };
}

// { loggedIn, email, base }
async function status() {
  const { base, token, email } = await getSettings();
  return { loggedIn: !!token, email: email || '', base };
}

async function login({ baseUrl, email, password }) {
  const base = (baseUrl || DEFAULT_BASE).trim().replace(/\/$/, '');
  let res;
  try {
    res = await fetch(`${base}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
  } catch {
    throw new Error(`Can't reach ${base}. Check the address and your internet connection.`);
  }
  if (!res.ok) throw new Error((await res.text()).replace(/^"|"$/g, '') || `Login failed (${res.status})`);
  const { token, user } = await res.json();
  await chrome.storage.local.set({ backendUrl: base, token, email: user.email });
  return status();
}

async function logout() {
  await chrome.storage.local.remove(['token', 'email']);
  return status();
}

async function autofill(tabId, pageUrl) {
  const { base, token } = await getSettings();
  if (!token) throw new Error('Please log in first.');
  const profiles = await getJson(`${base}/api/profiles`, token);
  const profile = profiles[0];
  if (!profile) throw new Error('No profile found. Open the JobAgent app and finish your profile first.');

  const [answers, resume] = await Promise.all([
    getJson(`${base}/api/profiles/${profile.id}/answers`, token).catch(() => []),
    loadResume(base, token, profile.id, pageUrl).catch(() => null),
  ]);

  await chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
    func: (data) => { window.__JOBAGENT_DATA__ = data; },
    args: [{ profile, answers, resume }],
  });
  const injections = await chrome.scripting.executeScript({ target: { tabId, allFrames: true }, files: ['fill.js'] });

  const merged = { filled: [], skipped: [], resume: null };
  for (const { result } of injections) {
    if (!result) continue;
    merged.filled.push(...result.filled);
    merged.skipped.push(...result.skipped);
    merged.resume = merged.resume || result.resume;
  }
  merged.resumeName = resume?.name ?? null;
  merged.resumeTailored = resume?.tailored ?? false;

  // Answer memory: read every question on the page, fill the ones answered before, and show the review panel.
  const company = (() => { try { return new URL(pageUrl).hostname.replace(/^www./, ''); } catch { return ''; } })();
  await chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
    func: (data) => { window.__JOBAGENT_MEM__ = data; },
    args: [{ pageUrl, company }],
  });
  const memory = await chrome.scripting.executeScript({
    target: { tabId, allFrames: true },
    files: ['answer-memory/reader.js', 'answer-memory/applier.js', 'answer-memory/panel.js', 'answer-memory/controller.js'],
  }).catch(() => []);
  merged.memory = { questions: 0, auto: 0, confirm: 0, ask: 0, conflict: 0 };
  for (const { result } of memory) for (const k of Object.keys(merged.memory)) merged.memory[k] += result?.[k] || 0;
  return merged;
}

// ---- answer memory API calls (made here so the page never sees your login token) ----
let cachedProfileId = null;
async function memoryCall(path, method, body) {
  const { base, token } = await getSettings();
  if (!token) throw new Error('Please log in first.');
  if (!cachedProfileId) cachedProfileId = (await getJson(base + '/api/profiles', token))[0]?.id;
  if (!cachedProfileId) throw new Error('No profile found.');
  const res = await fetch(base + '/api/profiles/' + cachedProfileId + path, {
    method, headers: { 'Content-Type': 'application/json', Authorization: 'Bearer ' + token },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) { await chrome.storage.local.remove('token'); throw new Error('Please log in again.'); }
  if (!res.ok) throw new Error(path + ' -> ' + res.status);
  return res.status === 204 ? null : res.json();
}

async function memoryMessage(msg) {
  const page = { pageUrl: msg.pageUrl, company: msg.company };
  switch (msg.type) {
    case 'memory-resolve': return memoryCall('/answer-memory/resolve', 'POST', { ...page, questions: msg.questions });
    case 'memory-save': return memoryCall('/answer-memory', 'POST', { question: msg.question, answer: msg.answer, type: msg.answerType, options: msg.options, sourceUrl: msg.pageUrl, company: msg.company });
    case 'memory-used': return memoryCall('/answer-memory/used', 'POST', msg.ids);
    case 'memory-validate': return memoryCall('/answer-memory/validate', 'POST', { ...page, items: msg.items });
    case 'memory-prefer': {
      const { base, token } = await getSettings();
      const res = await fetch(base + '/api/answer-memory/' + msg.id + '/prefer', { method: 'POST', headers: { Authorization: 'Bearer ' + token } });
      if (!res.ok) throw new Error('prefer -> ' + res.status);
      return null;
    }
  }
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  const task = msg.type?.startsWith('memory-') ? memoryMessage(msg)
    : msg.type === 'status' ? status()
    : msg.type === 'login' ? login(msg)
    : msg.type === 'logout' ? logout()
    : msg.type === 'autofill' ? autofill(msg.tabId, msg.url || '')
    : null;
  if (!task) return false;
  task.then((data) => sendResponse({ ok: true, data }), (e) => sendResponse({ ok: false, error: String(e.message || e) }));
  return true; // async response
});

// Keyboard shortcut (Alt+Shift+F): same as pressing Autofill in the popup; the result shows as a badge on the icon.
chrome.commands.onCommand.addListener(async (command) => {
  if (command !== 'autofill-page') return;
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) return;
  try {
    const data = await autofill(tab.id, tab.url || '');
    await chrome.action.setBadgeBackgroundColor({ color: '#00a572' });
    await chrome.action.setBadgeText({ tabId: tab.id, text: String(data.filled.length) });
  } catch (e) {
    await chrome.action.setBadgeBackgroundColor({ color: '#c0392b' });
    await chrome.action.setBadgeText({ tabId: tab.id, text: '!' });
  }
  setTimeout(() => chrome.action.setBadgeText({ tabId: tab.id, text: '' }), 5000);
});
