// User Confirmation (pipeline step 8): a review panel on the application page listing what was filled from memory,
// what needs confirming, what is new, and any conflicting earlier answers, plus the final check before submitting.
(() => {
  const JA = (window.JA = window.JA || {});
  const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  const short = (s, n = 90) => (String(s ?? '').length > n ? String(s).slice(0, n - 1) + '…' : String(s ?? ''));

  const CSS = `
    :host { all: initial; }
    .box { position: fixed; top: 12px; right: 12px; width: 360px; max-height: calc(100vh - 24px); overflow: auto; z-index: 2147483647;
      background: #fff; color: #1d2433; font: 13px/1.4 system-ui, sans-serif; border: 1px solid #cfd6e4; border-radius: 10px; box-shadow: 0 8px 30px rgba(0,0,0,.25); }
    header { display: flex; align-items: center; justify-content: space-between; padding: 10px 12px; background: #0b7a5a; color: #fff; border-radius: 10px 10px 0 0; position: sticky; top: 0; }
    header strong { font-size: 13px; }
    header button { background: transparent; color: #fff; border: 0; font-size: 16px; cursor: pointer; }
    section { padding: 8px 12px; border-top: 1px solid #eef1f6; }
    h3 { margin: 0 0 6px; font-size: 12px; text-transform: uppercase; letter-spacing: .04em; color: #5b6578; display: flex; justify-content: space-between; }
    .item { padding: 6px 0; border-bottom: 1px dashed #e6eaf2; }
    .item:last-child { border-bottom: 0; }
    .q { font-weight: 600; }
    .a { color: #0b7a5a; }
    .why { color: #6b7488; font-size: 12px; }
    .row { display: flex; gap: 6px; margin-top: 5px; flex-wrap: wrap; }
    button.b { border: 1px solid #b9c2d3; background: #f6f8fc; border-radius: 6px; padding: 3px 9px; cursor: pointer; font: inherit; }
    button.b.p { background: #0b7a5a; border-color: #0b7a5a; color: #fff; }
    .ok { color: #0b7a5a; } .warn { color: #b26a00; } .bad { color: #b3261e; }
    .banner { padding: 8px 12px; font-weight: 600; }
    .banner.ok { background: #e6f6ef; } .banner.bad { background: #fdeceb; }
    .empty { color: #8a93a6; padding: 2px 0; }
  `;

  function ensureHost() {
    let host = document.getElementById('jobagent-answer-panel');
    if (!host) {
      host = document.createElement('div');
      host.id = 'jobagent-answer-panel';
      host.attachShadow({ mode: 'open' }).innerHTML = `<style>${CSS}</style><div class="box" hidden></div>`;
      document.documentElement.appendChild(host);
      host.shadowRoot.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-act]');
        if (btn) JA.onPanelAction && JA.onPanelAction(btn.dataset.act, btn.dataset.id, btn.dataset.arg);
      });
    }
    return host.shadowRoot.querySelector('.box');
  }

  const group = (state) => JA.state.entries.filter((e) => e.state === state);

  JA.renderPanel = () => {
    const box = ensureHost();
    const auto = group('auto'), confirm = group('confirm'), fresh = [...group('ask'), ...group('skip')], conflict = group('conflict'), done = group('done');
    const report = JA.state.report;
    if (!auto.length && !confirm.length && !fresh.length && !conflict.length && !done.length && !report) { box.hidden = true; return; }

    const section = (title, items, render, emptyText) =>
      `<section><h3><span>${title}</span><span>${items.length}</span></h3>${items.length ? items.map(render).join('') : `<div class="empty">${emptyText}</div>`}</section>`;

    let html = `<header><strong>JobAgent answer memory</strong><button data-act="close" title="Hide">×</button></header>`;
    if (report) html += renderReport(report);

    if (conflict.length) html += section('Conflicting earlier answers', conflict, (e) => `
      <div class="item"><div class="q">${esc(short(e.text, 140))}</div><div class="why bad">${esc(e.reason)}</div>
        ${e.candidates.map((c) => `<div class="row"><span class="a">${esc(short(c.answer))}</span>
          <button class="b" data-act="prefer" data-id="${e.id}" data-arg="${c.memoryId}">Use and keep this</button></div>
          <div class="why">from: ${esc(short(c.question, 80))}</div>`).join('')}</div>`, '');

    if (confirm.length) html += section('Needs your confirmation', confirm, (e) => `
      <div class="item"><div class="q">${esc(short(e.text, 140))}</div>
        <div>Suggested: <span class="a">${esc(short(e.suggestion))}</span></div><div class="why warn">${esc(e.reason)}</div>
        <div class="row"><button class="b p" data-act="accept" data-id="${e.id}">Use this answer</button>
        <button class="b" data-act="focus" data-id="${e.id}">I'll answer it</button></div></div>`, '');

    if (fresh.length) html += section('New questions: not filled', fresh, (e) => `
      <div class="item"><div class="q">${esc(short(e.text, 140))}${e.required ? ' <span class="bad">*</span>' : ''}</div>
        <div class="why">${esc(e.state === 'skip' ? e.reason : 'No reliable earlier answer. Answer it on the page and I will remember it.')}</div>
        <div class="row"><button class="b" data-act="focus" data-id="${e.id}">Go to field</button></div></div>`, '');

    html += section('Filled from memory', auto, (e) => `
      <div class="item"><div class="q">${esc(short(e.text, 140))}</div><div class="a">${esc(short(e.value))}</div>
        <div class="why">${e.confidence}% match: ${esc(e.reason)}</div></div>`, 'Nothing was filled automatically.');

    if (done.length) html += section('Saved to memory', done, (e) => `
      <div class="item"><div class="q">${esc(short(e.text, 100))}</div><div class="a">${esc(short(e.value))}</div><div class="why">${esc(e.saveNote || 'Saved.')}</div></div>`, '');

    html += `<section><div class="row"><button class="b p" data-act="check">Final check before submitting</button></div>
      <div class="why" style="margin-top:6px">JobAgent never submits for you. Guessed or low-confidence answers are never filled.</div></section>`;
    box.innerHTML = html;
    box.hidden = false;
  };

  function renderReport(r) {
    const line = (l) => `<div class="item"><div class="q">${esc(short(l.question, 120))}</div>${l.answer ? `<div class="a">${esc(short(l.answer))}</div>` : ''}<div class="why">${esc(l.note)}</div></div>`;
    const part = (title, list, cls) => `<section><h3><span class="${cls}">${title}</span><span>${list.length}</span></h3>${list.length ? list.map(line).join('') : '<div class="empty">None</div>'}</section>`;
    return `<div class="banner ${r.canSubmit ? 'ok' : 'bad'}">${r.canSubmit ? 'Final check passed. Review the page, then submit yourself.' : 'Not ready to submit. Resolve the items below.'}</div>`
      + part('Answered from memory', r.fromMemory, 'ok') + part('Needs confirmation', r.needsConfirmation, 'warn')
      + part('New / unanswered', r.unanswered, 'bad') + part('Conflicting earlier answers', r.conflicts, 'bad');
  }

  JA.togglePanel = (show) => { const b = ensureHost(); b.hidden = show === undefined ? !b.hidden : !show; };
})();
