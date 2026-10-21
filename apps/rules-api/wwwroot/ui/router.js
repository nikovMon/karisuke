// Add a route descriptor here when the console grows beyond auto-run.
const routes = [];
export function registerRoute(pattern, handler) { routes.push({ pattern, handler }); }
export function navigate(path) {
  if (location.pathname !== path) history.pushState(null, '', path);
  dispatch();
}
function dispatch() {
  const path = location.pathname.replace(/\/$/, '') || '/ui';
  for (const route of routes) {
    const match = path.match(route.pattern);
    if (match) return route.handler(...match.slice(1).map(decodeURIComponent));
  }
  document.getElementById('app').innerHTML = '<div class="empty-state"><h1>Page not found</h1><p>Return to the auto-run workspace.</p><a href="/ui/auto-run" data-route class="button primary">Open auto-run</a></div>';
}
export function startRouter() {
  document.addEventListener('click', event => {
    const anchor = event.target.closest('a[data-route]');
    if (!anchor || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    if (new URL(anchor.href).origin !== location.origin) return;
    event.preventDefault();
    navigate(new URL(anchor.href).pathname);
  });
  window.addEventListener('popstate', dispatch);
  dispatch();
}
