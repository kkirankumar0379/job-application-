const $ = (id) => document.getElementById(id);
const out = $('out');

const send = (msg) => new Promise((resolve) => chrome.runtime.sendMessage(msg, resolve));
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

function show(state) {
  $('loginView').hidden = state.loggedIn;
  $('mainView').hidden = !state.loggedIn;
  $('who').textContent = state.email ? `Signed in as ${state.email}` : '';
  if (!state.loggedIn) $('baseUrl').value = $('baseUrl').value || state.base;
}

(async () => {
  const res = await send({ type: 'status' });
  show(res?.ok ? res.data : { loggedIn: false, base: 'http://localhost:5000' });
})();

$('loginView').onsubmit = async (e) => {
  e.preventDefault();
  $('loginBtn').disabled = true;
  out.textContent = 'Logging in…';
  const res = await send({ type: 'login', baseUrl: $('baseUrl').value, email: $('email').value, password: $('password').value });
  $('loginBtn').disabled = false;
  if (!res?.ok) { out.innerHTML = `<p class="err">${esc(res?.error || 'Login failed.')}</p>`; return; }
  $('password').value = '';
  out.textContent = '';
  show(res.data);
};

$('logout').onclick = async () => {
  const res = await send({ type: 'logout' });
  out.textContent = '';
  if (res?.ok) show(res.data);
};

$('go').onclick = async () => {
  $('go').disabled = true;
  out.textContent = 'Filling…';
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const res = await send({ type: 'autofill', tabId: tab.id, url: tab.url });
  $('go').disabled = false;
  if (!res?.ok) {
    out.innerHTML = `<p class="err">${esc(res?.error || 'Something went wrong.')}</p>`;
    if (/log in/i.test(res?.error || '')) show({ loggedIn: false, email: '', base: '' });
    return;
  }

  const { filled, skipped, resume, resumeName, resumeTailored } = res.data;
  let html = `<strong>${filled.length} field${filled.length === 1 ? '' : 's'} filled</strong>`;
  if (resume) html += `<div class="muted">Resume uploaded: ${esc(resume)}${resumeTailored ? ' (tailored for this job)' : ''}</div>`;
  else if (resumeName) html += `<div class="muted">No resume upload box found on this page yet. If it appears on the next step, run Autofill again.</div>`;
  if (filled.length) html += '<h2>Filled</h2>' + filled.map((f) => `<div class="row"><span>${esc(f.field)}</span><span>${esc(f.value)}</span></div>`).join('');
  if (skipped.length) html += '<h2>Left for you</h2>' + skipped.map((f) => `<div class="row"><span>${esc(f.field)}</span><span>${esc(f.reason)}</span></div>`).join('');
  const m = res.data.memory;
  if (m && m.questions) {
    html += '<h2>Answer memory</h2>' + '<div class="row"><span>Filled from memory</span><span>' + m.auto + '</span></div>'
      + '<div class="row"><span>Need your confirmation</span><span>' + m.confirm + '</span></div>'
      + '<div class="row"><span>New questions</span><span>' + m.ask + '</span></div>'
      + (m.conflict ? '<div class="row"><span>Conflicting answers</span><span>' + m.conflict + '</span></div>' : '')
      + '<div class="muted">Review them in the panel on the page (top right).</div>';
  }
  if (!filled.length && !resume) html += '<p class="muted">Nothing to fill here. Open the application form (past any Sign In step) and try again.</p>';
  out.innerHTML = html;
};
