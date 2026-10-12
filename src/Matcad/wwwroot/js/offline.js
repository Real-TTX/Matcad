/* The offline page: checks whether Matcad is back, with growing pauses, and returns to the page that was asked for. */
(function () {
  'use strict';

  var status = document.getElementById('status');
  var button = document.getElementById('retry');
  var delay = 3000, timer = null, checking = false;

  // After a failed form post the service worker sends us here with ?to=<the page that held the form>.
  // Only addresses on this site are followed.
  function target() {
    var to = new URLSearchParams(location.search).get('to');
    if (to) {
      try { var u = new URL(to, location.origin); if (u.origin === location.origin && u.pathname !== '/offline.html') { return u.href; } } catch (e) { /* not an address */ }
    }
    return /\/offline\.html$/.test(location.pathname) ? '/' : location.href;
  }

  function check() {
    if (checking) { return; }
    checking = true; button.disabled = true; status.textContent = 'Checking the connection...';
    clearTimeout(timer);
    var ctl = window.AbortController ? new AbortController() : null;
    var abort = setTimeout(function () { if (ctl) { ctl.abort(); } }, 6000);
    fetch('/healthz', { cache: 'no-store', credentials: 'omit', signal: ctl ? ctl.signal : undefined })
      .then(function (r) {
        clearTimeout(abort);
        if (!r.ok) { throw new Error('HTTP ' + r.status); }
        status.textContent = 'Connected. Loading...';
        location.replace(target());
      })
      .catch(function () {
        clearTimeout(abort);
        checking = false; button.disabled = false;
        status.textContent = 'Still offline. Trying again in ' + Math.round(delay / 1000) + ' seconds.';
        timer = setTimeout(check, delay);
        delay = Math.min(delay * 2, 30000);
      });
  }

  button.addEventListener('click', function () { delay = 3000; check(); });
  window.addEventListener('online', function () { delay = 3000; check(); });
  document.addEventListener('visibilitychange', function () { if (!document.hidden) { check(); } });
  check();
})();
