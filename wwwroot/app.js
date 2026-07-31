import { api } from "./api.js";

// Helper to retrive element by ID
const $ = id => document.getElementById(id);

// store UI element references
const ui = {
  browser: $("file-browser-dialog"),
  rows: $("file-rows"),
  crumbs: $("breadcrumbs"),
  empty: $("empty-state"),
  loading: $("loading-area"),
  error: $("error-area"),
  summary: $("status-summary"),
  limit: $("limit-notice"),
  selectedBar: $("selected-bar"),
  selectedName: $("selected-name"),
  search: $("search-input"),
  upload: $("upload-input"),
  action: $("action-dialog"),
  actionForm: $("action-form"),
  actionInput: $("action-input"),
  recursive: $("recursive-input")
};

// Creates in-memory cache for recently loaded directory results
const cache = new Map();
let route;
let result;
let selected;
let activeRequest;
let busy = false;

// Parses current URL hash into application state
function readRoute() {
  const [mode = "browse", query = ""] =
    (location.hash.slice(2) || "browse").split("?");
  const values = new URLSearchParams(query);
  return {
    mode: mode === "search" ? "search" : "browse",
    path: (values.get("path") || "").replaceAll("\\", "/").replace(/^\/+|\/+$/g, ""),
    query: values.get("query") || "",
    open: values.get("dialog") === "open"
  };
}

// Inverse of readRoute - turns application state into URL hash
function hash(next) {
  const values = new URLSearchParams();
  if (next.path) values.set("path", next.path);
  if (next.mode === "search" && next.query) values.set("query", next.query);
  if (next.open) values.set("dialog", "open");
  return `#/${next.mode}${values.size ? `?${values}` : ""}`;
}

// Navigation helper
function go(changes) {
  const next = { ...(route || readRoute()), ...changes };
  const destination = hash(next);
  if (location.hash === destination) load();
  else location.hash = destination;
}

// Main route loading function
async function load(force = false) {
  route = readRoute();
  if (route.open && !ui.browser.open) ui.browser.showModal();
  if (!route.open && ui.browser.open) ui.browser.close();
  if (!route.open) return activeRequest?.abort();

  selected = null;
  showSelection();
  showError();
  ui.search.value = route.mode === "search" ? route.query : "";
  drawCrumbs();

  // Browse cache lookup
  const key = `browse:${route.path}`;
  const saved = cache.get(key);
  if (!force && route.mode === "browse" && saved && Date.now() - saved.time < 5000) {
    result = saved.data;
    return draw();
  }

  // start network request
  activeRequest?.abort();
  activeRequest = new AbortController();
  setLoading(true);
  try {
    result = route.mode === "search"
      ? await api.search(route.path, route.query, activeRequest.signal)
      : await api.browse(route.path, activeRequest.signal);
    if (route.mode === "browse") cache.set(key, { time: Date.now(), data: result });
    draw();
  } catch (error) {
    if (error.name !== "AbortError") showError(error.message);
  } finally {
    setLoading(false);
  }
}

// Renders navigation buttons representing each path segment
function drawCrumbs() {
  const fragment = document.createDocumentFragment();
  let path = "";
  for (const name of ["Home", ...route.path.split("/").filter(Boolean)]) {
    if (name !== "Home") path = path ? `${path}/${name}` : name;
    if (fragment.childNodes.length) fragment.append(text("span", "/", "crumb-separator"));
    const button = text("button", name);
    button.type = "button";
    button.dataset.path = path;
    fragment.append(button);
  }
  ui.crumbs.replaceChildren(fragment);
}

// Renders current result
function draw() {
  const fragment = document.createDocumentFragment();
  for (const item of result.items) fragment.append(row(item));
  ui.rows.replaceChildren(fragment);
  ui.empty.hidden = result.items.length > 0;
  ui.limit.hidden = !(route.mode === "search" && result.limitReached);
  ui.summary.textContent = `${result.folderCount} folders · ${result.fileCount} files · ${bytes(result.totalFileSizeBytes)}`;
}

