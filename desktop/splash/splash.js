'use strict';

(function initSplash() {
  const versionEl = document.getElementById('splash-version');
  const statusEl = document.getElementById('splash-status');

  function applyState(state) {
    if (!state || typeof state !== 'object') return;
    if (typeof state.version === 'string' && state.version.trim() && versionEl) {
      versionEl.textContent = `v${state.version.trim().replace(/^v/i, '')}`;
    }
    if (typeof state.text === 'string' && state.text.trim() && statusEl) {
      statusEl.textContent = state.text.trim();
    }
  }

  const params = new URLSearchParams(window.location.search);
  applyState({
    version: params.get('v') || undefined,
    text: params.get('text') || undefined,
  });

  if (window.makoSplash && typeof window.makoSplash.onState === 'function') {
    window.makoSplash.onState(applyState);
  }
})();
