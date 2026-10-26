export class ApiError extends Error {
  constructor(message, status, body, context = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    this.code = body?.code;
    this.traceId = body?.traceId;
    this.diagnostic = body?.diagnostic;
    this.validationErrors = body?.errors && !Array.isArray(body.errors) && typeof body.errors === 'object'
      ? body.errors : null;
    Object.assign(this, context);
  }
}

// Error envelopes can contain an 8 KiB provider excerpt plus JSON escaping and metadata.
// Bound error reads independently of successful responses such as rule and item pages.
async function readErrorResponse(response, limit = 64 * 1024) {
  if (!response.body) return { text: '', truncated: false };
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let text = '', received = 0, truncated = false;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      const available = Math.max(0, limit - received);
      text += decoder.decode(value.subarray(0, available), { stream: true });
      received += value.length;
      if (received > limit) { truncated = true; break; }
    }
    text += decoder.decode();
  } finally {
    if (truncated) await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
  return { text, truncated };
}

export async function request(path, { method = 'GET', body, signal } = {}) {
  let response;
  try {
    response = await fetch(path, {
      method, signal, credentials: 'same-origin', cache: 'no-store',
      headers: body === undefined ? { Accept: 'application/json' } : {
        Accept: 'application/json', 'Content-Type': 'application/json'
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) })
    });
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    throw new ApiError('Cannot reach Rules API. Check the connection and try again.', 0, null, { method, path });
  }
  let text, truncated = false;
  try {
    if (response.ok) text = await response.text();
    else ({ text, truncated } = await readErrorResponse(response));
  } catch (error) {
    if (error.name === 'AbortError') throw error;
    throw new ApiError(`Rules API returned HTTP ${response.status}, but its response could not be read.`,
      response.status, null, { method, path });
  }
  let data;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) {
    const validation = data?.errors && typeof data.errors === 'object'
      ? Object.entries(data.errors).map(([field, errors]) => `${field}: ${[].concat(errors).join(' ')}`).join(' ') : '';
    throw new ApiError(validation || data?.detail || data?.error || data?.title ||
      `Rules API request failed (HTTP ${response.status}).`, response.status, data, {
        method, path, responseBody: data === null ? text.slice(0, 8192) : null,
        responseBodyTruncated: truncated || (data === null && text.length > 8192)
      });
  }
  if (text && data === null) throw new ApiError('The API returned an unexpected response.', response.status, null,
    { method, path, responseBody: text.slice(0, 8192), responseBodyTruncated: text.length > 8192 });
  return data;
}

export const api = {
  options: () => request('/auto-runs/options'),
  pipelines: () => request('/pipelines'),
  rules: (pipeline, from, size, signal, search = '', isActive = '') => {
    const query = new URLSearchParams({ from, size });
    if (search) query.set('search', search);
    if (isActive !== '') query.set('isActive', isActive);
    return request(`/pipelines/${encodeURIComponent(pipeline)}/rules?${query}`, { signal });
  },
  rule: (pipeline, id, signal) => request(`/pipelines/${encodeURIComponent(pipeline)}/rules/${encodeURIComponent(id)}`, { signal }),
  preview: body => request('/auto-runs/preview', { method: 'POST', body }),
  list: () => request('/auto-runs'),
  deleteRun: id => request(`/auto-runs/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  deleteAllRuns: () => request('/auto-runs', { method: 'DELETE' }),
  run: id => request(`/auto-runs/${encodeURIComponent(id)}`),
  items: (id, from, size, outcome) => request(`/auto-runs/${encodeURIComponent(id)}/items?from=${from}&size=${size}${outcome ? `&outcome=${encodeURIComponent(outcome)}` : ''}`),
  start: id => request(`/auto-runs/${encodeURIComponent(id)}/start`, { method: 'POST' }),
  rerun: id => request(`/auto-runs/${encodeURIComponent(id)}/rerun`, { method: 'POST' }),
  cancel: id => request(`/auto-runs/${encodeURIComponent(id)}/cancel`, { method: 'POST' }),
  publication: (id, body) => request(`/auto-runs/${encodeURIComponent(id)}/publication`, { method: 'PATCH', body })
};