// Creates complete table row for one file or folder
function row(item) {
  const tr = document.createElement("tr");
  tr.dataset.path = item.relativePath;

  const selectCell = tr.insertCell();
  selectCell.className = "select-cell";
  const radio = document.createElement("input");
  radio.type = "radio";
  radio.name = "selected-item";
  radio.ariaLabel = `Select ${item.name}`;
  actionData(radio, "select", item.relativePath);
  selectCell.append(radio);

  const nameCell = tr.insertCell();
  const name = document.createElement("button");
  name.type = "button";
  name.className = "entry-name";
  actionData(name, item.isDirectory ? "open" : "select", item.relativePath);
  name.append(
    text("span", item.isDirectory ? "▸" : "·", `entry-icon ${item.isDirectory ? "folder" : "file"}`),
    text("span", item.name));
  nameCell.append(name);

  tr.insertCell().append(text("span", item.isDirectory ? "—" : bytes(item.sizeBytes), "muted"));
  tr.insertCell().append(text("span", date(item.lastModifiedUtc), "muted"));

  const actions = tr.insertCell();
  actions.className = "row-actions";
  const primary = text("button", item.isDirectory ? "Open" : "Download");
  primary.type = "button";
  actionData(primary, item.isDirectory ? "open" : "download", item.relativePath);
  actions.append(primary);
  return tr;
}

// Helper for plain text
function text(tag, value, className) {
  const element = document.createElement(tag);
  element.textContent = value;
  if (className) element.className = className;
  return element;
}

// Adds standardized action information to a clickable element
function actionData(element, action, path) {
  element.dataset.action = action;
  element.dataset.path = path;
}

// Sets one file or folder as selected
function select(item) {
  selected = item;
  for (const row of ui.rows.rows) {
    const active = row.dataset.path === item.relativePath;
    row.classList.toggle("selected", active);
    row.querySelector("input").checked = active;
  }
  showSelection();
}

// Synchronizes the selection controls with selected
function showSelection() {
  ui.selectedBar.hidden = !selected;
  if (!selected) return;
  ui.selectedName.textContent = selected.name;
  ui.selectedBar.querySelector('[data-selected-action="download"]').hidden =
    selected.isDirectory;
}

// Handles actions from table rows or the selected item action bar
async function act(action, path) {
  const item = result?.items.find(x => x.relativePath === path);
  if (!item || busy) return;
  if (action === "select") return select(item);
  if (action === "open") return go({ mode: "browse", path, query: "" });
  if (action === "download") return location.assign(api.download(path));
  if (action === "delete") return remove(item);
  if (action === "copy" || action === "move") return transfer(item, action);
}

// Asks user to confirm deletion and then performs it
async function remove(item) {
  const answer = await ask({
    title: `Delete “${item.name}”?`,
    description: item.isDirectory
      ? "Nonempty folders require recursive deletion."
      : "This permanently removes the file.",
    input: false,
    recursive: item.isDirectory,
    confirm: "Delete",
    destructive: true
  });
  if (answer) await mutate(
    () => api.delete(item.relativePath, answer.recursive), `${item.name} was deleted.`);
}

// Handles either copying or moving one selected item
async function transfer(item, operation) {
  const slash = item.relativePath.lastIndexOf("/");
  const parent = slash < 0 ? "" : item.relativePath.slice(0, slash);
  const name = operation === "copy" ? `Copy of ${item.name}` : item.name;
  const answer = await ask({
    title: `${operation === "copy" ? "Copy" : "Move"} “${item.name}”`,
    description: "Enter a path relative to the workspace root.",
    label: "Destination path",
    value: parent ? `${parent}/${name}` : name,
    confirm: operation === "copy" ? "Copy" : "Move"
  });
  if (answer) await mutate(
    () => api.transfer(operation, item.relativePath, answer.value),
    `${item.name} was ${operation === "copy" ? "copied" : "moved"}.`);
}

// Handles creation of a folder in the current directory
async function newFolder() {
  const answer = await ask({
    title: "Create a folder",
    description: `Location: ${route.path || "Home"}`,
    label: "Folder name",
    confirm: "Create"
  });
  if (answer) await mutate(
    () => api.folder(route.path, answer.value), `${answer.value} was created.`);
}

