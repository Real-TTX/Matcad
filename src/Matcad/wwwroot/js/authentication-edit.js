/* The login page branding of an authentication: the preview follows the fields while they change.
   The accent re-colours the preview alone, like a free colour does the page of the toolkit: the text on it is white or dark, whichever reads better. */
(function () {
  'use strict';

  var scope = document.getElementById('preview-scope');
  if (!scope) { return; }

  var $ = function (id) { return document.getElementById(id); };
  var field = function (name) { return document.querySelector('[data-preview="' + name + '"]'); };

  var color = field('color');
  var logoUrl = field('logo');
  var logoFile = $('LogoFile');
  var logoImg = $('preview-logo');
  var logoFallback = $('preview-logo-fallback');

  function luminance(hex) {
    var v = [1, 3, 5].map(function (i) {
      var c = parseInt(hex.substr(i, 2), 16) / 255;
      return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
  }

  function onAccent(hex) {
    var l = luminance(hex);
    var onWhite = 1.05 / (l + 0.05), onInk = (l + 0.05) / (luminance('#0b1020') + 0.05);
    return onWhite >= 4.5 || onWhite >= onInk ? '#ffffff' : '#0b1020';
  }

  function applyColor() {
    var v = (color.value || '').trim();
    if (/^#[0-9a-fA-F]{6}$/.test(v)) {
      scope.style.setProperty('--mat-app-accent', v);
      scope.style.setProperty('--mat-app-on-accent', onAccent(v));
    } else {
      scope.style.removeProperty('--mat-app-accent');
      scope.style.removeProperty('--mat-app-on-accent');
    }
  }

  function bindText(name, target, fallback) {
    var source = field(name);
    source.addEventListener('input', function () { target.textContent = source.value.trim() || fallback; });
  }

  function applyLogo(src) {
    if (src) { logoImg.src = src; logoImg.hidden = false; logoFallback.hidden = true; }
    else { logoImg.removeAttribute('src'); logoImg.hidden = true; logoFallback.hidden = false; }
  }

  bindText('title', $('preview-title'), 'Matcad');
  bindText('text', $('preview-text'), 'Protected area - please sign in');

  // The colour picker of the toolkit writes the value and says so with these events.
  color.addEventListener('input', applyColor);
  color.addEventListener('change', applyColor);

  Array.prototype.forEach.call(document.querySelectorAll('input[name="BrandLogoLayout"]'), function (r) {
    r.addEventListener('change', function () { $('preview-brand').classList.toggle('mc-brand--top', r.value === 'top' && r.checked); });
  });

  logoUrl.addEventListener('input', function () { applyLogo(logoUrl.value.trim()); });
  logoFile.addEventListener('change', function () {
    var file = logoFile.files && logoFile.files[0];
    if (!file) { applyLogo(logoUrl.value.trim()); return; }
    var reader = new FileReader();
    reader.onload = function (e) { applyLogo(e.target.result); };
    reader.readAsDataURL(file);
  });

  applyColor();
})();
