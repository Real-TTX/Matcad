// Shared UI behaviors. Loaded on every page via _Layout.

// --- Tab bars ---------------------------------------------------------------
// Markup convention (supports multiple independent tab groups per page):
//   <div data-tab-group>
//     <div class="tabbar">
//       <button class="tab active" data-tab="one">One</button>
//       <button class="tab" data-tab="two">Two</button>
//     </div>
//     <div class="tab-panel active" data-panel="one">…</div>
//     <div class="tab-panel" data-panel="two">…</div>
//   </div>
function initTabs(root) {
    root.querySelectorAll('[data-tab-group]').forEach(group => {
        const tabs = group.querySelectorAll('.tabbar .tab');
        const panels = group.querySelectorAll('.tab-panel');
        tabs.forEach(tab => tab.addEventListener('click', () => {
            const key = tab.getAttribute('data-tab');
            tabs.forEach(t => t.classList.toggle('active', t === tab));
            panels.forEach(p => p.classList.toggle('active', p.getAttribute('data-panel') === key));
        }));
    });
}

// --- Delete confirmation ----------------------------------------------------
// Any form with [data-confirm] asks before submitting.
function initConfirms(root) {
    root.querySelectorAll('form[data-confirm]').forEach(form => {
        if (form.dataset.confirmBound) return;
        form.dataset.confirmBound = '1';
        form.addEventListener('submit', e => {
            if (!confirm(form.getAttribute('data-confirm'))) e.preventDefault();
        });
    });
}

document.addEventListener('DOMContentLoaded', () => {
    initTabs(document);
    initConfirms(document);
});
