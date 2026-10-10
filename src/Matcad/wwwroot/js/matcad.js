/* Matcad UI behaviour. Plain JavaScript, no build step.
   Behaviour is switched on by data-* attributes, so several of the same control can live on one page. */
(function () {
  'use strict';

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  // ------------------------------------------------------------------ theme (system / light / dark)

  var THEME_KEY = 'matcad-theme';

  function applyTheme(pref) {
    var dark = pref === 'dark' || (pref === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
    document.documentElement.setAttribute('data-theme-pref', pref);
    $$('[data-theme-set]').forEach(function (b) { b.classList.toggle('is-active', b.getAttribute('data-theme-set') === pref); });
  }

  function currentPref() { try { return localStorage.getItem(THEME_KEY) || 'system'; } catch (e) { return 'system'; } }

  applyTheme(currentPref());
  if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
      if (currentPref() === 'system') { applyTheme('system'); }
    });
  }

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-theme-set]');
    if (!btn) { return; }
    var pref = btn.getAttribute('data-theme-set');
    try { localStorage.setItem(THEME_KEY, pref); } catch (err) { /* private mode: the choice just lasts for this page */ }
    applyTheme(pref);
  });

  // ------------------------------------------------------------------ scrollbars only while scrolling

  var scrollTimers = new WeakMap();
  document.addEventListener('scroll', function (e) {
    var el = e.target === document ? document.documentElement : e.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(scrollTimers.get(el));
    scrollTimers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 700));
  }, true);

  // ------------------------------------------------------------------ mobile menu closes after a tap on a link

  document.addEventListener('click', function (e) {
    var toggle = document.getElementById('nav-toggle');
    if (toggle && e.target.closest('.sidebar a.nav-link')) { toggle.checked = false; }
  });

  // ------------------------------------------------------------------ confirm dialogs
  // <form data-confirm="Really?">, <button data-confirm> (submit buttons) and <a data-confirm>.

  function confirmDialog(message, okText, danger) {
    return new Promise(function (resolve) {
      var opener = document.activeElement;
      var backdrop = document.createElement('div');
      backdrop.className = 'dialog-backdrop';
      backdrop.innerHTML =
        '<div class="dialog" role="alertdialog" aria-modal="true" aria-labelledby="dialog-title">' +
        '<h3 id="dialog-title"></h3><p></p>' +
        '<div class="mat-buttons"><button type="button" class="mat-btn mat-btn--ghost" data-no>Cancel</button>' +
        '<button type="button" class="mat-btn ' + (danger ? 'mat-btn--solid-danger' : 'mat-btn--primary') + '" data-yes></button></div></div>';
      backdrop.querySelector('h3').textContent = danger ? 'Are you sure?' : 'Please confirm';
      backdrop.querySelector('p').textContent = message;
      backdrop.querySelector('[data-yes]').textContent = okText || (danger ? 'Delete' : 'Confirm');
      function close(result) {
        backdrop.remove(); document.removeEventListener('keydown', onKey);
        if (opener && opener.focus) { opener.focus(); }
        resolve(result);
      }
      function onKey(ev) { if (ev.key === 'Escape') { close(false); } }
      backdrop.addEventListener('click', function (ev) {
        if (ev.target === backdrop || ev.target.closest('[data-no]')) { close(false); }
        else if (ev.target.closest('[data-yes]')) { close(true); }
      });
      document.addEventListener('keydown', onKey);
      document.body.appendChild(backdrop);
      backdrop.querySelector('[data-yes]').focus();
    });
  }

  function isDanger(el) {
    return el.hasAttribute('data-confirm-danger') || el.classList.contains('mat-btn--danger');
  }

  document.addEventListener('submit', function (e) {
    var form = e.target;
    if (form.__confirmed || (e.defaultPrevented)) { return; }
    var submitter = e.submitter;
    var source = submitter && submitter.hasAttribute('data-confirm') ? submitter : form;
    var message = source.getAttribute && source.getAttribute('data-confirm');
    if (!message) { return; }
    e.preventDefault();
    confirmDialog(message, source.getAttribute('data-confirm-ok'), isDanger(source)).then(function (ok) {
      if (!ok) { return; }
      form.__confirmed = true;
      if (form.requestSubmit && submitter) { form.requestSubmit(submitter); } else { form.submit(); }
    });
  });

  document.addEventListener('click', function (e) {
    var link = e.target.closest('a[data-confirm]');
    if (!link || link.__confirmed) { return; }
    e.preventDefault();
    confirmDialog(link.getAttribute('data-confirm'), link.getAttribute('data-confirm-ok'), isDanger(link)).then(function (ok) {
      if (ok) { link.__confirmed = true; link.click(); }
    });
  });

  // ------------------------------------------------------------------ tabs
  // <div data-tab-group><div class="mat-tabs"><button class="mat-tabs__tab is-active" data-tab="a">..</button></div>
  //   <div class="tab-panel is-active" data-panel="a">..</div></div>

  $$('[data-tab-group]').forEach(function (group) {
    // Only this group's own tabs and panels: a group can sit inside a panel of another one.
    var tabs = $$(':scope > .mat-tabs .mat-tabs__tab', group);
    var panels = $$(':scope > .tab-panel', group);
    tabs.forEach(function (tab) {
      tab.addEventListener('click', function () {
        var key = tab.getAttribute('data-tab');
        tabs.forEach(function (t) { t.classList.toggle('is-active', t === tab); });
        panels.forEach(function (p) { p.classList.toggle('is-active', p.getAttribute('data-panel') === key); });
      });
    });
  });

  // ------------------------------------------------------------------ copy to clipboard: <button data-copy-from="#element">

  document.addEventListener('click', function (e) {
    var btn = e.target.closest('[data-copy-from]');
    var source = btn && $(btn.getAttribute('data-copy-from'));
    if (!source) { return; }
    var label = $('span', btn);
    var done = function () {
      var old = label.textContent;
      label.textContent = 'Copied';
      setTimeout(function () { label.textContent = old; }, 1500);
    };
    if (navigator.clipboard && window.isSecureContext) { navigator.clipboard.writeText(source.textContent).then(done); return; }
    var area = document.createElement('textarea');
    area.value = source.textContent;
    document.body.appendChild(area);
    area.select();
    try { document.execCommand('copy'); done(); } catch (err) { /* nothing to do */ }
    area.remove();
  });

  // ------------------------------------------------------------------ dependent fields: data-show-if="#selector" data-show-value="x"

  function refreshDependents() {
    $$('[data-show-if]').forEach(function (el) {
      var ctl = $(el.getAttribute('data-show-if'));
      if (!ctl) { return; }
      var wanted = el.getAttribute('data-show-value');
      var value = ctl.type === 'checkbox' ? String(ctl.checked) : ctl.value;
      if (ctl.type === 'radio') { var on = $('input[name="' + ctl.name + '"]:checked'); value = on ? on.value : ''; }
      el.hidden = wanted === null ? !value : value !== wanted;
    });
  }
  document.addEventListener('change', refreshDependents);
  refreshDependents();

  // ------------------------------------------------------------------ DNS provider fields: the type picker shows the credentials of that type

  document.addEventListener('change', function (e) {
    var select = e.target.closest('[data-cred-select]');
    if (!select) { return; }
    $$('.cred-block').forEach(function (block) { block.hidden = block.getAttribute('data-cred-type') !== select.value; });
  });

  // ------------------------------------------------------------------ editable table rows
  // <button data-add-row="#table tbody" data-template="row-template-id"> appends a copy of the <template>;
  // <button data-remove-row> inside a row removes it.

  document.addEventListener('click', function (e) {
    var add = e.target.closest('[data-add-row]');
    if (add) {
      var template = document.getElementById(add.getAttribute('data-template'));
      var body = $(add.getAttribute('data-add-row'));
      if (template && body) {
        body.appendChild(template.content.cloneNode(true));
        var input = body.querySelector('tr:last-child input');
        if (input) { input.focus(); }
      }
    }
    var remove = e.target.closest('[data-remove-row]');
    if (remove) { remove.closest('tr').remove(); }
  });

  // ------------------------------------------------------------------ list toolbars: submit on change, search with a short delay

  $$('form.js-autosubmit').forEach(function (form) {
    var timer;
    function submit() { try { sessionStorage.setItem('matcad-focus-search', form.querySelector('input[type=search]') ? '1' : ''); } catch (e) { } form.submit(); }
    form.addEventListener('change', function (e) { if (e.target.matches('select')) { submit(); } });
    form.addEventListener('input', function (e) {
      if (e.target.matches('input[type=search]')) { clearTimeout(timer); timer = setTimeout(submit, 450); }
    });
  });
  try {
    if (sessionStorage.getItem('matcad-focus-search') === '1') {
      var search = $('form.js-autosubmit input[type=search]');
      if (search) { search.focus(); var len = search.value.length; search.setSelectionRange(len, len); }
    }
    sessionStorage.removeItem('matcad-focus-search');
  } catch (e) { }

  // ------------------------------------------------------------------ times in the browser's time zone: <time data-local datetime="...Z">

  $$('time[data-local]').forEach(function (el) {
    var d = new Date(el.getAttribute('datetime'));
    if (isNaN(d.getTime())) { return; }
    var kind = el.getAttribute('data-local');
    el.textContent = kind === 'short'
      ? d.toLocaleString([], { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' })
      : kind === 'date' ? d.toLocaleDateString() : d.toLocaleString();
    el.title = d.toISOString();
  });

  // ------------------------------------------------------------------ connection state
  // The page that is already open stays usable, but it says so when the server cannot be reached, keeps
  // trying to reconnect, and refreshes itself afterwards so nothing stale is shown as current.

  var banner = document.getElementById('conn-banner');
  var bannerText = document.getElementById('conn-banner-text');
  var retryBtn = banner && $('[data-conn-retry]', banner);
  var offline = false;
  var offlineSince = 0;
  var dirty = false;
  var retryDelay = 2000;
  var probeTimer = null;

  // Typing into a form must never be thrown away by an automatic refresh.
  document.addEventListener('input', function (e) { if (e.target.closest && e.target.closest('form:not(.js-autosubmit):not(.logout-form)')) { dirty = true; } });
  document.addEventListener('submit', function (e) { if (!e.defaultPrevented) { dirty = false; } });

  function showBanner(state, text, buttonText) {
    if (!banner) { return; }
    banner.setAttribute('data-state', state);
    bannerText.textContent = text;
    if (retryBtn) { retryBtn.querySelector('span').textContent = buttonText; retryBtn.hidden = false; }
    banner.hidden = false;
  }

  function goOffline() {
    if (offline) { return; }
    offline = true;
    offlineSince = Date.now();
    retryDelay = 2000;
    showBanner('offline', 'No connection to Matcad. What you see may be out of date - reconnecting...', 'Retry');
    document.dispatchEvent(new CustomEvent('matcad:offline'));
    scheduleProbe();
  }

  function goOnline() {
    if (!offline) { return; }
    offline = false;
    clearTimeout(probeTimer);
    var away = Date.now() - offlineSince;
    document.dispatchEvent(new CustomEvent('matcad:online', { detail: { away: away } }));
    if (!dirty) {
      showBanner('online', 'Back online - refreshing...', 'Reload');
      location.reload();
    } else {
      // There are unsaved changes on this page: let the user decide.
      showBanner('online', 'Back online. Reload to see the latest data (your unsaved changes would be lost).', 'Reload');
    }
  }

  function probe() {
    var ctl = window.AbortController ? new AbortController() : null;
    var timeout = setTimeout(function () { if (ctl) { ctl.abort(); } }, 6000);
    return fetch('/healthz', { cache: 'no-store', credentials: 'omit', signal: ctl ? ctl.signal : undefined })
      .then(function (r) { clearTimeout(timeout); if (!r.ok) { throw new Error('HTTP ' + r.status); } goOnline(); })
      .catch(function () { clearTimeout(timeout); goOffline(); if (offline) { scheduleProbe(); } });
  }

  function scheduleProbe() {
    clearTimeout(probeTimer);
    probeTimer = setTimeout(probe, retryDelay);
    retryDelay = Math.min(retryDelay * 2, 30000);
  }

  window.addEventListener('offline', goOffline);
  window.addEventListener('online', function () { if (offline) { probe(); } });
  document.addEventListener('visibilitychange', function () { if (!document.hidden && offline) { probe(); } });
  document.addEventListener('click', function (e) {
    if (!e.target.closest('[data-conn-retry]')) { return; }
    if (banner.getAttribute('data-state') === 'online') { location.reload(); } else { probe(); }
  });

  // Forms cannot be sent while the server is away: say so instead of losing the input to a browser error page.
  document.addEventListener('submit', function (e) {
    if (!offline) { return; }
    e.preventDefault();
    dirty = true;
    showBanner('offline', 'No connection - your changes were not sent. They stay on this page until Matcad is reachable again.', 'Retry');
    if (banner.scrollIntoView) { banner.scrollIntoView({ block: 'nearest' }); }
  }, true);

  window.matcadConnection = { markOffline: goOffline, probe: probe };

  // ------------------------------------------------------------------ live request log (dashboard)

  var stream = document.getElementById('log-stream');
  if (stream && window.EventSource) {
    var statusEl = document.getElementById('log-status');
    var empty = stream.querySelector('.log-empty');
    var setStatus = function (live) {
      if (!statusEl) { return; }
      statusEl.className = 'pill ' + (live ? 'pill--online' : 'pill--warn');
      statusEl.textContent = live ? 'Live' : 'Reconnecting';
    };
    var esc = function (s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); };
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };

    var addLine = function (e) {
      var d = new Date(e.timestamp + (/[zZ]|[+-]\d\d:?\d\d$/.test(e.timestamp) ? '' : 'Z'));
      var row = document.createElement('div');
      row.className = 'log-line';
      row.innerHTML =
        '<span class="text-muted">' + pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()) + '</span>' +
        '<span class="st-' + Math.floor(e.status / 100) + '">' + esc(e.status) + '</span>' +
        '<span>' + esc(e.method) + '</span>' +
        '<span>' + esc(e.host) + esc(e.path) + '</span>' +
        '<span class="text-muted">' + esc(e.remoteIp) + '</span>' +
        '<span class="text-muted">' + Math.round(e.durationMs) + ' ms</span>';
      stream.insertBefore(row, stream.firstChild);
      while (stream.childElementCount > 200) { stream.removeChild(stream.lastChild); }
    };

    document.addEventListener('matcad:offline', function () { setStatus(false); });
    var es = new EventSource('/logs/stream');
    // Every (re)connection starts with the server's latest entries again: begin from an empty list, so nothing shows twice.
    es.onopen = function () {
      stream.textContent = '';
      if (empty) { stream.appendChild(empty); }
      setStatus(true);
      if (offline) { probe(); }
    };
    es.onmessage = function (ev) {
      try { addLine(JSON.parse(ev.data)); if (empty && empty.parentNode) { empty.remove(); } } catch (err) { /* skip a damaged line */ }
    };
    es.onerror = function () {
      setStatus(false);
      // EventSource retries by itself; the probe tells whether the whole server is gone or just this stream.
      probe();
    };
  }

  // ------------------------------------------------------------------ service worker (installable app, offline page)
  // Needs a secure context (HTTPS or localhost); elsewhere the app still works as a normal website.

  if ('serviceWorker' in navigator && window.isSecureContext) {
    var version = document.documentElement.getAttribute('data-app-version') || '0';
    navigator.serviceWorker.register('/sw.js?v=' + encodeURIComponent(version), { scope: '/' }).catch(function () { /* optional feature */ });
  }
})();
