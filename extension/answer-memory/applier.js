// Auto-Fill Engine (pipeline step 7). Puts an answer into a question's field(s) the way a person would, so the
// page's own scripts see the change. It never clicks Next / Submit and never overwrites an answer you typed.
(() => {
  const JA = (window.JA = window.JA || {});
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const mark = (el, color) => { try { el.style.outline = `2px solid ${color}`; el.style.outlineOffset = '1px'; } catch { /* ignore */ } };

  function setNativeValue(el, value) {
    const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
      : el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    el.focus();
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value); // bypasses React's value tracking
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.dispatchEvent(new Event('blur', { bubbles: true }));
  }

  const yesNo = (s) => {
    const a = JA.norm(s);
    if (['yes', 'y', 'true'].includes(a) || a.startsWith('yes ')) return true;
    if (['no', 'n', 'false'].includes(a) || a.startsWith('no ')) return false;
    return null;
  };

  /** The option label that expresses the answer, or null when none clearly does. */
  function pickLabel(answer, labels) {
    const a = JA.norm(answer);
    const exact = labels.filter((l) => JA.norm(l) === a);
    if (exact.length === 1) return exact[0];
    const yn = yesNo(answer);
    if (yn !== null) { const same = labels.filter((l) => yesNo(l) === yn); if (same.length === 1) return same[0]; }
    const part = labels.filter((l) => a.length >= 3 && (JA.norm(l).includes(a) || (JA.norm(l).length >= 3 && a.includes(JA.norm(l)))));
    return part.length === 1 ? part[0] : null;
  }

  async function chooseFromList(button, answer) {
    button.click();
    for (let i = 0; i < 15; i++) {
      await sleep(100);
      const opts = [...document.querySelectorAll('[role="option"], [role="listbox"] li')].filter(JA.visible);
      if (!opts.length) continue;
      const label = pickLabel(answer, opts.map((o) => JA.squash(o.innerText)));
      const hit = label && opts.find((o) => JA.squash(o.innerText) === label);
      if (hit) { hit.click(); await sleep(150); return true; }
      break;
    }
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    return false;
  }

  /**
   * Fills the question's field(s) with the answer. Returns true when the field now shows it.
   * color: green = filled from memory, amber = filled after you confirmed.
   */
  JA.apply = async (entry, answer, color = '#00a572') => {
    if (answer == null || String(answer).trim() === '') return false;
    const els = entry.els;
    let ok = false;
    switch (entry.kind) {
      case 'radio': {
        const labels = els.map(JA.optionLabel);
        const label = pickLabel(answer, labels);
        const target = label && els[labels.indexOf(label)];
        if (target) { if (!target.checked) target.click(); ok = target.checked; }
        break;
      }
      case 'checkbox': {
        const want = yesNo(answer);
        if (want === null) break;
        if (els[0].checked !== want) els[0].click();
        ok = els[0].checked === want;
        break;
      }
      case 'multi': {
        const wanted = String(answer).split(/\s*,\s*/).map(JA.norm);
        const labels = els.map((e) => JA.norm(JA.optionLabel(e)));
        els.forEach((e, i) => { if (wanted.includes(labels[i]) && !e.checked) e.click(); });
        ok = els.some((e) => e.checked);
        break;
      }
      case 'select': {
        const sel = els[0];
        const opts = [...sel.options].filter((o) => o.value !== '');
        const label = pickLabel(answer, opts.map((o) => JA.squash(o.text)));
        const opt = label && opts.find((o) => JA.squash(o.text) === label);
        if (opt) { setNativeValue(sel, opt.value); ok = true; }
        break;
      }
      case 'listbox': ok = await chooseFromList(els[0], answer); break;
      default:
        if (!els[0].value || !els[0].value.trim()) { setNativeValue(els[0], String(answer)); ok = true; }
    }
    if (ok) els.forEach((e) => mark(e, color));
    return ok;
  };

  JA.highlight = (entry) => {
    const el = entry.els[0];
    el.scrollIntoView({ block: 'center', behavior: 'smooth' });
    mark(el, '#e08a00');
    if (el.focus) try { el.focus({ preventScroll: true }); } catch { /* ignore */ }
  };
  JA.mark = mark;
})();
