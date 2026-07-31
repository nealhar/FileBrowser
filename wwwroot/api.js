const root = "/api/files";

// Asynchronous helper that sends an HTTP request
async function request(url, options = {}) {
  const response = await fetch(url, options);
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    throw new Error(error.message || `Request failed (${response.status}).`);
  }
  return response.status === 204 ? null : response.json();
}

// Helper for constructing API URLs with query parameters
function url(route, values = {}) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) {
    if (value !== undefined && value !== null && value !== "") query.set(key, value);
  }
  return `${root}${route}?${query}`;
}

// Creates fetch options for JSON Post request
const json = body => ({
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body)
});

// Creates and exports an object containing all frontend API operations
export const api = {
  browse: (path, signal) => request(url("/browse", { path }), { signal }),
  search: (path, query, signal) =>
    request(url("/search", { path, query }), { signal }),
  download: path => url("/download", { path }),
  upload(path, files) {
    const body = new FormData();
    for (const file of files) body.append("files", file);
    return request(url("/upload", { path }), { method: "POST", body });
  },
  folder: (parentPath, name) =>
    request(`${root}/folders`, json({ parentPath, name })),
  delete: (path, recursive) =>
    request(url("/", { path, recursive }), { method: "DELETE" }),
  transfer: (operation, sourcePath, destinationPath) =>
    request(`${root}/${operation}`,
      json({ sourcePath, destinationPath, overwrite: false }))
};