// Defines generic dialog function
function ask({ title, description, label = "", value = "", input = true,
  recursive = false, confirm, destructive = false }) {
  $("action-title").textContent = title;
  $("action-description").textContent = description;
  $("action-input-label").textContent = label;
  $("action-input-label").hidden = !input;
  ui.actionInput.hidden = !input;
  ui.actionInput.required = input;
  ui.actionInput.value = value;
  $("recursive-row").hidden = !recursive;
  ui.recursive.checked = false;
  $("confirm-action").textContent = confirm;
  $("confirm-action").classList.toggle("button-danger", destructive);
  $("action-error").hidden = true;
  ui.action.returnValue = "";
  ui.action.showModal();
  if (input) queueMicrotask(() => ui.actionInput.select());

  return new Promise(resolve => {
    const submit = event => {
      event.preventDefault();
      if (input && !ui.actionInput.value.trim()) {
        $("action-error").textContent = "This field is required.";
        $("action-error").hidden = false;
        return;
      }
      ui.action.close("confirm");
    };
    ui.actionForm.addEventListener("submit", submit);
    ui.action.addEventListener("close", () => {
      ui.actionForm.removeEventListener("submit", submit);
      resolve(ui.action.returnValue === "confirm"
        ? { value: ui.actionInput.value.trim(), recursive: ui.recursive.checked }
        : null);
    }, { once: true });
  });
}

// Centralizes common workflow for operations that change filesystem state
async function mutate(operation, message) {
  busy = true;
  controls(true);
  showError();
  try {
    await operation();
    cache.clear();
    await load(true);
    ui.summary.textContent = `${message} ${ui.summary.textContent}`;
  } catch (error) {
    showError(error.message);
  } finally {
    busy = false;
    controls(false);
  }
}

// Sets disabled state for interactive controls inside the browser dialog
function controls(disabled) {
  ui.browser.querySelectorAll("button, input").forEach(x => x.disabled = disabled);
}

// Updates the main error area
function showError(message = "") {
  ui.error.textContent = message;
  ui.error.hidden = !message;
}

// Updates visual loading indicators
function setLoading(loading) {
  ui.loading.hidden = !loading;
  $("refresh-browser").classList.toggle("spinning", loading);
}

// Converts a byte count into a readable string
function bytes(value) {
  if (!value) return value === 0 ? "0 B" : "—";
  const units = ["B", "KB", "MB", "GB", "TB"];
  const unit = Math.min(Math.floor(Math.log(value) / Math.log(1024)), 4);
  return `${(value / 1024 ** unit).toLocaleString(undefined,
    { maximumFractionDigits: unit ? 1 : 0 })} ${units[unit]}`;
}

// Converts a server timestamp into a localized display string
function date(value) {
  return new Intl.DateTimeFormat(undefined, {
    month: "short", day: "numeric", year: "numeric", hour: "numeric", minute: "2-digit"
  }).format(new Date(value));
}


// button event assignments
$("open-browser").onclick = () => go({ open: true });
$("close-browser").onclick = () => go({ open: false });
$("cancel-action").onclick = () => ui.action.close();
$("new-folder-button").onclick = newFolder;
$("refresh-browser").onclick = () => load(true);
$("upload-button").onclick = () => ui.upload.click();

ui.browser.addEventListener("cancel", event => {
  event.preventDefault();
  go({ open: false });
});
ui.crumbs.onclick = event => {
  const button = event.target.closest("[data-path]");
  if (button) go({ mode: "browse", path: button.dataset.path, query: "" });
};
ui.rows.onclick = event => {
  const target = event.target.closest("[data-action]");
  if (target) act(target.dataset.action, target.dataset.path);
};
ui.selectedBar.onclick = event => {
  const target = event.target.closest("[data-selected-action]");
  if (target && selected) act(target.dataset.selectedAction, selected.relativePath);
};
$("search-form").onsubmit = event => {
  event.preventDefault();
  const query = ui.search.value.trim();
  go(query ? { mode: "search", query } : { mode: "browse", query: "" });
};
ui.upload.onchange = async () => {
  const files = [...ui.upload.files];
  ui.upload.value = "";
  if (files.length) await mutate(
    () => api.upload(route.path, files), `${files.length} file(s) uploaded.`);
};

window.addEventListener("hashchange", () => load());
if (!location.hash) history.replaceState(null, "", "#/browse");
load();
