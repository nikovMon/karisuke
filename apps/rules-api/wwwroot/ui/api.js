export class ApiError extends Error {
  constructor(message, status, body) {
    super(message);
    this.status = status;
    this.body = body;
  }
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
    throw new ApiError('Cannot reach Rules API. Check the connection and try again.', 0);
  }
  const text = await response.text();
  let data;
  try { data = text ? JSON.parse(text) : null; } catch { data = null; }
  if (!response.ok) {
    const validation = data?.errors && typeof data.errors === 'object'
      ? Object.values(data.errors).flat().join(' ') : '';
    throw new ApiError(validation || data?.detail || data?.error || data?.title ||
      `Request failed (${response.status}).`, response.status, data);
  }
  if (text && data === null) throw new ApiError('The API returned an unexpected response.', response.status);
  return data;
}

export const api = {
  options: () => request('/auto-runs/options'),
  pipelines: () => request('/pipelines'),
  rules: (pipeline, from, size) => request(`/pipelines/${encodeURIComponent(pipeline)}/rules?from=${from}&size=${size}`),
  rule: (pipeline, id) => request(`/pipelines/${encodeURIComponent(pipeline)}/rules/${encodeURIComponent(id)}`),
  preview: body => request('/auto-runs/preview', { method: 'POST', body }),
  list: () => request('/auto-runs'),
  run: id => request(`/auto-runs/${encodeURIComponent(id)}`),
  items: (id, from, size, outcome) => request(`/auto-runs/${encodeURIComponent(id)}/items?from=${from}&size=${size}${outcome ? `&outcome=${encodeURIComponent(outcome)}` : ''}`),
  start: id => request(`/auto-runs/${encodeURIComponent(id)}/start`, { method: 'POST' }),
  cancel: id => request(`/auto-runs/${encodeURIComponent(id)}/cancel`, { method: 'POST' }),
  publication: (id, body) => request(`/auto-runs/${encodeURIComponent(id)}/publication`, { method: 'PATCH', body })
};
