import { registerRoute, startRouter } from './router.js';
import { showAutoRun } from './pages/auto-run.js';

registerRoute(/^\/ui(?:\/auto-run)?$/, () => showAutoRun());
registerRoute(/^\/ui\/auto-run\/runs\/([^/]+)$/, id => showAutoRun(id));
startRouter();
