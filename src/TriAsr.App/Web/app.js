"use strict";
// Mockingbird Client Webview, the web page of a Mockingbird server (ADR-0015): send a recording, follow the recordings on the server, review and edit a transcript, export it.
// It talks to the same HTTP API as every other client, from the same address, and the transcripts are only ever put on the page as text (never as markup).
// Every text is looked up by its English wording: t(text) (the server answers /ui/strings.json with the translations of the ones used here).
(() => {
  const PRODUCT = "Mockingbird Client Webview";   // the name of this page, whatever kind of server it comes from
  const LANGUAGES = [["en", "English"], ["hu", "Magyar"], ["de", "Deutsch"], ["es", "Español"], ["fr", "Français"]];
  const app = document.getElementById("app");
  const text = { lang: "en", table: {} };
  const t = (key, ...args) => {
    let value = text.table[key] || key;
    args.forEach((argument, index) => { value = value.split("{" + index + "}").join(String(argument)); });
    return value;
  };

  const keep = {
    get(area, key) { try { return window[area].getItem(key); } catch { return null; } },
    set(area, key, value) { try { if (value === null) window[area].removeItem(key); else window[area].setItem(key, value); } catch { /* storage may be blocked */ } }
  };

  const state = {
    health: null, info: null, password: keep.get("sessionStorage", "mb-password") || "",
    tab: "new", jobs: [], languages: [], file: null, review: null, timer: 0, sending: false, dirty: false
  };
  const ui = {};

  // ---- small helpers -------------------------------------------------------------------------------------------------------------------

  function h(tag, props, ...children) {
    const element = document.createElement(tag);
    let value;
    for (const [name, v] of Object.entries(props || {})) {
      if (v === undefined || v === null || v === false) continue;
      if (name === "class") element.className = v;
      else if (name === "value") value = v;
      else if (name.startsWith("on")) element.addEventListener(name.slice(2).toLowerCase(), v);
      else if (["checked", "disabled", "hidden", "selected", "multiple"].includes(name)) element[name] = v;
      else element.setAttribute(name, v === true ? "" : v);
    }
    for (const child of children.flat(2)) {
      if (child === undefined || child === null || child === false) continue;
      element.append(child.nodeType ? child : document.createTextNode(String(child)));
    }
    if (value !== undefined) element.value = value; // after the options of a select exist
    return element;
  }

  class ApiError extends Error {
    constructor(status, code, message) { super(message); this.status = status; this.code = code; }
  }

  async function api(path, options = {}) {
    const headers = { ...(options.headers || {}) };
    if (state.password) headers["Authorization"] = "Bearer " + state.password;
    let response;
    try { response = await fetch(path, { ...options, headers, cache: "no-store" }); }
    catch { throw new ApiError(0, "unreachable", t("Could not reach the server.")); }
    if (!response.ok) {
      let body = null;
      try { body = await response.json(); } catch { /* not JSON */ }
      const error = (body && body.error) || {};
      throw new ApiError(response.status, error.code || "error", error.message || response.statusText);
    }
    return response;
  }
  const getJson = async (path) => (await api(path)).json();

  /** Runs an action; a refused password goes back to the password page, anything else is shown on the page. */
  async function guard(action, describe) {
    try { return await action(); }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) { signOut(error.code === "too_many_attempts" ? t("Too many wrong passwords. Try again in a minute.") : t("The password is wrong.")); return undefined; }
      notice("error", describe ? describe(error.message) : error.message);
      return undefined;
    }
  }

  function notice(kind, message) {
    if (!ui.notice) return;
    ui.notice.replaceChildren();
    if (!message) return;
    ui.notice.append(h("div", { class: "note " + (kind === "error" ? "error" : kind === "warn" ? "warn" : ""), role: kind === "error" ? "alert" : "status" }, message));
  }

  const pad = (number, length = 2) => String(number).padStart(length, "0");
  function clock(milliseconds) {
    const total = Math.max(0, Math.floor(milliseconds / 1000));
    const hours = Math.floor(total / 3600), minutes = Math.floor(total / 60) % 60, seconds = total % 60;
    return (hours ? hours + ":" + pad(minutes) : minutes) + ":" + pad(seconds);
  }

  // ---- the language of the page --------------------------------------------------------------------------------------------------------

  async function chooseLanguage(code) {
    if (!LANGUAGES.some(([known]) => known === code)) code = "en";
    let table = {};
    if (code !== "en") {
      try { table = await (await fetch("/ui/strings.json?lang=" + code, { cache: "no-store" })).json(); } catch { table = {}; }
    }
    text.lang = code; text.table = table;
    document.documentElement.lang = code;
    keep.set("localStorage", "mb-lang", code);
  }

  function initialLanguage() {
    const saved = keep.get("localStorage", "mb-lang");
    if (saved) return saved;
    const wanted = (navigator.language || "en").slice(0, 2).toLowerCase();
    return LANGUAGES.some(([code]) => code === wanted) ? wanted : "en";
  }

  // ---- the frame: header, then either the password page or the workspace -------------------------------------------------------------

  function renderHeader() {
    const health = state.health;
    const bar = h("header", { class: "bar" },
      h("div", { class: "brand" },
        h("span", { class: "mark", "aria-hidden": "true" }, svgMark()),
        h("span", null, PRODUCT),
        health && h("span", { class: "version" }, "v" + health.version)),
      h("span", { class: "spacer" }),
      health && h("span", { class: "pill" }, health.name),
      health && health.encrypted && h("span", { class: "pill", title: t("Connections are encrypted") }, t("encrypted")),
      h("select", { "aria-label": t("Interface language"), onChange: async (event) => { await chooseLanguage(event.target.value); await remount(); } },
        LANGUAGES.map(([code, name]) => h("option", { value: code, selected: code === text.lang }, name))),
      health && health.passwordRequired && state.password && h("button", { class: "btn", type: "button", onClick: () => signOut("") }, t("Sign out")));
    ui.header.className = "bar";
    ui.header.replaceChildren(...bar.childNodes);
    document.title = health ? PRODUCT + " · " + health.name : PRODUCT;
  }

  function svgMark() {
    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 32 32");
    const path = document.createElementNS(ns, "path");
    path.setAttribute("d", "M8 16V10M13 23V5M18 19V9M23 16v-4");
    svg.append(path);
    return svg;
  }

  function signOut(message) {
    stopPolling();
    state.password = ""; keep.set("sessionStorage", "mb-password", null);
    state.review = null; state.dirty = false;
    renderHeader();
    showLogin(message);
  }

  function showLogin(message) {
    ui.frame.querySelector("[data-tabs]").replaceChildren();
    ui.main.replaceChildren(
      h("div", { class: "card login stack" },
        h("h1", null, t("Password")),
        h("p", { class: "muted" }, t("This server is protected by a password.")),
        message && h("div", { class: "note error", role: "alert" }, message),
        h("form", { class: "stack", onSubmit: async (event) => {
          event.preventDefault();
          const entered = event.target.elements.password.value;
          if (!entered) return;
          state.password = entered;
          try {
            state.info = await getJson("/v1/server");
            keep.set("sessionStorage", "mb-password", entered);
            renderHeader();
            await showWorkspace();
          } catch (error) {
            state.password = "";
            showLogin(error instanceof ApiError && error.code === "too_many_attempts" ? t("Too many wrong passwords. Try again in a minute.")
              : error instanceof ApiError && error.status === 401 ? t("The password is wrong.") : error.message);
          }
        } },
          h("div", null, h("label", { for: "password" }, t("Password")), h("input", { id: "password", name: "password", type: "password", autocomplete: "current-password", autofocus: true })),
          h("div", { class: "row" }, h("button", { class: "btn primary", type: "submit" }, t("Connect"))))));
    const field = ui.main.querySelector("input");
    if (field) field.focus();
  }

  async function remount() {
    renderHeader();
    if (!state.health) return;
    if (state.health.passwordRequired && !state.password) { showLogin(""); return; }
    await showWorkspace();
  }

  // ---- the workspace -------------------------------------------------------------------------------------------------------------------

  async function showWorkspace() {
    stopPolling();
    ui.notice = h("div");
    ui.newView = h("section", { id: "view-new" });
    ui.projectsView = h("section", { id: "view-projects" });
    ui.reviewView = h("section", { id: "view-review" });
    ui.tabs = h("nav", { class: "tabs", role: "tablist" });
    ui.main.replaceChildren(ui.notice, ui.newView, ui.projectsView, ui.reviewView);
    ui.frame.querySelector("[data-tabs]").replaceChildren(ui.tabs);
    await guard(async () => {
      state.info = await getJson("/v1/server");
      const catalog = await getJson("/v1/languages");
      state.languages = catalog.data;
    });
    buildNew();
    renderTabs();
    selectTab(state.tab);
    renderInfoNotice();
    await refreshJobs();
    if (state.review) await openReview(state.review.id, true);
    startPolling();
  }

  function renderTabs() {
    const items = [["new", t("New transcription")], ["projects", t("Projects")], ["review", t("Review")]];
    ui.tabs.replaceChildren(...items.map(([id, label]) => h("button", { class: "tab", type: "button", role: "tab", id: "tab-" + id, "aria-selected": String(state.tab === id), onClick: () => selectTab(id) }, label)));
  }

  function selectTab(id) {
    state.tab = id;
    for (const [name, view] of [["new", ui.newView], ["projects", ui.projectsView], ["review", ui.reviewView]]) view.hidden = name !== id;
    for (const tab of ui.tabs.children) tab.setAttribute("aria-selected", String(tab.id === "tab-" + id));
    if (id === "projects") renderProjects();
    if (id === "review") renderReview();
  }

  function renderInfoNotice() {
    const info = state.info;
    if (info && !info.modelsReady) notice("warn", t("This server cannot transcribe yet: its speech models are missing ({0}). Set it up on the server first.", (info.missingModels || []).join(", ")));
    else notice("", "");
  }

  function startPolling() {
    stopPolling();
    state.timer = window.setInterval(async () => {
      if (document.visibilityState !== "visible" || !state.password && state.health && state.health.passwordRequired) return;
      await refreshJobs(true);
    }, 2500);
  }
  function stopPolling() { if (state.timer) { window.clearInterval(state.timer); state.timer = 0; } }

  // ---- send a recording ----------------------------------------------------------------------------------------------------------------

  function buildNew() {
    const input = h("input", { type: "file", accept: "audio/*,video/*,.mkv,.m4a,.flac,.opus,.ogg", hidden: true, "aria-label": t("Select media file"),
      onChange: () => choose(input.files && input.files[0]) });
    const chosen = h("p", { class: "muted", "aria-live": "polite" }, "");
    const progress = h("progress", { max: "100", value: "0", hidden: true, "aria-label": t("Upload progress") });
    const status = h("p", { "aria-live": "polite" }, "");
    const language = h("select", { id: "language", "aria-label": t("Speech language") },
      languageOptions());
    const send = h("button", { class: "btn primary", type: "button", disabled: true, onClick: () => sendFile() }, t("Send to server"));
    const drop = h("div", { class: "drop" },
      h("strong", null, t("Drop audio or video here")),
      h("div", { class: "muted" }, t("WAV, MP3, M4A, FLAC, MP4, MKV and more")),
      h("p", null, h("button", { class: "btn", type: "button", onClick: () => input.click() }, t("Select file"))),
      chosen);
    drop.addEventListener("dragover", (event) => { event.preventDefault(); drop.classList.add("over"); });
    drop.addEventListener("dragleave", () => drop.classList.remove("over"));
    drop.addEventListener("drop", (event) => { event.preventDefault(); drop.classList.remove("over"); const file = event.dataTransfer && event.dataTransfer.files[0]; if (file) choose(file); });
    Object.assign(ui, { fileInput: input, chosen, progress, sendStatus: status, language, send });
    ui.newView.replaceChildren(h("h1", null, t("New transcription")),
      h("div", { class: "card stack" }, drop, input,
        h("div", null, h("label", { for: "language" }, t("Language")), language),
        h("div", { class: "row" }, send),
        progress, status));
    if (state.file) choose(state.file);
  }

  function languageOptions() {
    const names = new Intl.Collator(text.lang);
    const entries = state.languages.filter((language) => language.code !== "auto")
      .map((language) => ({ code: language.code, label: t(language.name) + " (" + language.code + ")" }))
      .sort((first, second) => names.compare(first.label, second.label));
    entries.unshift({ code: "auto", label: t("Auto-detect language") });
    return entries.map((entry) => h("option", { value: entry.code }, entry.label));
  }

  function choose(file) {
    state.file = file || null;
    ui.chosen.textContent = file ? file.name : "";
    ui.send.disabled = !file || state.sending || !!(state.info && !state.info.modelsReady);
    ui.sendStatus.textContent = "";
  }

  function sendFile() {
    const file = state.file;
    if (!file) { notice("error", t("No recording selected") + ": " + t("Choose an existing audio or video file first.")); return; }
    state.sending = true; ui.send.disabled = true; ui.progress.hidden = false; ui.progress.value = 0;
    ui.sendStatus.textContent = t("Sending to {0}…", state.health.name);
    const request = new XMLHttpRequest();
    request.open("POST", "/v1/transcriptions?language=" + encodeURIComponent(ui.language.value) + "&name=" + encodeURIComponent(file.name));
    if (state.password) request.setRequestHeader("Authorization", "Bearer " + state.password);
    request.setRequestHeader("Content-Type", file.type || "application/octet-stream");
    request.upload.addEventListener("progress", (event) => { if (event.lengthComputable) ui.progress.value = (event.loaded * 100) / event.total; });
    const done = (failure) => {
      state.sending = false; ui.progress.hidden = true;
      if (failure) { ui.sendStatus.textContent = t("The recording could not be sent: {0}", failure); choose(file); return; }
      state.file = null; ui.chosen.textContent = ""; ui.fileInput.value = "";
      ui.sendStatus.textContent = t("Sent. The server is working on it.");
      ui.send.disabled = true;
      selectTab("projects"); refreshJobs();
    };
    request.addEventListener("load", () => {
      if (request.status === 202 || request.status === 200) { done(null); return; }
      if (request.status === 401) { signOut(t("The password is wrong.")); return; }
      let message = request.statusText;
      try { message = JSON.parse(request.responseText).error.message; } catch { /* keep the status text */ }
      done(message);
    });
    request.addEventListener("error", () => done(t("Could not reach the server.")));
    request.send(file);
  }

  // ---- the recordings on the server ----------------------------------------------------------------------------------------------------

  async function refreshJobs(quiet) {
    try {
      const response = await api("/v1/transcriptions");
      state.jobs = (await response.json()).data || [];
      if (ui.lost) { ui.lost = false; renderInfoNotice(); }
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) { signOut(t("The password is wrong.")); return; }
      if (!quiet || !ui.lost) { ui.lost = true; notice("error", t("The connection was lost: {0}", error.message)); }
      return;
    }
    if (state.tab === "projects") renderProjects();
  }

  function stateLabel(job) {
    switch (job.state) {
      case "queued": return t("Queued");
      case "complete": return t("Complete");
      case "failed": return t("Failed");
      case "cancelled": return t("Cancelled");
      default: return job.stage ? t(job.stage) : t("Preparing transcription");
    }
  }

  function renderProjects() {
    const list = h("ul", { class: "jobs", "aria-label": t("Recordings on the server") },
      state.jobs.map((job) => h("li", { class: "job" },
        h("div", null,
          h("div", { class: "name" }, job.name),
          h("div", { class: "state" + (job.state === "failed" ? " failed" : "") }, stateLabel(job)),
          job.state === "running" && h("progress", { max: "100", value: String(job.percent), "aria-label": stateLabel(job) }),
          h("div", { class: "when" }, new Date(job.createdUtc).toLocaleString(text.lang)),
          job.error && h("div", { class: "state failed" }, job.error)),
        h("div", { class: "row" },
          job.state === "complete" && h("button", { class: "btn", type: "button", onClick: () => openReview(job.id) }, t("Open transcript")),
          (job.state === "queued" || job.state === "running") && h("button", { class: "btn", type: "button", onClick: () => cancelJob(job.id) }, t("Cancel"))))));
    ui.projectsView.replaceChildren(h("h1", null, t("Projects")),
      h("div", { class: "card" }, state.jobs.length ? list : h("p", { class: "muted" }, t("Nothing here yet. Finished transcriptions are listed here."))));
  }

  async function cancelJob(id) {
    await guard(async () => { await api("/v1/transcriptions/" + id + "/cancel", { method: "POST" }); await refreshJobs(); },
      (message) => t("Could not cancel the recording") + ": " + message);
  }

  // ---- review ----------------------------------------------------------------------------------------------------------------------------

  async function openReview(id, keepSelection) {
    await guard(async () => {
      const data = await getJson("/v1/transcriptions/" + id + "/review");
      const job = state.jobs.find((item) => item.id === id);
      const previous = keepSelection && state.review && state.review.id === id ? state.review : null;
      state.review = {
        id, name: job ? job.name : "", data, selected: previous ? Math.min(previous.selected, data.regions.length - 1) : 0, view: previous ? previous.view : "final",
        search: previous ? previous.search : "", only: previous ? previous.only : false, audio: previous ? previous.audio : null,
        regions: data.regions.map((region, index) => ({ index, region, original: region.finalText, text: region.finalText, automatic: data.automaticTexts[index] }))
      };
      state.dirty = false;
      selectTab("review");
      if (!previous || !previous.audio) loadAudio(id);
    }, (message) => t("Cannot open transcript") + ": " + message);
  }

  async function loadAudio(id) {
    if (!state.review || state.review.id !== id) return;
    ui.audioStatus && (ui.audioStatus.textContent = t("Getting the audio from the server…"));
    try {
      const blob = await (await api("/v1/transcriptions/" + id + "/audio")).blob();
      if (!state.review || state.review.id !== id) return;
      if (state.review.audio) URL.revokeObjectURL(state.review.audio);
      state.review.audio = URL.createObjectURL(blob);
      if (ui.player) { ui.player.src = state.review.audio; if (ui.audioStatus) ui.audioStatus.textContent = ""; }
    } catch (error) {
      if (ui.audioStatus) ui.audioStatus.textContent = t("The audio could not be fetched: {0}", error.message);
    }
  }

  const needsListening = (region) => region.source === "uncertain" || region.source === "single-asr-needs-listening" || (region.warnings && region.warnings.length > 0);
  const sourceLabel = (source) => source === "single-asr-needs-listening" ? t("one engine · needs listening") : source === "llm-arbitrated" ? t("chosen by the correction model") : t(source);

  function visibleRegions() {
    const review = state.review;
    const query = review.search.trim().toLowerCase();
    return review.regions.filter((entry) => (!review.only || needsListening(entry.region))
      && (!query || entry.text.toLowerCase().includes(query) || entry.region.whisperText.toLowerCase().includes(query) || entry.region.canaryText.toLowerCase().includes(query)));
  }

  function renderReview() {
    const review = state.review;
    if (!review) {
      ui.reviewView.replaceChildren(h("h1", null, t("Review")), h("p", { class: "muted" }, t("Open a finished recording from Projects to review its transcript.")));
      return;
    }
    const uncertain = review.regions.filter((entry) => needsListening(entry.region)).length;
    const summary = h("p", { class: "muted" }, t("{0} · regions: {1} · to listen to: {2}", review.name, review.regions.length, uncertain));
    const search = h("input", { type: "search", value: review.search, placeholder: t("Search transcript"), "aria-label": t("Search transcript"), onInput: (event) => { review.search = event.target.value; renderRegionList(); } });
    const only = h("input", { type: "checkbox", checked: review.only, id: "only", onChange: (event) => { review.only = event.target.checked; renderRegionList(); } });
    ui.saveButton = h("button", { class: "btn primary", type: "button", disabled: !state.dirty, onClick: () => saveEdits() }, t("Save edits"));
    const format = h("select", { "aria-label": t("Export…") }, [["txt", t("Text")], ["srt", t("SubRip subtitles")], ["vtt", "WebVTT"], ["md", "Markdown"], ["full-json", t("JSON with provenance")], ["csv", t("CSV comparison")], ["docx", t("Word document")]]
      .map(([value, label]) => h("option", { value }, label)));
    const mode = h("select", { "aria-label": t("Export mode"), title: t("Readable adjusts spacing without changing the spoken words. Strict Verbatim exports the current transcript without readability adjustments.") },
      [["strict", t("Strict Verbatim")], ["readable", t("Readable")]].map(([value, label]) => h("option", { value }, label)));
    const exportButton = h("button", { class: "btn", type: "button", onClick: () => exportTranscript(format.value, mode.value) }, t("Export…"));
    ui.regionList = h("ul", { class: "regions", role: "listbox", "aria-label": t("Transcript regions") });
    ui.editor = h("div");
    ui.audioStatus = h("p", { class: "muted", "aria-live": "polite" }, review.audio ? "" : t("Getting the audio from the server…"));
    ui.player = h("audio", { controls: true, preload: "auto", "aria-label": t("Audio position"), onError: () => { if (ui.player.src) ui.audioStatus.textContent = t("Audio playback failed"); } });
    if (review.audio) ui.player.src = review.audio;
    ui.reviewView.replaceChildren(h("h1", null, t("Review")), summary,
      h("div", { class: "row card" }, search, h("label", { for: "only" }, only, " ", t("Needs listening")), ui.saveButton, format, mode, exportButton),
      h("div", { class: "review" }, ui.regionList, h("div", { class: "card" }, ui.editor)),
      h("div", { class: "card" }, h("div", { class: "row" },
        h("button", { class: "btn", type: "button", onClick: () => playRegion() }, t("Play region")),
        h("button", { class: "btn", type: "button", onClick: () => ui.player.pause() }, t("Pause"))), ui.player, ui.audioStatus));
    renderRegionList();
    renderEditor();
  }

  function renderRegionList() {
    const review = state.review;
    const shown = visibleRegions();
    ui.regionList.replaceChildren(...shown.map((entry) => h("li", {
      class: "region" + (needsListening(entry.region) ? " uncertain" : "") + (entry.text !== entry.original ? " edited" : ""), role: "option", "aria-selected": String(entry.index === review.selected),
      onClick: () => select(entry.index)
    }, h("div", null, h("div", { class: "time" }, clock(entry.region.startMs) + " – " + clock(entry.region.endMs)), h("div", { class: "source" }, sourceLabel(entry.region.source))),
      h("div", { class: "text" }, entry.text))));
  }

  function select(index) {
    state.review.selected = index;
    renderRegionList();
    renderEditor();
  }

  function current() { return state.review && state.review.regions[state.review.selected]; }

  function renderEditor() {
    const review = state.review;
    const entry = current();
    if (!entry) { ui.editor.replaceChildren(h("p", { class: "muted" }, t("Open a finished recording from Projects to review its transcript."))); return; }
    const region = entry.region;
    const views = [["final", t("Final")], ["comparison", t("Comparison")], ["raw", t("Raw")]];
    const evidence = h("p", { class: "evidence" }, t("{0} · {1} · confidence {2}", sourceLabel(region.source), region.llmChoice || "—", region.confidence == null ? "—" : Number(region.confidence).toFixed(2))
      + (region.warnings && region.warnings.length ? " · " + region.warnings.map((warning) => t(warning)).join(" · ") : ""));
    const subtabs = h("div", { class: "subtabs", role: "tablist" }, views.map(([id, label]) =>
      h("button", { class: "subtab", type: "button", role: "tab", "aria-selected": String(review.view === id), onClick: () => { review.view = id; renderEditor(); } }, label)));
    let body;
    if (review.view === "comparison") {
      body = h("div", null,
        h("div", { class: "eyebrow" }, t("WHISPER")), h("div", null, region.whisperText),
        h("div", { class: "eyebrow" }, "CANARY"), h("div", null, region.canaryText),
        h("div", { class: "eyebrow" }, t("FINAL")), h("div", null, entry.text));
    } else if (review.view === "raw") {
      const note = review.data.rawCanaryNote === "outside-coverage" ? t("This language is outside Canary's coverage. Whisper timestamps and text are preserved; all regions require listening.")
        : review.data.rawCanaryNote === "incomplete" ? t("Canary did not complete. All regions require listening.") : "";
      body = h("div", { class: "stack" }, h("p", { class: "muted" }, t("Original engine output (read-only).")),
        h("pre", { class: "raw" }, review.data.rawWhisper), h("pre", { class: "raw" }, review.data.rawCanary), note && h("p", { class: "muted" }, note));
    } else {
      const area = h("textarea", { id: "editor", "aria-label": t("Edit selected transcript region"), value: entry.text, onInput: (event) => { entry.text = event.target.value; markDirty(); renderRegionListKeepingFocus(); } });
      ui.textarea = area;
      body = h("div", { class: "stack" },
        h("div", { class: "row" },
          h("button", { class: "btn", type: "button", onClick: () => useText(region.whisperText) }, t("Use Whisper · 1")),
          h("button", { class: "btn", type: "button", onClick: () => useText(region.canaryText) }, t("Use Canary · 2")),
          h("button", { class: "btn", type: "button", onClick: () => useText(entry.automatic) }, t("Restore automatic result · 3"))),
        area);
    }
    ui.editor.replaceChildren(evidence, subtabs, body);
  }

  /** While typing: only the selected row follows the text, so that the text box keeps its place. */
  function renderRegionListKeepingFocus() {
    const selected = ui.regionList.querySelector("[aria-selected='true']");
    const entry = current();
    if (selected && entry) {
      selected.classList.toggle("edited", entry.text !== entry.original);
      const box = selected.querySelector(".text"); if (box) box.textContent = entry.text;
    }
  }

  function markDirty() {
    state.dirty = state.review.regions.some((entry) => entry.text !== entry.original);
    if (ui.saveButton) ui.saveButton.disabled = !state.dirty;
  }

  function useText(value) {
    const entry = current();
    if (!entry || value == null) return;
    entry.text = value;
    markDirty();
    renderRegionList();
    renderEditor();
  }

  function playRegion() {
    const entry = current();
    if (!entry || !ui.player || !ui.player.src) return;
    ui.player.currentTime = entry.region.startMs / 1000;
    ui.player.play().catch(() => { ui.audioStatus.textContent = t("Audio playback failed"); });
  }

  async function saveEdits() {
    const review = state.review;
    const edits = review.regions.filter((entry) => entry.text !== entry.original).map((entry) => ({ index: entry.index, text: entry.text }));
    if (!edits.length) return;
    await guard(async () => {
      await api("/v1/transcriptions/" + review.id + "/review", { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ edits }) });
      await openReview(review.id, true);
      notice("info", t("Edits saved with revision history"));
    }, (message) => t("Save failed") + ": " + message);
  }

  async function exportTranscript(format, mode) {
    const review = state.review;
    await guard(async () => {
      const blob = await (await api("/v1/transcriptions/" + review.id + "/transcript?format=" + format + "&mode=" + mode)).blob();
      const extension = format === "full-json" ? "json" : format;
      const link = h("a", { href: URL.createObjectURL(blob), download: (review.name.replace(/\.[^.]*$/, "") || "transcript") + "." + extension });
      document.body.append(link); link.click(); link.remove();
      window.setTimeout(() => URL.revokeObjectURL(link.href), 10000);
    }, (message) => t("Export failed") + ": " + message);
  }

  window.addEventListener("beforeunload", (event) => { if (state.dirty) { event.preventDefault(); event.returnValue = ""; } });

  document.addEventListener("keydown", (event) => {
    if (state.tab !== "review" || !state.review || event.ctrlKey || event.metaKey || event.altKey) return;
    if (event.target instanceof Element && event.target.closest("textarea, input, select, button, audio, a")) return;
    const entry = current();
    const shown = visibleRegions();
    const position = shown.findIndex((item) => item === entry);
    switch (event.key) {
      case " ": if (ui.player && ui.player.src) { if (ui.player.paused) ui.player.play().catch(() => { }); else ui.player.pause(); } break;
      case "j": if (position >= 0 && position + 1 < shown.length) select(shown[position + 1].index); break;
      case "k": if (position > 0) select(shown[position - 1].index); break;
      case "1": if (entry) useText(entry.region.whisperText); break;
      case "2": if (entry) useText(entry.region.canaryText); break;
      case "3": if (entry) useText(entry.automatic); break;
      case "e": if (ui.textarea) ui.textarea.focus(); break;
      default: return;
    }
    event.preventDefault();
  });

  // ---- start ---------------------------------------------------------------------------------------------------------------------------

  async function boot() {
    ui.frame = h("div", null, h("div", { id: "header" }), h("div", { "data-tabs": "" }), h("main", { id: "main" }));
    app.replaceChildren(ui.frame);
    ui.header = ui.frame.querySelector("#header"); ui.main = ui.frame.querySelector("#main");
    await chooseLanguage(initialLanguage());
    try { state.health = await (await fetch("/v1/health", { cache: "no-store" })).json(); }
    catch { renderHeader(); ui.main.replaceChildren(h("div", { class: "note error", role: "alert" }, t("Could not reach the server."))); return; }
    renderHeader();
    if (state.health.passwordRequired && state.password) {
      try { state.info = await getJson("/v1/server"); }
      catch { state.password = ""; keep.set("sessionStorage", "mb-password", null); }
    }
    if (state.health.passwordRequired && !state.password) { showLogin(""); return; }
    await showWorkspace();
  }

  boot();
})();
