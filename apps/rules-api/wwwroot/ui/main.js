import { registerRoute, startRouter } from './router.js';
import { showAutoRun, stopAutoRun } from './pages/auto-run.js';

document.addEventListener('ui:route-change', () => {
  stopAutoRun();
  document.getElementById('app').hidden = false;
  document.getElementById('coming-soon-page').hidden = true;
  const path = location.pathname.replace(/\/$/, '');
  let title = 'Auto Run';
  for (const link of document.querySelectorAll('.nav-item[data-route]')) {
    const active = path === link.pathname || path.startsWith(`${link.pathname}/`) || (path === '/ui' && link.pathname === '/ui/auto-run');
    link.classList.toggle('active', active);
    if (active) { link.setAttribute('aria-current', 'page'); title = link.dataset.pageTitle; }
    else link.removeAttribute('aria-current');
  }
  document.querySelector('.breadcrumb strong').textContent = title;
});

function showComingSoon(title) {
  document.getElementById('app').hidden = true;
  const page = document.getElementById('coming-soon-page');
  page.hidden = false;
  page.querySelector('h1').textContent = title;
  document.title = `${title} · Flarules`;
}

registerRoute(/^\/ui(?:\/auto-run)?$/, () => showAutoRun());
registerRoute(/^\/ui\/auto-run\/runs\/([^/]+)$/, id => showAutoRun(id));
registerRoute(/^\/ui\/rules$/, () => showComingSoon('Rules'));
registerRoute(/^\/ui\/rules-simulate$/, () => showComingSoon('Rules Simulate'));
startRouter();
