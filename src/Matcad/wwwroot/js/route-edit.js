/* The route editor: explains what the chosen type means, offers a wildcard where a single host would get a certificate of its own,
   and lets a redirect target be picked from the existing domains. The domains that a wildcard covers come with the form (data-wildcard-parents). */
(function () {
  'use strict';

  var form = document.getElementById('route-form');
  var host = document.getElementById('host-input');
  if (!form || !host) { return; }

  var $ = function (id) { return document.getElementById(id); };
  var wildcardParents = [];
  try { wildcardParents = JSON.parse(form.getAttribute('data-wildcard-parents') || '[]'); } catch (e) { /* no wildcard known */ }

  var routeType = function () { var on = form.querySelector('input[name="RouteType"]:checked'); return on ? on.value : 'single'; };
  var parentOf = function (h) { var i = h.indexOf('.'); return i > 0 ? h.slice(i + 1) : ''; };

  // Warn when a single host (subdomain or apex) is not covered by any wildcard route, so it would get its own individual certificate.
  function checkWildcard() {
    var nudge = $('wildcard-nudge');
    var h = (host.value || '').trim().toLowerCase().replace(/\.+$/, '');
    var labels = h.split('.').filter(Boolean).length;
    var internal = h === 'localhost' || /\.localhost$/.test(h) || /^[0-9.]+$/.test(h);
    // A subdomain (3+ labels) needs *.<immediate parent>; an apex (2 labels) is folded into its own *.<host> certificate.
    var base = labels >= 3 ? parentOf(h) : h;
    var covered = wildcardParents.some(function (p) { return p.toLowerCase() === base; });
    if (routeType() === 'single' && labels >= 2 && h.indexOf('*.') !== 0 && !internal && !covered) {
      $('wildcard-nudge-text').textContent =
        'No wildcard covers "' + h + '", so it gets a certificate of its own. A *.' + base + ' wildcard route would cover it.';
      nudge.setAttribute('data-parent', base);
      // Converting replaces this host, so it is only offered for a subdomain.
      $('wildcard-nudge-btn').hidden = labels < 3;
      nudge.hidden = false;
    } else {
      nudge.hidden = true;
    }
  }

  function applyRouteType() {
    if (routeType() === 'wildcard') {
      // Make the wildcard explicit: the host starts with "*.".
      var h = host.value.trim();
      if (h.indexOf('*.') !== 0) { host.value = '*.' + h.replace(/^[*.]+/, ''); }
      host.placeholder = '*.example.com';
      $('host-hint').textContent = 'Covers every direct subdomain. Needs a DNS provider.';
    } else {
      if (host.value.trim().indexOf('*.') === 0) { host.value = host.value.trim().slice(2); }
      host.placeholder = 'app.example.com';
      $('host-hint').textContent = 'One host. Its certificate comes from the HTTP challenge or a wildcard route.';
    }
    checkWildcard();
  }

  $('wildcard-nudge-btn').addEventListener('click', function () {
    var parent = $('wildcard-nudge').getAttribute('data-parent');
    if (!parent) { return; }
    var wildcard = $('route-type-wildcard');
    wildcard.checked = true;
    host.value = '*.' + parent;
    wildcard.dispatchEvent(new Event('change', { bubbles: true }));
  });

  // Redirect target: a picker of existing domains fills the address; "Another address" reveals the field for free text.
  var url = $('FallbackUrl'), preset = $('redirect-preset'), urlGroup = $('redirect-url-group');
  function applyPreset() {
    var v = preset.value;
    if (v === '__other__') { urlGroup.hidden = false; url.focus(); }
    else { urlGroup.hidden = true; url.value = v; }
  }
  function initPreset() {
    var value = (url.value || '').trim();
    if (!value) { preset.value = ''; urlGroup.hidden = true; }
    else if (Array.prototype.some.call(preset.options, function (o) { return o.value === value; })) { preset.value = value; urlGroup.hidden = true; }
    else { preset.value = '__other__'; urlGroup.hidden = false; }
  }

  Array.prototype.forEach.call(form.querySelectorAll('input[name="RouteType"]'), function (r) { r.addEventListener('change', applyRouteType); });
  host.addEventListener('input', checkWildcard);
  preset.addEventListener('change', applyPreset);
  applyRouteType();
  initPreset();

  // Prefilled as ".example.com" (Add subdomain): the cursor goes to the start, so the user types the label first.
  if (host.value.charAt(0) === '.') { host.focus(); host.setSelectionRange(0, 0); }
})();
