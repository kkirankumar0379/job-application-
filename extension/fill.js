// Runs inside the application page (every frame). Fills what it can from window.__JOBAGENT_DATA__ and returns a report.
// It never clicks Next/Submit and never touches passwords.
(async () => {
  const D = window.__JOBAGENT_DATA__;
  if (!D) return null;
  const { profile: p, answers = [], resume } = D;
  const report = { filled: [], skipped: [], resume: null };

  const norm = (s) => (s || '').toLowerCase().replace(/[\s_\-*:()/]+/g, ' ').trim();
  const STATES = {
    AL: 'Alabama', AK: 'Alaska', AZ: 'Arizona', AR: 'Arkansas', CA: 'California', CO: 'Colorado', CT: 'Connecticut',
    DE: 'Delaware', FL: 'Florida', GA: 'Georgia', HI: 'Hawaii', ID: 'Idaho', IL: 'Illinois', IN: 'Indiana', IA: 'Iowa',
    KS: 'Kansas', KY: 'Kentucky', LA: 'Louisiana', ME: 'Maine', MD: 'Maryland', MA: 'Massachusetts', MI: 'Michigan',
    MN: 'Minnesota', MS: 'Mississippi', MO: 'Missouri', MT: 'Montana', NE: 'Nebraska', NV: 'Nevada', NH: 'New Hampshire',
    NJ: 'New Jersey', NM: 'New Mexico', NY: 'New York', NC: 'North Carolina', ND: 'North Dakota', OH: 'Ohio',
    OK: 'Oklahoma', OR: 'Oregon', PA: 'Pennsylvania', RI: 'Rhode Island', SC: 'South Carolina', SD: 'South Dakota',
    TN: 'Tennessee', TX: 'Texas', UT: 'Utah', VT: 'Vermont', VA: 'Virginia', WA: 'Washington', WV: 'West Virginia',
    WI: 'Wisconsin', WY: 'Wyoming', DC: 'District of Columbia',
  };

  const stateAbbr = (p.state || '').toUpperCase();
  const stateFull = STATES[stateAbbr] || p.state || '';
  const isUS = /^(us|usa|united states( of america)?)$/i.test((p.country || '').trim());

  // Each rule: profile value (plus alternative spellings for dropdowns), key pattern, and patterns that rule it out.
  const RULES = [
    { id: 'First name', value: p.firstName, match: /(first|given) ?name|firstname|fname|legalnamesection firstname/, not: /last|middle|preferred|nick/ },
    { id: 'Last name', value: p.lastName, match: /(last|family|sur) ?name|lastname|lname|legalnamesection lastname/, not: /first|middle/ },
    { id: 'Email', value: p.email, match: /e ?mail/, not: /confirm|verify|re ?enter|reference|friend|referr/, type: 'email' },
    { id: 'Phone', value: p.phone, match: /phone|mobile|cell\b|\btel\b/, not: /country|code|extension|type|device/, type: 'tel' },
    { id: 'City', value: p.city, match: /\bcity\b|\btown\b/, not: /birth/ },
    { id: 'State', value: stateFull, alts: [stateAbbr, stateFull], match: /\bstate\b|province|countryregion|region/, not: /country|united states|statement|birth/ },
    { id: 'Country', value: isUS ? 'United States' : p.country, alts: isUS ? ['United States', 'United States of America', 'USA', 'US'] : [p.country], match: /country/, not: /code|phone|birth|citizen/ },
    { id: 'LinkedIn', value: p.linkedInUrl, match: /linkedin/ },
    { id: 'Years of experience', value: p.yearsOfExperience != null ? String(p.yearsOfExperience) : '', match: /years? of (professional |relevant |total |work )?experience|total experience|experience.*\byears\b/, textOnly: true },
    { id: 'Full name', value: `${p.firstName} ${p.lastName}`.trim(), match: /^(full |legal |your )?name$|\bfull name\b|\byour name\b/, not: /company|school|employer|job|user|reference|file|first|last|preferred/ },
  ];

  // Demographic / legal questions are never guessed. (A saved answer for one still gets used.)
  const NEVER_GUESS = /gender|race|ethnic|veteran|disabilit|pronoun|sexual|orientation|religio|date of birth|birth|ssn|social security|password|captcha|salary|compensation|sponsor|authori[sz]ed|visa|criminal|felony|signature/;

  function labelText(el) {
    const parts = [];
    if (el.labels) for (const l of el.labels) parts.push(l.innerText);
    const by = el.getAttribute('aria-labelledby');
    if (by) by.split(/\s+/).forEach((id) => { const n = document.getElementById(id); if (n) parts.push(n.innerText); });
    parts.push(el.getAttribute('aria-label') || '', el.placeholder || '');
    if (!parts.join('').trim()) {
      const wrap = el.closest('fieldset, [class*="field"], [class*="question"], [class*="form-group"], li, div');
      const lab = wrap && wrap.querySelector('label, legend, [class*="label"]');
      if (lab) parts.push(lab.innerText);
    }
    return parts.join(' ').replace(/\s+/g, ' ').trim();
  }

  function keyOf(el) {
    return norm([labelText(el), el.name, el.id, el.getAttribute('autocomplete'), el.getAttribute('data-automation-id'),
      el.getAttribute('data-qa'), el.getAttribute('data-testid')].filter(Boolean).join(' ').replace(/([a-z])([A-Z])/g, '$1 $2'));
  }

  const usable = (el) => {
    if (el.disabled || el.readOnly) return false;
    const r = el.getBoundingClientRect();
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none' && (r.width > 0 || r.height > 0);
  };

  function setValue(el, value) {
    const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
      : el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
    el.focus();
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value); // bypasses React's value tracking
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.dispatchEvent(new Event('blur', { bubbles: true }));
    el.style.outline = '2px solid #00c389';
  }

  function pickOption(select, wanted) {
    const w = wanted.map(norm).filter(Boolean);
    const opts = [...select.options].filter((o) => o.value !== '' || o.text.trim() !== '');
    return opts.find((o) => w.includes(norm(o.text)) || w.includes(norm(o.value)))
      || opts.find((o) => w.some((x) => norm(o.text).startsWith(x)));
  }

  function fillWith(el, label, value, alts) {
    if (value == null || String(value).trim() === '') return false;
    if (el instanceof HTMLSelectElement) {
      const o = pickOption(el, alts || [value]);
      if (!o) return false;
      setValue(el, o.value);
    } else {
      if (el.value && el.value.trim()) return false; // never overwrite what's already there
      setValue(el, String(value));
    }
    report.filled.push({ field: label, value: String(value).length > 60 ? String(value).slice(0, 57) + '…' : String(value) });
    return true;
  }

  function savedAnswerFor(text) {
    const t = norm(text);
    if (!t) return null;
    return answers.find((a) => {
      const pat = (a.questionPattern || '').trim();
      if (!pat) return false;
      try { if (new RegExp(pat, 'i').test(text)) return true; } catch { /* not a regex */ }
      return t.includes(norm(pat));
    }) || null;
  }

  // ---- text inputs, textareas, selects ----
  const fields = [...document.querySelectorAll('input, textarea, select')].filter((el) => {
    if (el instanceof HTMLInputElement && ['hidden', 'password', 'checkbox', 'radio', 'file', 'submit', 'button', 'image', 'reset', 'date'].includes(el.type)) return false;
    return usable(el);
  });

  for (const el of fields) {
    const label = labelText(el) || el.name || el.id || 'field';
    const key = keyOf(el);

    const saved = savedAnswerFor(labelText(el));
    if (saved) {
      if (saved.isSensitive || saved.requiresReviewEveryTime) { report.skipped.push({ field: label, reason: 'saved answer needs your review' }); continue; }
      if (fillWith(el, label, saved.answer, [saved.answer])) continue;
    }
    if (NEVER_GUESS.test(key)) { report.skipped.push({ field: label, reason: 'sensitive question, left for you' }); continue; }

    const rule = RULES.find((r) => (r.match.test(key) || (r.type && el.type === r.type)) && !(r.not && r.not.test(key))
      && !(r.textOnly && el instanceof HTMLSelectElement));
    if (rule) fillWith(el, rule.id, rule.value, rule.alts);
  }

  // ---- custom dropdowns (Workday and similar: a button that opens a listbox instead of a real <select>) ----
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const PHONE_TYPE = { id: "Phone device type", alts: ["Mobile", "Cell", "Cellular", "Mobile Phone"], match: /phone device type|device type|phone type/ };
  const CUSTOM_RULES = [RULES.find((r) => r.id === "Country"), RULES.find((r) => r.id === "State"), PHONE_TYPE];

  async function chooseOption(button, wanted) {
    const w = wanted.map(norm).filter(Boolean);
    button.click();
    for (let i = 0; i < 15; i++) {
      await sleep(100);
      const opts = [...document.querySelectorAll('[role="option"], [role="listbox"] li')].filter(usable);
      if (!opts.length) continue;
      const hit = opts.find((o) => w.includes(norm(o.innerText))) || opts.find((o) => w.some((x) => norm(o.innerText).startsWith(x)));
      if (hit) { hit.click(); await sleep(150); return true; }
      break;
    }
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); // close the list, leave the field alone
    return false;
  }

  const dropdowns = [...document.querySelectorAll('button[aria-haspopup="listbox"], [role="combobox"]:not(input)')].filter(usable);
  for (const btn of dropdowns) {
    const current = norm(btn.innerText || btn.value || "");
    if (current && !/^(select one|select|choose|please select)/.test(current)) continue; // already has a value
    const label = labelText(btn) || btn.getAttribute("aria-label") || "dropdown";
    const key = keyOf(btn);
    if (NEVER_GUESS.test(key)) { report.skipped.push({ field: label, reason: "sensitive question, left for you" }); continue; }
    const rule = CUSTOM_RULES.find((r) => r && r.match.test(key) && !(r.not && r.not.test(key)));
    if (!rule) continue;
    const wanted = rule.alts || [rule.value];
    if (wanted.every((x) => !x)) continue;
    if (await chooseOption(btn, wanted)) report.filled.push({ field: rule.id, value: String(wanted[0]) });
  }

  // ---- radio groups with saved answers (e.g. "Are you legally authorized to work…?" → Yes) ----
  const groups = new Map();
  for (const r of document.querySelectorAll('input[type=radio]')) if (r.name && usable(r)) (groups.get(r.name) || groups.set(r.name, []).get(r.name)).push(r);
  for (const radios of groups.values()) {
    if (radios.some((r) => r.checked)) continue;
    const q = (radios[0].closest('fieldset')?.querySelector('legend')?.innerText) || labelText(radios[0].closest('[class*="question"], [class*="field"], div') || radios[0]);
    const saved = savedAnswerFor(q);
    if (!saved || saved.isSensitive || saved.requiresReviewEveryTime) continue;
    const want = norm(saved.answer);
    const hit = radios.find((r) => { const t = norm((r.labels && r.labels[0]?.innerText) || r.value); return t === want || t.startsWith(want); });
    if (hit) { hit.click(); report.filled.push({ field: q.slice(0, 60), value: saved.answer }); }
  }

  // ---- resume upload ----
  const files = [...document.querySelectorAll('input[type=file]')].filter((f) => !f.disabled);
  if (resume && files.length) {
    const bytes = Uint8Array.from(atob(resume.b64), (c) => c.charCodeAt(0));
    for (const input of files) {
      const key = keyOf(input);
      const wantsResume = /resume|\bcv\b|curriculum/.test(key) || (files.length === 1 && !/cover|photo|image|picture|transcript|portfolio/.test(key));
      if (!wantsResume || /cover/.test(key) || input.files.length) continue;
      const dt = new DataTransfer();
      dt.items.add(new File([bytes], resume.name, { type: resume.type }));
      input.files = dt.files;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      input.dispatchEvent(new Event('change', { bubbles: true }));
      report.resume = resume.name;
      break;
    }
  }

  return report;
})();
