// Job Page Reader + Question Extractor (pipeline steps 1-2).
// Scans the whole page for application questions and describes each one: its text, type, options and context.
// Pure page reading: it never changes a field. Works on any site because it relies on labels, legends and ARIA,
// not on one company's markup. Loaded as a plain script; everything hangs off window.JA.
(() => {
  const JA = (window.JA = window.JA || {});
  JA.state = JA.state || { entries: [], seq: 0 };

  const squash = (s) => (s || '').replace(/\s+/g, ' ').trim();
  JA.squash = squash;
  JA.norm = (s) => squash(s).toLowerCase().replace(/[.,;:!?()"*]/g, '').trim();

  const visible = (el) => {
    if (!el || !el.getBoundingClientRect) return false;
    const r = el.getBoundingClientRect();
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && (r.width > 0 || r.height > 0);
  };

  // Label text without the "*" / "(required)" marker; remembers which texts carried one (see isRequired).
  const starred = new Set();
  const cleanLabel = (t) => {
    const raw = squash(t);
    const clean = raw.replace(/\(\s*required\s*\)/i, '').replace(/\*/g, '').replace(/\s*required\s*$/i, '').trim();
    if (clean !== raw) starred.add(clean);
    return clean;
  };

  // Fields the profile autofill (fill.js) already covers, and fields that are never questions.
  const IDENTITY = /^(legal |preferred |first |last |middle |full |given |family )?(name|first name|last name|middle name|surname)$|e-?mail|phone|mobile|\btel\b|address|street|\bcity\b|^state\b|province|zip|postal|^country|linkedin|github|portfolio|personal (website|site)|resume|\bcv\b|cover letter|search|password|captcha|user ?name|verification code|one[- ]time/i;

  function optionLabel(input) {
    const fromLabels = input.labels && input.labels[0] && input.labels[0].innerText;
    if (fromLabels) return cleanLabel(fromLabels);
    const aria = input.getAttribute('aria-label');
    if (aria) return cleanLabel(aria);
    const by = input.getAttribute('aria-labelledby');
    if (by) return cleanLabel(by.split(/\s+/).map((id) => document.getElementById(id)?.innerText || '').join(' '));
    const next = input.nextElementSibling || input.parentElement;
    return cleanLabel(next ? next.innerText : input.value);
  }

  // The question a group of options (radios / checkboxes) belongs to.
  function groupQuestion(options) {
    const first = options[0];
    const container = first.closest('fieldset, [role="radiogroup"], [role="group"]');
    const legend = container && container.querySelector('legend');
    if (legend && squash(legend.innerText)) return cleanLabel(legend.innerText);
    if (container) {
      const aria = container.getAttribute('aria-label');
      if (aria) return cleanLabel(aria);
      const by = container.getAttribute('aria-labelledby');
      if (by) { const t = by.split(/\s+/).map((id) => document.getElementById(id)?.innerText || '').join(' '); if (squash(t)) return cleanLabel(t); }
    }
    // Otherwise: the closest label-like text that comes before the first option.
    const optionLabels = new Set(options.flatMap((o) => [...(o.labels || [])]));
    let node = container || first.parentElement;
    for (let depth = 0; node && depth < 6; depth++, node = node.parentElement) {
      const candidates = [...node.querySelectorAll('legend, [role="heading"], label, p, span, div, h1, h2, h3, h4, h5, h6')].filter((c) => {
        if (optionLabels.has(c) || options.some((o) => c.contains(o) || o.contains(c))) return false;
        if (c.children.length > 3) return false;
        const t = squash(c.innerText);
        return t.length >= 8 && t.length <= 400 && (first.compareDocumentPosition(c) & Node.DOCUMENT_POSITION_PRECEDING);
      });
      if (candidates.length) return cleanLabel(candidates[candidates.length - 1].innerText);
    }
    return '';
  }

  function fieldQuestion(el) {
    const parts = [];
    if (el.labels) for (const l of el.labels) parts.push(l.innerText);
    const by = el.getAttribute('aria-labelledby');
    if (by) by.split(/\s+/).forEach((id) => { const n = document.getElementById(id); if (n) parts.push(n.innerText); });
    if (!squash(parts.join(' '))) parts.push(el.getAttribute('aria-label') || '');
    if (!squash(parts.join(' '))) {
      const wrap = el.closest('fieldset, [class*="field" i], [class*="question" i], [class*="form-group" i], li, div');
      const lab = wrap && wrap.querySelector('label, legend, [class*="label" i]');
      if (lab) parts.push(lab.innerText);
    }
    if (!squash(parts.join(' '))) parts.push(el.placeholder || '');
    return cleanLabel(parts.join(' '));
  }

  const isRequired = (els, text) =>
    els.some((e) => e.required || e.getAttribute('aria-required') === 'true')
    || starred.has(text);

  function contextOf(el) {
    const parts = [];
    const described = el.getAttribute('aria-describedby');
    if (described) described.split(/\s+/).forEach((id) => { const n = document.getElementById(id); if (n) parts.push(n.innerText); });
    let node = el;
    for (let i = 0; i < 8 && node; i++, node = node.parentElement) {
      const h = node.querySelector && node.querySelector('h1, h2, h3, [role="heading"]');
      if (h && squash(h.innerText).length < 120) { parts.push(squash(h.innerText)); break; }
    }
    return squash(parts.join(' | ')).slice(0, 300);
  }

  const known = (el) => JA.state.entries.some((e) => e.els.includes(el));

  /** Reads every question on the page that isn't known yet and returns the new entries. */
  JA.readQuestions = () => {
    const out = [];
    const add = (entry) => { entry.id = `q${++JA.state.seq}`; entry.state = 'new'; entry.origin = 'empty'; JA.state.entries.push(entry); out.push(entry); };

    // Radio groups (native inputs, and ARIA radios).
    const radios = [...document.querySelectorAll('input[type="radio"]')].filter((r) => !r.disabled && visible(r) && !known(r));
    const radioGroups = new Map();
    for (const r of radios) {
      const key = (r.form ? 'f' : '') + (r.name || '') + '|' + (r.closest('fieldset, [role="radiogroup"]')?.id || '');
      (radioGroups.get(key) || radioGroups.set(key, []).get(key)).push(r);
    }
    for (const group of radioGroups.values()) {
      const text = groupQuestion(group);
      if (!text || IDENTITY.test(text)) continue;
      add({ kind: 'radio', els: group, text, type: 'radio', options: group.map(optionLabel), required: isRequired(group, text), context: contextOf(group[0]) });
    }

    // Checkboxes: a lone checkbox is a yes/no statement; several in a group are a multi-select question.
    const boxes = [...document.querySelectorAll('input[type="checkbox"]')].filter((c) => !c.disabled && visible(c) && !known(c));
    const boxGroups = new Map();
    for (const c of boxes) {
      const fs = c.closest('fieldset, [role="group"]');
      const key = fs ? 'g' + [...document.querySelectorAll('fieldset, [role="group"]')].indexOf(fs) : 'solo' + boxes.indexOf(c);
      (boxGroups.get(key) || boxGroups.set(key, []).get(key)).push(c);
    }
    for (const group of boxGroups.values()) {
      if (group.length === 1) {
        const text = optionLabel(group[0]) || fieldQuestion(group[0]);
        if (!text || IDENTITY.test(text)) continue;
        add({ kind: 'checkbox', els: group, text, type: 'checkbox', options: ['Yes', 'No'], required: isRequired(group, text), context: contextOf(group[0]) });
      } else {
        const text = groupQuestion(group);
        if (!text || IDENTITY.test(text)) continue;
        add({ kind: 'multi', els: group, text, type: 'multi', options: group.map(optionLabel), required: isRequired(group, text), context: contextOf(group[0]) });
      }
    }

    // Native dropdowns.
    for (const sel of document.querySelectorAll('select')) {
      if (sel.disabled || !visible(sel) || known(sel)) continue;
      const text = fieldQuestion(sel);
      if (!text || IDENTITY.test(text)) continue;
      const options = [...sel.options].filter((o) => o.value !== '' && !/^(select|choose|please select|--)/i.test(squash(o.text))).map((o) => squash(o.text));
      add({ kind: 'select', els: [sel], text, type: 'select', options, required: isRequired([sel], text), context: contextOf(sel) });
    }

    // Custom dropdown buttons (Workday style): options are only known once the list is opened, so none are listed.
    for (const btn of document.querySelectorAll('button[aria-haspopup="listbox"], [role="combobox"]:not(input):not(select)')) {
      if (!visible(btn) || known(btn)) continue;
      const text = fieldQuestion(btn) || cleanLabel(btn.getAttribute('aria-label') || '');
      if (!text || IDENTITY.test(text)) continue;
      add({ kind: 'listbox', els: [btn], text, type: 'select', options: [], required: isRequired([btn], text), context: contextOf(btn) });
    }

    // Text boxes.
    for (const el of document.querySelectorAll('input, textarea')) {
      if (el instanceof HTMLInputElement && !['text', 'number', 'url', 'tel', 'email', ''].includes(el.type)) continue;
      if (el.disabled || el.readOnly || !visible(el) || known(el)) continue;
      if (el.getAttribute('role') === 'combobox' || el.getAttribute('aria-haspopup') === 'listbox') continue;
      const text = fieldQuestion(el);
      if (!text || IDENTITY.test(text) || el.type === 'email' || el.type === 'tel') continue;
      const kind = el instanceof HTMLTextAreaElement ? 'textarea' : el.type === 'number' ? 'number' : 'text';
      add({ kind, els: [el], text, type: kind, options: [], required: isRequired([el], text), context: contextOf(el) });
    }

    for (const e of out) {
      e.value = JA.readValue(e);
      if (e.value) e.origin = 'existing'; // already filled when we got here: left alone
    }
    return out;
  };

  /** Current answer in a question's field(s) as text ('' when unanswered). */
  JA.readValue = (e) => {
    const placeholder = /^(select one|select|choose|please select|--)/i;
    switch (e.kind) {
      case 'radio': { const r = e.els.find((x) => x.checked); return r ? optionLabel(r) : ''; }
      case 'checkbox': return e.els[0].checked ? 'Yes' : '';
      case 'multi': return e.els.filter((x) => x.checked).map(optionLabel).join(', ');
      case 'select': { const s = e.els[0]; const o = s.options[s.selectedIndex]; return o && o.value !== '' && !placeholder.test(squash(o.text)) ? squash(o.text) : ''; }
      case 'listbox': { const t = squash(e.els[0].innerText || e.els[0].value || ''); return placeholder.test(t) ? '' : t; }
      default: return (e.els[0].value || '').trim();
    }
  };

  JA.optionLabel = optionLabel;
  JA.visible = visible;
})();
