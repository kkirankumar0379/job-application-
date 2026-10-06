// Orchestrates the answer-memory pipeline on the page:
//   read page -> extract questions -> (server: normalize, search memory, score, validate) -> auto-fill
//   -> user confirmation -> save new / changed answers back to memory -> final check before submitting.
// The server owns matching and safety rules (backend Services/AnswerMemory); this file only moves data and fills fields.
(async () => {
  const JA = (window.JA = window.JA || {});
  const cfg = window.__JOBAGENT_MEM__ || {};
  const bg = (type, payload) => new Promise((resolve) =>
    chrome.runtime.sendMessage({ type, ...payload }, (res) => resolve(res || { ok: false, error: chrome.runtime.lastError?.message || 'No response.' })));
  const ctx = { pageUrl: cfg.pageUrl || location.href, company: cfg.company || location.hostname };
  const live = (e) => e.els.every((el) => el.isConnected);

  // ---------- user answers and changes are saved back to memory ----------
  function watch(entries) {
    for (const entry of entries) {
      for (const el of entry.els) {
        el.addEventListener('change', async (event) => {
          if (!event.isTrusted) return; // only real edits by you; our own fills don't count
          const value = JA.readValue(entry);
          if (!value) return;
          const changed = entry.origin === 'memory' || entry.origin === 'confirmed';
          entry.origin = 'user';
          entry.value = value;
          const res = await bg('memory-save', { question: entry.text, answer: value, answerType: entry.type, options: entry.options, ...ctx });
          if (res.ok && !res.data.skipped) {
            entry.state = 'done';
            entry.saveNote = res.data.replacedAnswer ? `Updated: this is now your preferred answer (was "${res.data.previousAnswer}").`
              : changed ? 'Saved your change.' : 'Saved: I will reuse it next time.';
          } else if (entry.state !== 'skip') entry.state = 'done', entry.saveNote = res.data?.reason || 'Answered.';
          JA.state.report = null;
          JA.renderPanel();
        }, true);
      }
    }
  }

  // ---------- scan the page and resolve the new questions ----------
  async function scan() {
    JA.state.entries = JA.state.entries.filter(live);
    const fresh = JA.readQuestions();
    const toResolve = fresh.filter((e) => e.origin !== 'existing');
    for (const e of fresh) if (e.origin === 'existing') e.state = 'existing';
    const usedIds = [];

    if (toResolve.length) {
      const res = await bg('memory-resolve', {
        ...ctx,
        questions: toResolve.map((e) => ({ id: e.id, text: e.text, type: e.type, options: e.options, context: e.context, required: e.required })),
      });
      if (!res.ok) throw new Error(res.error || 'Could not reach the answer memory.');
      for (const r of res.data.results) {
        const entry = JA.state.entries.find((e) => e.id === r.id);
        if (!entry) continue;
        Object.assign(entry, { confidence: r.confidence, reason: r.reason, memoryId: r.memoryId, candidates: r.candidates || [], suggestion: r.answer });
        if (r.status === 'auto' && (await JA.apply(entry, r.answer))) {
          entry.state = 'auto'; entry.origin = 'memory'; entry.value = JA.readValue(entry);
          usedIds.push(r.memoryId);
        } else if (r.status === 'auto' || r.status === 'confirm') {
          entry.state = 'confirm'; // suggested, never filled without you
          if (r.status === 'auto') entry.reason = 'Could not be filled automatically on this page.';
          JA.mark(entry.els[0], '#e08a00');
        } else if (r.status === 'conflict') { entry.state = 'conflict'; JA.mark(entry.els[0], '#b3261e'); }
        else entry.state = r.status === 'skip' ? 'skip' : 'ask';
      }
    }
    watch(fresh);
    if (usedIds.length) bg('memory-used', { ids: usedIds });
    JA.renderPanel();
    const count = (s) => JA.state.entries.filter((e) => e.state === s).length;
    return { questions: fresh.length, auto: count('auto'), confirm: count('confirm'), ask: count('ask') + count('skip'), conflict: count('conflict') };
  }

  // ---------- final validation pass ----------
  async function finalCheck() {
    await scan(); // pick up fields that appeared since (later steps of the form)
    const items = JA.state.entries.filter(live).map((e) => {
      const answer = JA.readValue(e);
      const origin = !answer ? 'empty' : e.origin === 'empty' ? 'existing' : e.origin;
      return { id: e.id, text: e.text, type: e.type, options: e.options, required: e.required, answer, origin, memoryId: e.memoryId || null };
    });
    const res = await bg('memory-validate', { ...ctx, items });
    if (!res.ok) throw new Error(res.error || 'Final check failed.');
    JA.state.report = res.data;
    JA.state.lastCheck = { ok: res.data.canSubmit, at: Date.now() };
    JA.renderPanel();
    JA.togglePanel(true);
    return res.data;
  }

  // ---------- panel buttons ----------
  JA.onPanelAction = async (act, id, arg) => {
    const entry = JA.state.entries.find((e) => e.id === id);
    if (act === 'close') return JA.togglePanel(false);
    if (act === 'check') return finalCheck().catch((e) => alert(e.message));
    if (!entry) return;
    if (act === 'focus') return JA.highlight(entry);
    if (act === 'accept') {
      if (await JA.apply(entry, entry.suggestion, '#e08a00')) {
        entry.origin = 'confirmed'; entry.value = JA.readValue(entry); entry.state = 'done'; entry.saveNote = 'Confirmed by you.';
        bg('memory-save', { question: entry.text, answer: entry.value, answerType: entry.type, options: entry.options, ...ctx });
      } else { JA.highlight(entry); entry.reason = 'Could not fill this field. Please answer it yourself.'; }
      JA.state.report = null;
      JA.renderPanel();
    }
    if (act === 'prefer') {
      const chosen = (entry.candidates || []).find((c) => c.memoryId === arg);
      if (!chosen) return;
      await bg('memory-prefer', { id: chosen.memoryId });
      if (await JA.apply(entry, chosen.answer, '#e08a00')) {
        entry.origin = 'confirmed'; entry.value = JA.readValue(entry); entry.state = 'done'; entry.saveNote = 'Kept as your preferred answer.';
      } else JA.highlight(entry);
      JA.state.report = null;
      JA.renderPanel();
    }
  };

  // ---------- never submit with unresolved answers ----------
  const FINAL = /^\s*(submit( application)?|submit (&|and) apply|apply|apply now|send application|finish|complete application)\s*$/i;
  if (!JA.guardInstalled) {
    JA.guardInstalled = true;
    document.addEventListener('click', (event) => {
      if (!JA.state.entries.length) return;
      const t = event.target instanceof Element && event.target.closest('button, input[type="submit"], [role="button"], a');
      if (!t || !FINAL.test(t.innerText || t.value || t.getAttribute('aria-label') || '')) return;
      if (JA.state.allowUntil && Date.now() < JA.state.allowUntil) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      finalCheck().then((r) => { if (r.canSubmit) JA.state.allowUntil = Date.now() + 120000; }).catch((e) => alert(e.message));
    }, true);
  }

  return scan();
})();
