/* Matcad behaviour: what this app adds to the toolkit (matui.js does the theme, menus, dialogs, tabs and forms).
   Plain JavaScript, no build step; behaviour is switched on by attributes, so several of the same thing can live on one page. */
(function () {
  'use strict';

  var $ = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };
  var root = document.documentElement;

  // ------------------------------------------------------------------ the colour of the browser bar follows the page
  // The page colour changes with the theme (a choice, or the system); the bar of an installed app should keep its colour.

  var barColors = $$('meta[name="theme-color"]');
  function syncBarColor() {
    var color = getComputedStyle(root).getPropertyValue('--mat-bg').trim();
    if (!color) { return; }
    barColors.forEach(function (meta) { meta.setAttribute('content', color); });
  }
  syncBarColor();
  root.addEventListener('mat:theme', syncBarColor);
  if (window.matchMedia) { window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', syncBarColor); }

  // ------------------------------------------------------------------ bars of a chart: <div data-mc-value="40"> is 40 % high

  $$('[data-mc-value]').forEach(function (bar) {
    var value = Math.max(0, Math.min(100, parseFloat(bar.getAttribute('data-mc-value')) || 0));
    bar.style.setProperty('--mc-value', value + '%');
  });

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

  // ------------------------------------------------------------------ connection state
  // The page that is already open stays usable, but it says so when the server cannot be reached, keeps
  // trying to reconnect, and refreshes itself afterwards so nothing stale is shown as current.

  var banner = document.getElementById('conn-banner');
  var bannerAlert = document.getElementById('conn-alert');
  var bannerText = document.getElementById('conn-banner-text');
  var retryBtn = banner && $('[data-conn-retry]', banner);
  var offline = false;
  var offlineSince = 0;
  var dirty = false;
  var retryDelay = 2000;
  var probeTimer = null;

  // Typing into a form must never be thrown away by an automatic refresh.
  document.addEventListener('input', function (e) { if (e.target.closest && e.target.closest('form[method="post"]')) { dirty = true; } });
  document.addEventListener('submit', function (e) { if (!e.defaultPrevented) { dirty = false; } });

  function showBanner(state, text, buttonText) {
    if (!banner) { return; }
    banner.setAttribute('data-state', state);
    bannerAlert.className = 'mat-alert mat-alert--' + (state === 'online' ? 'success' : 'warning');
    var use = $('use', bannerAlert);
    if (use) { use.setAttribute('href', '#mat-i-' + (state === 'online' ? 'check-circle' : 'alert')); }
    bannerText.textContent = text;
    if (retryBtn) { retryBtn.querySelector('span').textContent = buttonText; retryBtn.hidden = false; }
    banner.hidden = false;
  }

  function goOffline() {
    if (offline) { return; }
    offline = true;
    offlineSince = Date.now();
    retryDelay = 2000;
    showBanner('offline', 'No connection to Matcad. What you see may be out of date. Reconnecting...', 'Retry');
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
      showBanner('online', 'Back online. Refreshing...', 'Reload');
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
    if (!banner || !e.target.closest('[data-conn-retry]')) { return; }
    if (banner.getAttribute('data-state') === 'online') { location.reload(); } else { probe(); }
  });

  // Forms cannot be sent while the server is away: say so instead of losing the input to a browser error page.
  document.addEventListener('submit', function (e) {
    if (!offline) { return; }
    e.preventDefault();
    dirty = true;
    showBanner('offline', 'No connection. Your changes were not sent; they stay on this page until Matcad is reachable again.', 'Retry');
  }, true);

  window.matcadConnection = { markOffline: goOffline, probe: probe };

  // ------------------------------------------------------------------ live request log (dashboard)

  var stream = document.getElementById('log-stream');
  if (stream && window.EventSource) {
    var statusEl = document.getElementById('log-status');
    var empty = stream.querySelector('.mc-log__empty');
    var setStatus = function (live) {
      if (!statusEl) { return; }
      statusEl.className = 'mat-pill mc-log__status ' + (live ? 'mat-pill--ok' : 'mat-pill--warn');
      statusEl.textContent = live ? 'Live' : 'Reconnecting';
    };
    var pad = function (n) { return (n < 10 ? '0' : '') + n; };
    var cell = function (text, cls) {
      var span = document.createElement('span');
      span.textContent = text == null ? '' : String(text);
      if (cls) { span.className = cls; }
      return span;
    };

    var addLine = function (e) {
      var d = new Date(e.timestamp + (/[zZ]|[+-]\d\d:?\d\d$/.test(e.timestamp) ? '' : 'Z'));
      var row = document.createElement('div');
      row.className = 'mc-log__line';
      row.appendChild(cell(pad(d.getHours()) + ':' + pad(d.getMinutes()) + ':' + pad(d.getSeconds()), 'mc-log__muted'));
      row.appendChild(cell(e.status, 'mc-log__st-' + Math.floor(e.status / 100)));
      row.appendChild(cell(e.method));
      row.appendChild(cell((e.host || '') + (e.path || '')));
      row.appendChild(cell(e.remoteIp, 'mc-log__muted'));
      row.appendChild(cell(Math.round(e.durationMs) + ' ms', 'mc-log__muted'));
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
    var version = root.getAttribute('data-app-version') || '0';
    navigator.serviceWorker.register('/sw.js?v=' + encodeURIComponent(version), { scope: '/' }).catch(function () { /* optional feature */ });
  }
})();
