/* Matcad service worker.
   What it does: makes the app installable, keeps its static files (styles, scripts, icons) available, and shows a
   friendly page with automatic reconnect when Matcad cannot be reached.
   What it deliberately does not do: store pages. They are signed-in views of a live system (routes, certificates,
   credentials), so an old copy would be wrong at best and a leftover on a shared device at worst. Nothing is queued
   for later either: a change to a reverse proxy must happen now, or not at all.

   Registered as /sw.js?v=<app version>; a new version gives a new cache and removes the old one. */

const VERSION = new URL(self.location.href).searchParams.get('v') || '0';
const CACHE = 'matcad-static-' + VERSION;
const OFFLINE_URL = '/offline.html';
const PRECACHE = [OFFLINE_URL, '/manifest.webmanifest', '/img/logo.svg', '/img/favicon.svg', '/img/icon-192.png'];

// A reverse proxy in front of Matcad answers with these while Matcad restarts.
const GATEWAY_ERRORS = [502, 503, 504];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k.startsWith('matcad-') && k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const request = event.request;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) { return; }

  if (request.mode === 'navigate') {
    event.respondWith(navigate(request));
  } else if (request.method === 'GET' && isStaticAsset(url.pathname)) {
    event.respondWith(staticAsset(request, url));
  }
  // Everything else (the live log stream, /healthz, form posts, API calls) goes straight to the network.
});

function isStaticAsset(path) {
  return path.startsWith('/css/') || path.startsWith('/js/') || path.startsWith('/img/') || path === '/manifest.webmanifest';
}

async function navigate(request) {
  try {
    const response = await fetch(request);
    return GATEWAY_ERRORS.includes(response.status) ? offlinePage(request) : response;
  } catch (error) {
    return offlinePage(request);
  }
}

async function offlinePage(request) {
  if (request.method !== 'GET') {
    // Do not replay a form post after reconnecting: send the user back to the page that holds the form.
    const from = request.referrer || '/';
    return Response.redirect(OFFLINE_URL + '?to=' + encodeURIComponent(from), 303);
  }
  return (await caches.match(OFFLINE_URL)) || Response.error();
}

// Files with ?v=<hash> never change under the same URL: cache first. The rest is served from the cache and refreshed in the background.
async function staticAsset(request, url) {
  const cache = await caches.open(CACHE);
  const cached = await cache.match(request);
  const versioned = url.searchParams.has('v');
  if (cached && versioned) { return cached; }

  const refresh = fetch(request).then((response) => {
    if (response.ok) { cache.put(request, response.clone()); }
    return response;
  });
  if (cached) { refresh.catch(() => { }); return cached; }
  try { return await refresh; } catch (error) { return Response.error(); }
}
