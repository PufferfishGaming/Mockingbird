"use strict";
// Mockingbird Client Webview, the web page of a Mockingbird server: send a recording, follow the recordings on the server, review and edit a transcript, export it.
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
    tab: "new", jobs: [], languages: [], file: null, review: null, timer: 0, sending: false, sendingLink: false, dirty: false
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
    abandonNotes();
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
    if (recorder.media) stopRecording();
    if (notes.rec) await stopNoteRecording();
    await flushNote();
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
    ui.notesView = h("section", { id: "view-notes" });
    ui.tabs = h("nav", { class: "tabs", role: "tablist" });
    ui.main.replaceChildren(ui.notice, ui.newView, ui.projectsView, ui.reviewView, ui.notesView);
    ui.frame.querySelector("[data-tabs]").replaceChildren(ui.tabs);
    await guard(async () => {
      state.info = await getJson("/v1/server");
      const catalog = await getJson("/v1/languages");
      state.languages = catalog.data;
    });
    buildNew();
    buildNotes();
    renderTabs();
    selectTab(state.tab);
    renderInfoNotice();
    await refreshJobs();
    if (state.review) await openReview(state.review.id, true);
    startPolling();
  }

  function renderTabs() {
    const items = [["new", t("New transcription")], ["projects", t("Projects")], ["review", t("Review")], ["notes", t("Notes")]];
    ui.tabs.replaceChildren(...items.map(([id, label]) => h("button", { class: "tab", type: "button", role: "tab", id: "tab-" + id, "aria-selected": String(state.tab === id), onClick: () => selectTab(id) }, label)));
  }

  function selectTab(id) {
    state.tab = id;
    for (const [name, view] of [["new", ui.newView], ["projects", ui.projectsView], ["review", ui.reviewView], ["notes", ui.notesView]]) view.hidden = name !== id;
    for (const tab of ui.tabs.children) tab.setAttribute("aria-selected", String(tab.id === "tab-" + id));
    if (id === "projects") renderProjects();
    if (id === "review") renderReview();
    if (id === "new") refreshInfo();
    if (id === "notes") { renderNotesList(); refreshNotes(true); }
  }

  /** Looks at what the server says about itself again (its link helper may have been installed, its models downloaded). */
  async function refreshInfo() {
    try { state.info = await getJson("/v1/server"); } catch { return; }
    updateLink();
    if (state.tab === "new") renderInfoNotice();
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
      if (state.tab === "notes" && !notes.rec && !(notes.open && notes.open.dirty)) await refreshNotes(true);        // the notes change when others write
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
        progress, status),
      buildRecorder(),
      buildLink());
    if (state.file) choose(state.file);
    updateLink();
  }

  // ---- fetch a link ------------------------------------------------------------------------------------------------------------------------
  // The server downloads the sound of the address (a video site, a podcast episode, a link to an audio file) and transcribes it; nothing is downloaded here.

  function buildLink() {
    const address = h("input", { type: "text", id: "link", inputmode: "url", autocomplete: "off", spellcheck: "false", maxlength: "2000", placeholder: "https://", "aria-label": t("Web address"),
      onInput: () => updateLink(), onKeydown: (event) => { if (event.key === "Enter") sendLink(); } });
    const send = h("button", { class: "btn primary", type: "button", disabled: true, onClick: () => sendLink() }, t("Send link to server"));
    const note = h("p", { class: "note", hidden: true });
    const status = h("p", { "aria-live": "polite" }, "");
    Object.assign(ui, { linkInput: address, linkSend: send, linkNote: note, linkStatus: status });
    return h("div", { class: "card stack" },
      h("h2", null, t("Link")),
      h("p", { class: "muted" }, t("Paste the address of a video or audio on the web: a video site, a podcast episode or a link to an audio file. The server downloads its sound and transcribes it.")),
      h("div", null, h("label", { for: "link" }, t("Web address")), address),
      h("div", { class: "row" }, send),
      note, status);
  }

  /** The button and the note follow what the server said about itself: links need a password on the server, and pages need its link helper. */
  function updateLink() {
    if (!ui.linkSend) return;
    const info = state.info;
    const ready = !!info && info.linksEnabled === true && info.modelsReady !== false;
    ui.linkSend.disabled = !ready || state.sendingLink || !ui.linkInput.value.trim();
    let note = "";
    if (info && info.linksEnabled !== true) note = info.passwordRequired === false ? t("This server has no password, so it does not fetch links for other computers. Set a password on the server to allow it.") : t("This server does not fetch links.");
    else if (info && info.linkPages !== true) note = t("This server fetches links to audio and video files. To fetch the sound of web pages as well, install the link helper in Mockingbird on the server.");
    ui.linkNote.textContent = note; ui.linkNote.hidden = !note;
  }

  async function sendLink() {
    const url = ui.linkInput.value.trim();
    if (!url || state.sendingLink || ui.linkSend.disabled) return;
    state.sendingLink = true; updateLink();
    ui.linkStatus.textContent = t("Sending to {0}…", state.health.name);
    try {
      const response = await api("/v1/links", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ url, language: ui.language.value }) });
      await response.json();
      ui.linkInput.value = ""; ui.linkStatus.textContent = t("Sent. The server is working on it.");
      selectTab("projects"); refreshJobs();
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) { signOut(t("The password is wrong.")); return; }
      ui.linkStatus.textContent = t("The link could not be sent: {0}",
        error instanceof ApiError && error.code === "models_missing" ? t("The server cannot transcribe this language yet: its speech models are not downloaded.") : t(error.message));
    } finally { state.sendingLink = false; updateLink(); }
  }

  // ---- record with the microphone ---------------------------------------------------------------------------------------------------------
  // The browser records (MediaRecorder) and asks the user's permission itself; the recording stays in the page until it is sent like any chosen file.

  const recorder = { media: null, stream: null, context: null, chunks: [], started: 0, timer: 0, quiet: 0, type: "" };

  function buildRecorder() {
    const start = h("button", { class: "btn primary", type: "button", onClick: () => startRecording() }, t("Start recording"));
    const stop = h("button", { class: "btn primary", type: "button", hidden: true, onClick: () => stopRecording() }, t("Stop recording"));
    const timeLabel = h("span", { class: "clock", hidden: true, "aria-label": t("Recording time") }, "0:00");
    const level = h("progress", { max: "100", value: "0", hidden: true, "aria-label": t("Microphone level") });
    const message = h("p", { class: "muted", "aria-live": "polite" }, "");
    const devices = h("select", { "aria-label": t("Microphone"), id: "microphone" }, h("option", { value: "" }, t("Default microphone")));
    Object.assign(ui, { recStart: start, recStop: stop, recClock: timeLabel, recLevel: level, recMessage: message, recDevices: devices });
    return h("div", { class: "card stack" },
      h("h2", null, t("Record")),
      h("p", { class: "muted" }, t("Record with the microphone. The recording is chosen as the recording above and is only sent when you press the button.")),
      h("div", null, h("label", { for: "microphone" }, t("Microphone")), devices),
      h("div", { class: "row" }, start, stop, timeLabel),
      level, message);
  }

  function recordingType() {
    if (typeof MediaRecorder === "undefined") return null;
    return ["audio/webm;codecs=opus", "audio/ogg;codecs=opus", "audio/mp4", "audio/webm"].find((type) => MediaRecorder.isTypeSupported(type)) || "";
  }

  async function startRecording() {
    if (recorder.media) return;
    if (!window.isSecureContext || !navigator.mediaDevices) { ui.recMessage.textContent = t("The browser only allows recording on a secure page (https, or localhost)."); return; }
    const type = recordingType();
    if (type === null) { ui.recMessage.textContent = t("Recording is not available in this browser."); return; }
    try {
      const wanted = ui.recDevices.value;
      recorder.stream = await navigator.mediaDevices.getUserMedia({ audio: wanted ? { deviceId: { exact: wanted } } : true });
    } catch (error) {
      ui.recMessage.textContent = error && error.name === "NotFoundError" ? t("No microphone was found. Connect one and allow the browser to use it.")
        : t("The browser did not allow this page to use the microphone. Allow it in the address bar, then try again.");
      return;
    }
    await listMicrophones();
    recorder.chunks = []; recorder.type = type; recorder.quiet = 0;
    recorder.media = type ? new MediaRecorder(recorder.stream, { mimeType: type }) : new MediaRecorder(recorder.stream);
    recorder.media.addEventListener("dataavailable", (event) => { if (event.data && event.data.size) recorder.chunks.push(event.data); });
    recorder.media.addEventListener("stop", finishRecording);
    recorder.media.start(1000);
    recorder.started = Date.now();
    const analyser = meter(recorder.stream);
    ui.recStart.hidden = true; ui.recStop.hidden = false; ui.recClock.hidden = false; ui.recLevel.hidden = false; ui.recDevices.disabled = true;
    ui.recMessage.textContent = t("Recording…");
    recorder.timer = window.setInterval(() => {
      ui.recClock.textContent = clock(Date.now() - recorder.started);
      const peak = analyser();
      ui.recLevel.value = peak * 100;
      recorder.quiet = peak > 0.002 ? 0 : recorder.quiet + 1;
      if (recorder.quiet === 20) ui.recMessage.textContent = t("No sound is coming from the microphone. Check that it is not muted and that the browser is using the right microphone.");
      else if (recorder.quiet === 0 && ui.recMessage.textContent !== t("Recording…")) ui.recMessage.textContent = t("Recording…");
    }, 100);
  }

  /** A function that answers how loud the microphone is now, 0 to 1. */
  function meter(stream) {
    try {
      recorder.context = new (window.AudioContext || window.webkitAudioContext)();
      const analyser = recorder.context.createAnalyser();
      analyser.fftSize = 1024;
      recorder.context.createMediaStreamSource(stream).connect(analyser);
      const samples = new Uint8Array(analyser.fftSize);
      return () => { analyser.getByteTimeDomainData(samples); let peak = 0; for (const sample of samples) peak = Math.max(peak, Math.abs(sample - 128) / 128); return peak; };
    } catch { return () => 0.5; }
  }

  async function listMicrophones() {
    try {
      const chosen = ui.recDevices.value;
      const found = (await navigator.mediaDevices.enumerateDevices()).filter((device) => device.kind === "audioinput" && device.deviceId && device.deviceId !== "default");
      ui.recDevices.replaceChildren(h("option", { value: "" }, t("Default microphone")), ...found.map((device) => h("option", { value: device.deviceId }, device.label || t("Microphone"))));
      ui.recDevices.value = found.some((device) => device.deviceId === chosen) ? chosen : "";
    } catch { /* the list stays as it was */ }
  }

  function stopRecording() {
    if (recorder.media && recorder.media.state !== "inactive") recorder.media.stop(); // finishRecording runs when the last piece has arrived
  }

  function finishRecording() {
    window.clearInterval(recorder.timer);
    const duration = Date.now() - recorder.started;
    for (const track of recorder.stream ? recorder.stream.getTracks() : []) track.stop();
    if (recorder.context) { recorder.context.close().catch(() => { }); recorder.context = null; }
    const quiet = recorder.quiet >= 20 && recorder.chunks.length === 0;
    const type = (recorder.media && recorder.media.mimeType) || recorder.type || "audio/webm";
    recorder.media = null; recorder.stream = null;
    ui.recStart.hidden = false; ui.recStop.hidden = true; ui.recClock.hidden = true; ui.recLevel.hidden = true; ui.recDevices.disabled = false;
    if (quiet || !recorder.chunks.length || duration < 500) { ui.recMessage.textContent = t("No sound was recorded. Check that the microphone is not muted and that the browser may use it."); return; }
    const extension = type.includes("ogg") ? "ogg" : type.includes("mp4") ? "m4a" : "webm";
    const now = new Date();
    const stamp = now.getFullYear() + "-" + pad(now.getMonth() + 1) + "-" + pad(now.getDate()) + " " + pad(now.getHours()) + "-" + pad(now.getMinutes()) + "-" + pad(now.getSeconds());
    const file = new File(recorder.chunks, "Recording " + stamp + "." + extension, { type: type.split(";")[0] });
    recorder.chunks = [];
    choose(file);
    ui.recMessage.textContent = t("Recording saved: {0} ({1})", file.name, clock(duration));
  }


  function languageOptions() {
    return [{ code: "auto", label: t("Auto-detect language") }, ...sortedLanguages()].map((entry) => h("option", { value: entry.code }, entry.label));
  }

  /** The choices of a second language: none, then every language but the first. */
  function secondLanguageOptions(first) {
    return [{ code: "", label: t("No second language") }, ...sortedLanguages().filter((entry) => entry.code !== first)].map((entry) => h("option", { value: entry.code }, entry.label));
  }

  function sortedLanguages() {
    const names = new Intl.Collator(text.lang);
    return state.languages.filter((language) => language.code !== "auto")
      .map((language) => ({ code: language.code, label: t(language.name) + " (" + language.code + ")" }))
      .sort((first, second) => names.compare(first.label, second.label));
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
        // A finished recording opens in the review when its name is clicked (or Enter is pressed on it).
        h("div", job.state === "complete"
          ? { class: "info open", tabindex: "0", role: "link", "aria-label": t("Open transcript") + ": " + job.name, title: t("Open transcript"), onClick: () => openReview(job.id), onKeydown: (event) => { if (event.key === "Enter") openReview(job.id); } }
          : { class: "info" },
          h("div", { class: "name" }, job.name),
          h("div", { class: "state" + (job.state === "failed" ? " failed" : "") }, stateLabel(job)),
          job.state === "running" && h("progress", { max: "100", value: String(job.percent), "aria-label": stateLabel(job) }),
          h("div", { class: "when" }, new Date(job.createdUtc).toLocaleString(text.lang)),
          job.error && h("div", { class: "state failed" }, t(job.error))),
        h("div", { class: "row" },
          job.state === "complete" && h("button", { class: "btn", type: "button", onClick: () => openReview(job.id) }, t("Open transcript")),
          (job.state === "queued" || job.state === "running") && h("button", { class: "btn", type: "button", onClick: () => cancelJob(job.id) }, t("Cancel")),
          (job.state === "complete" || job.state === "failed" || job.state === "cancelled") && h("button", { class: "btn", type: "button", "aria-label": t("Delete project") + ": " + job.name, title: t("Delete this project and its transcript"), onClick: () => deleteJob(job) }, t("Delete"))))));
    ui.projectsView.replaceChildren(h("h1", null, t("Projects")),
      h("div", { class: "card" }, state.jobs.length ? [h("p", { class: "muted" }, t("Click a finished project to open its transcript.")), list] : h("p", { class: "muted" }, t("Nothing here yet. Finished transcriptions are listed here."))));
  }

  /** Deletes a finished recording on the server after asking: its transcript, edits and the copy of the recording the server holds. */
  async function deleteJob(job) {
    if (!window.confirm(t("Delete \"{0}\" from the server? Its transcript, the edits and the recording the server holds are removed. This cannot be undone.", job.name))) return;
    await guard(async () => {
      try { await api("/v1/transcriptions/" + job.id, { method: "DELETE" }); }
      catch (error) {
        if (error instanceof ApiError && error.code === "still_running") { notice("error", t("Could not delete the project") + ": " + t("A project that is still being worked on cannot be deleted. Cancel it first.")); return; }
        throw error;
      }
      if (state.review && state.review.id === job.id) closeReview();
      await refreshJobs();
    }, (message) => t("Could not delete the project") + ": " + message);
  }

  /** The review that was open belongs to a recording that has been deleted: the audio is let go of and the page goes back to the list. */
  function closeReview() {
    if (state.review && state.review.audio) URL.revokeObjectURL(state.review.audio);
    state.review = null; state.dirty = false;
    if (ui.player) { ui.player.pause(); ui.player.removeAttribute("src"); ui.player.load(); }
    if (state.tab === "review") selectTab("projects");
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
        search: previous ? previous.search : "", only: previous ? previous.only : false, audio: previous ? previous.audio : null, follow: previous ? previous.follow : true,
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

  // ---- karaoke: the words as they are said -----------------------------------------------------------------------------------------------
  // A region has a start and an end, not a time for each word, so its time is shared out over the words by their length, with a longer wait after a
  // sentence than after a comma. These numbers are the same as in KaraokePlan.cs (a test compares them).

  const SENTENCE_PAUSE = 4, CLAUSE_PAUSE = 2;

  function karaokeWords(text) {
    const found = [];
    for (const match of text.matchAll(/\S+/g)) {
      const word = match[0];
      const letters = (word.match(/[\p{L}\p{N}]/gu) || []).length;
      const last = word[word.length - 1];
      const pause = ".!?…".includes(last) ? SENTENCE_PAUSE : ",;:–—".includes(last) ? CLAUSE_PAUSE : 0;
      found.push({ start: match.index, length: word.length, weight: Math.max(1, letters) + pause });
    }
    const total = found.reduce((sum, item) => sum + item.weight, 0);
    let at = 0;
    for (const item of found) { item.from = at; at += item.weight / total; item.to = at; }
    if (found.length) found[found.length - 1].to = 1;
    return found;
  }

  function wordAt(words, progress) {
    if (!words.length) return -1;
    const index = words.findIndex((word) => progress < word.to);
    return index < 0 ? words.length - 1 : index;
  }

  /** Puts the text of a row on the page: plain, or with the words said so far coloured and the word being said in bold. */
  function paintText(entry, word) {
    const row = ui.regionList && ui.regionList.querySelector('[data-index="' + entry.index + '"] .text');
    if (!row) return;
    if (word < 0) { row.textContent = entry.text; return; }
    const words = karaokeWords(entry.text);
    const parts = [];
    let at = 0;
    words.forEach((item, index) => {
      if (item.start > at) parts.push(entry.text.slice(at, item.start));
      parts.push(h("span", { class: index < word ? "said" : index === word ? "now" : null }, entry.text.slice(item.start, item.start + item.length)));
      at = item.start + item.length;
    });
    if (at < entry.text.length) parts.push(entry.text.slice(at));
    row.replaceChildren(...parts);
  }

  /** Marks the region being played and its word; with "Follow the playback" the selection moves on when the next region begins. */
  function updatePlayback() {
    const review = state.review;
    if (!review || !ui.player || !ui.player.src) return;
    const ms = ui.player.currentTime * 1000;
    const active = review.regions.find((entry) => entry.region.nativeTimestamps !== false && ms >= entry.region.startMs && ms < entry.region.endMs);
    const index = active ? active.index : -1;
    const word = active ? wordAt(karaokeWords(active.text), (ms - active.region.startMs) / Math.max(1, active.region.endMs - active.region.startMs)) : -1;
    if (index === review.spoken && word === review.spokenWord) return;
    const previous = review.spoken !== undefined && review.spoken !== index ? review.regions[review.spoken] : null;
    if (previous) paintText(previous, -1);
    if (active) paintText(active, word);
    const entered = index !== review.spoken;
    review.spoken = index; review.spokenWord = word;
    const editing = document.activeElement instanceof HTMLTextAreaElement || document.activeElement instanceof HTMLInputElement;
    if (active && entered && !ui.player.paused && !editing && ui.follow && ui.follow.checked && visibleRegions().includes(active)) {
      select(index);
      const row = ui.regionList.querySelector('[data-index="' + index + '"]');
      if (row) row.scrollIntoView({ block: "nearest" });
    }
  }

  function tickPlayback() {
    updatePlayback();
    if (ui.player && !ui.player.paused) window.requestAnimationFrame(tickPlayback);
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
    ui.player = h("audio", { controls: true, preload: "auto", "aria-label": t("Audio position"), onError: () => { if (ui.player.src) ui.audioStatus.textContent = t("Audio playback failed"); },
      onPlay: () => tickPlayback(), onSeeked: () => updatePlayback(), onPause: () => updatePlayback(), onTimeupdate: () => updatePlayback() });
    ui.follow = h("input", { type: "checkbox", id: "follow", checked: review.follow !== false, onChange: (event) => { review.follow = event.target.checked; } });
    if (review.audio) ui.player.src = review.audio;
    ui.reviewView.replaceChildren(h("h1", null, t("Review")), summary,
      h("div", { class: "row card" }, search, h("label", { for: "only" }, only, " ", t("Needs listening")), ui.saveButton, format, mode, exportButton),
      h("div", { class: "review" }, ui.regionList, h("div", { class: "card" }, ui.editor)),
      h("div", { class: "card" }, h("div", { class: "row" },
        h("button", { class: "btn", type: "button", onClick: () => playRegion() }, t("Play region")),
        h("button", { class: "btn", type: "button", onClick: () => ui.player.pause() }, t("Pause")),
        h("label", { for: "follow", title: t("While the recording plays, the transcript moves with it: the words already said are coloured and the region being said is selected.") }, ui.follow, " ", t("Follow the playback"))), ui.player, ui.audioStatus));
    renderRegionList();
    renderEditor();
  }

  function renderRegionList() {
    const review = state.review;
    const shown = visibleRegions();
    ui.regionList.replaceChildren(...shown.map((entry) => h("li", {
      class: "region" + (needsListening(entry.region) ? " uncertain" : "") + (entry.text !== entry.original ? " edited" : ""), role: "option", "aria-selected": String(entry.index === review.selected), "data-index": String(entry.index),
      onClick: () => select(entry.index)
    }, h("div", null, h("div", { class: "time" }, clock(entry.region.startMs) + " – " + clock(entry.region.endMs)), h("div", { class: "source" }, sourceLabel(entry.region.source))),
      h("div", { class: "text" }, entry.text))));
    if (review.spoken !== undefined && review.spoken >= 0) paintText(review.regions[review.spoken], review.spokenWord);
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

  // ---- notes ---------------------------------------------------------------------------------------------------------------------------
  // Notes are kept on the server, a file each. The open note saves itself a moment after the last change and names the revision it opened, so that another window's changes are never
  // overwritten. A note can be recorded: the sound is cut into phrases in this page (live.js), the server reads each phrase (POST /v1/live), and the words are added to the end of the note.

  const notes = {
    list: [], open: null, timer: 0, failed: "", notice: "", status: "", rec: null,
    language: keep.get("localStorage", "mb-notes-language") || "auto",
    keys: MbLive.parseCombo(keep.get("localStorage", "mb-notes-keys")), capture: null
  };
  let noteChain = Promise.resolve();

  const shorten = (value, length) => value.length <= length ? value : value.slice(0, length).trimEnd() + "…";
  const noteHeading = (note) => note.title || (note.preview ? shorten(note.preview, 48) : t("Untitled note"));

  function buildNotes() {
    ui.nList = h("ul", { class: "notelist", role: "listbox", "aria-label": t("Notes") });
    ui.nNew = h("button", { class: "btn primary", type: "button", onClick: () => newNote() }, t("New note"));
    ui.nEmpty = h("p", { class: "muted" }, t("No notes yet. Make one, then type or record into it."));
    ui.nEditor = h("div");
    ui.notesView.replaceChildren(h("h1", null, t("Notes")), h("div", { class: "notes" }, h("div", null, ui.nNew, ui.nEmpty, ui.nList), ui.nEditor));
    renderNotesList();
    renderNoteEditor();
  }

  function renderNotesList() {
    if (!ui.nList) return;
    const unavailable = !!state.info && state.info.notesEnabled === false;
    ui.nNew.disabled = !!notes.rec || unavailable;
    ui.nEmpty.hidden = notes.list.length > 0 || unavailable;
    ui.nList.replaceChildren(...notes.list.map((note) => h("li", { role: "option", "aria-selected": String(!!notes.open && notes.open.id === note.id), onClick: () => { if (!notes.rec) openNote(note.id); } },
      h("div", { class: "title" }, noteHeading(note)),
      note.title && note.preview && h("div", { class: "preview" }, note.preview),
      h("div", { class: "when" }, new Date(note.updatedUtc).toLocaleString(text.lang)))));
  }

  const defaultNoteStatus = () => notes.rec ? t("Listening…") : notes.failed ? notes.failed : notes.open && !notes.open.dirty ? t("All changes are saved") : "";
  function setNoteStatus(value) {
    notes.status = value;
    if (ui.nStatus) ui.nStatus.textContent = value;
  }

  function renderNoteEditor() {
    if (!ui.nEditor) return;
    const info = state.info;
    if (info && info.notesEnabled === false) { ui.nEditor.replaceChildren(h("p", { class: "muted" }, t("This server does not keep notes. It may be an older version."))); return; }
    const open = notes.open;
    if (!open) { ui.nEditor.replaceChildren(h("p", { class: "muted" }, t("Open a note from the list, or make a new one."))); return; }
    const recording = !!notes.rec;
    ui.nTitle = h("input", { type: "text", class: "notetitle", value: open.title, maxlength: "200", "aria-label": t("Title of the note"), title: t("Title of the note"),
      onInput: (event) => { open.title = event.target.value; noteEdited(); } });
    ui.nText = h("textarea", { class: "notetext", value: open.text, "aria-label": t("Text of the note"), onInput: (event) => { open.text = event.target.value; noteEdited(); } });
    ui.nNotice = h("div", { class: "note", hidden: !notes.notice, style: "margin-top:12px" }, notes.notice);
    ui.nStatus = h("span", { class: "muted", "aria-live": "polite" }, notes.status || defaultNoteStatus());
    ui.nRecord = h("button", { class: "btn primary", type: "button", onClick: () => toggleNoteRecording() }, recording ? t("Stop recording") : t("Record"));
    ui.nLevel = h("progress", { max: "100", value: "0", hidden: !recording, "aria-label": t("Microphone level") });
    const chosen = MbLive.splitLanguages(notes.language, state.languages.map((language) => language.code));
    ui.nLanguage = h("select", { "aria-label": t("Language of the recording"), disabled: recording,
      title: t("Choosing the language is more reliable than detecting it, because a phrase is short. Whatever is said in another language is written translated into this one, unless that language is chosen as the second language."),
      onChange: (event) => chooseNoteLanguages(event.target.value, ui.nSecond.value) }, languageOptions());
    ui.nLanguage.value = chosen.first;
    ui.nSecond = h("select", { "aria-label": t("Second language of the recording"), disabled: recording || chosen.first === "auto",
      title: t("If you switch between two languages, choose both: each phrase is written in the language it was spoken in, not translated. When the two are hard to tell apart, a phrase takes a moment longer."),
      onChange: (event) => chooseNoteLanguages(ui.nLanguage.value, event.target.value) }, secondLanguageOptions(chosen.first));
    ui.nSecond.value = chosen.second;
    ui.nMic = h("select", { "aria-label": t("Microphone"), disabled: recording }, h("option", { value: "" }, t("Default microphone")));
    ui.nHint = h("p", { class: "note", hidden: !(info && info.liveEnabled === false), style: "margin-top:12px" }, t("This server cannot read dictation. It may be an older version, or have no speech model downloaded yet."));
    ui.nKeys = h("div", { style: "margin-top:14px" });
    ui.nEditor.replaceChildren(ui.nTitle, ui.nNotice,
      h("div", { class: "card", style: "margin-top:14px" },
        h("div", { class: "row" }, ui.nRecord,
          h("div", null, h("label", null, t("Language")), ui.nLanguage),
          h("div", null, h("label", null, t("Second language")), ui.nSecond),
          h("div", null, h("label", null, t("Microphone")), ui.nMic)),
        ui.nLevel, ui.nHint, ui.nKeys),
      ui.nText,
      h("div", { class: "row", style: "margin-top:10px" }, ui.nStatus, h("span", { class: "spacer" }),
        h("button", { class: "btn", type: "button", onClick: () => copyNote() }, t("Copy text")),
        h("button", { class: "btn", type: "button", disabled: recording, title: t("Delete this note"), "aria-label": t("Delete note"), onClick: () => deleteNote() }, t("Delete"))));
    renderNoteKeys();
    listNoteMicrophones();
  }

  /** The languages of the recording were changed: they are kept for next time, and the second list loses the first language. */
  function chooseNoteLanguages(first, second) {
    notes.language = MbLive.joinLanguages(first, second);
    keep.set("localStorage", "mb-notes-language", notes.language);
    const chosen = MbLive.splitLanguages(notes.language);
    ui.nSecond.replaceChildren(...secondLanguageOptions(chosen.first));
    ui.nSecond.value = chosen.second;
    ui.nSecond.disabled = !!notes.rec || chosen.first === "auto";
  }

  async function copyNote() {
    if (!notes.open || !notes.open.text) return;
    try { await navigator.clipboard.writeText(notes.open.text); }
    catch { ui.nText.select(); try { document.execCommand("copy"); } catch { /* the person copies by hand */ } }
  }

  // ---- the list ------------------------------------------------------------------------------------------------------------------------

  async function refreshNotes(quiet) {
    if (state.info && state.info.notesEnabled === false) { notes.list = []; renderNotesList(); renderNoteEditor(); return; }
    let list;
    try { list = (await (await api("/v1/notes")).json()).data || []; }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) { signOut(t("The password is wrong.")); return; }
      if (!quiet) notice("error", t("The notes could not be read: {0}", t(error.message)));
      return;
    }
    notes.list = list;
    const open = notes.open;
    const row = open && list.find((note) => note.id === open.id);
    if (open && !open.dirty && !notes.rec) {
      if (!row) { notes.open = null; renderNoteEditor(); }
      else if (row.revision > open.revision) await loadNote(open.id);
    }
    renderNotesList();
  }

  async function loadNote(id) {
    const data = await (await api("/v1/notes/" + id)).json();
    notes.open = { id: data.id, title: data.title, text: data.text, revision: data.revision, dirty: false };
    notes.failed = "";
    renderNoteEditor();
  }

  async function openNote(id) {
    if (notes.open && notes.open.id === id) return;
    await flushNote();
    notes.notice = ""; notes.status = "";
    await guard(async () => {
      try { await loadNote(id); }
      catch (error) {
        if (error instanceof ApiError && error.code === "not_found") { notes.open = null; notes.notice = t("That note was deleted on another computer."); await refreshNotes(); renderNoteEditor(); return; }
        throw error;
      }
      renderNotesList();
    }, (message) => t("The note could not be opened: {0}", t(message)));
  }

  async function newNote() {
    if (notes.rec) return;
    await flushNote();
    await guard(async () => {
      const data = await (await api("/v1/notes", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title: "", text: "" }) })).json();
      notes.list.unshift({ id: data.id, title: "", preview: "", updatedUtc: data.updatedUtc, createdUtc: data.createdUtc, revision: data.revision, length: 0 });
      notes.open = { id: data.id, title: "", text: "", revision: data.revision, dirty: false };
      notes.notice = ""; notes.failed = ""; notes.status = "";
      renderNotesList();
      renderNoteEditor();
      ui.nTitle.focus();
    }, (message) => t("The note could not be created: {0}", t(message)));
  }

  async function deleteNote() {
    const open = notes.open;
    if (!open || notes.rec) return;
    const row = notes.list.find((note) => note.id === open.id);
    if (!window.confirm(t("Delete \"{0}\"? The note and its text are removed. This cannot be undone.", noteHeading(row || { title: open.title, preview: shorten(open.text.replace(/\s+/g, " ").trim(), 60) })))) return;
    window.clearTimeout(notes.timer);
    await guard(async () => {
      try { await api("/v1/notes/" + open.id, { method: "DELETE" }); }
      catch (error) { if (!(error instanceof ApiError && error.code === "not_found")) throw error; }
      notes.open = null; notes.notice = ""; notes.failed = ""; notes.status = "";
      notes.list = notes.list.filter((note) => note.id !== open.id);
      renderNotesList();
      renderNoteEditor();
    }, (message) => t("The note could not be deleted: {0}", t(message)));
  }

  // ---- saving --------------------------------------------------------------------------------------------------------------------------

  function noteEdited() {
    const open = notes.open;
    if (!open) return;
    open.dirty = true; notes.failed = "";
    scheduleNoteSave(900);
    setNoteStatus("");
  }

  function scheduleNoteSave(delay) {
    window.clearTimeout(notes.timer);
    notes.timer = window.setTimeout(() => saveNote(), delay);
  }

  /** Saves the open note if it changed. Saves follow one another, so that each names the revision the one before made. */
  const saveNote = () => (noteChain = noteChain.then(saveNoteNow, saveNoteNow));

  async function flushNote() {
    window.clearTimeout(notes.timer);
    for (let attempt = 0; attempt < 3 && notes.open && notes.open.dirty && !notes.failed; attempt++) await saveNote();
  }

  async function saveNoteNow() {
    window.clearTimeout(notes.timer);
    const open = notes.open;
    if (!open || !open.dirty) return;
    const { id, title, text: body, revision } = open;
    setNoteStatus(t("Saving…"));
    try {
      const saved = await (await api("/v1/notes/" + id, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title, text: body, revision }) })).json();
      notes.failed = "";
      if (notes.open && notes.open.id === id) {
        open.revision = saved.revision;
        if (open.title === title && open.text === body) open.dirty = false; else scheduleNoteSave(900);   // changed again meanwhile: saved once more
      }
      const row = notes.list.find((note) => note.id === id);
      if (row) { Object.assign(row, { title: saved.title, preview: MbPreview(saved.text), updatedUtc: saved.updatedUtc, revision: saved.revision }); notes.list = [row, ...notes.list.filter((note) => note !== row)]; }
      renderNotesList();
    } catch (error) { await noteSaveFailed(error, id, title, body); }
    setNoteStatus(defaultNoteStatus());
  }

  /** The first words of a note for the list, as the server writes them. */
  function MbPreview(value) {
    const flat = value.replace(/\s+/g, " ").trim();
    return flat.length > 160 ? flat.slice(0, 160).trimEnd() + "…" : flat;
  }

  async function noteSaveFailed(error, id, title, body) {
    if (error instanceof ApiError && error.status === 401) { signOut(t("The password is wrong.")); return; }
    try {
      if (error instanceof ApiError && error.code === "note_changed") {
        // Someone saved the note meanwhile. Nothing is overwritten and nothing is lost: what was written here is kept as a note of its own, and the note shows what the other computer saved.
        const name = title || MbPreview(body).slice(0, 30);
        const copy = await (await api("/v1/notes", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title: name ? t("{0} (my version)", name) : t("Untitled note (my version)"), text: body }) })).json();
        notes.notice = t("This note was changed on another computer. What you wrote was kept as a new note called \"{0}\".", copy.title);
        await loadNote(id);
        await refreshNotes(true);
        return;
      }
      if (error instanceof ApiError && error.code === "not_found") {
        const again = await (await api("/v1/notes", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ title, text: body }) })).json();
        notes.notice = t("This note was deleted on another computer. It was saved again.");
        notes.open = { id: again.id, title, text: body, revision: again.revision, dirty: false };
        await refreshNotes(true);
        renderNoteEditor();
        return;
      }
    } catch (second) { error = second; }
    notes.failed = t("The note could not be saved: {0}", t(error.message));
    scheduleNoteSave(5000);                                   // and tried again in a moment
    renderNoteEditor();
  }

  // ---- recording ------------------------------------------------------------------------------------------------------------------------

  async function listNoteMicrophones() {
    try {
      const found = (await navigator.mediaDevices.enumerateDevices()).filter((device) => device.kind === "audioinput" && device.deviceId && device.deviceId !== "default");
      if (!ui.nMic || !found.length) return;
      const chosen = ui.nMic.value;
      ui.nMic.replaceChildren(h("option", { value: "" }, t("Default microphone")), ...found.map((device) => h("option", { value: device.deviceId }, device.label || t("Microphone"))));
      ui.nMic.value = found.some((device) => device.deviceId === chosen) ? chosen : "";
    } catch { /* the list stays as it was */ }
  }

  const toggleNoteRecording = () => notes.rec ? stopNoteRecording() : startNoteRecording();

  async function startNoteRecording() {
    if (notes.rec || notes.starting) return;
    if (state.info && state.info.notesEnabled === false) return;
    if (!state.info || state.info.liveEnabled !== true) { setNoteStatus(t("This server cannot read dictation. It may be an older version, or have no speech model downloaded yet.")); return; }
    if (!window.isSecureContext || !navigator.mediaDevices) { setNoteStatus(t("The browser only allows recording on a secure page (https, or localhost).")); return; }
    if (typeof AudioWorkletNode === "undefined") { setNoteStatus(t("Recording is not available in this browser.")); return; }
    notes.starting = true;
    try {
      if (!notes.open) await newNote();
      if (!notes.open) return;
      const wanted = ui.nMic ? ui.nMic.value : "";
      let stream;
      try { stream = await navigator.mediaDevices.getUserMedia({ audio: wanted ? { deviceId: { exact: wanted } } : true }); }
      catch (error) {
        setNoteStatus(error && error.name === "NotFoundError" ? t("No microphone was found. Connect one and allow the browser to use it.")
          : t("The browser did not allow this page to use the microphone. Allow it in the address bar, then try again."));
        return;
      }
      const context = new (window.AudioContext || window.webkitAudioContext)();
      try { await context.audioWorklet.addModule("/worklet.js"); }
      catch { for (const track of stream.getTracks()) track.stop(); context.close().catch(() => { }); setNoteStatus(t("Recording is not available in this browser.")); return; }
      const source = context.createMediaStreamSource(stream);
      const node = new AudioWorkletNode(context, "mb-tap", { numberOfInputs: 1, numberOfOutputs: 1, channelCount: 1 });
      const silent = context.createGain();
      silent.gain.value = 0;                                  // the sound goes through the page and out of no speaker
      source.connect(node); node.connect(silent); silent.connect(context.destination);
      const resampler = new MbLive.Resampler(context.sampleRate);
      const rec = { context, stream, node, source, language: notes.language, recent: "", queue: [], busy: null, problem: false, meter: 0 };
      rec.detector = new MbLive.UtteranceDetector((phrase) => { rec.queue.push(phrase); pumpPhrases(rec); });
      node.port.onmessage = (event) => { const pcm = resampler.push(event.data); if (pcm.length) rec.detector.feed(pcm); };
      rec.meter = window.setInterval(() => { if (ui.nLevel) ui.nLevel.value = Math.min(100, rec.detector.level * 600); }, 100);
      notes.rec = rec;
      setNoteStatus(t("Listening…"));
      renderNotesList();
      renderNoteEditor();
    } finally { notes.starting = false; }
  }

  /** Reads the phrases one at a time, in the order they were spoken, so that a slow phrase never overtakes a quick one. */
  function pumpPhrases(rec) {
    if (rec.busy) return;
    rec.busy = (async () => {
      while (rec.queue.length) {
        const phrase = rec.queue.shift();
        if (notes.rec === rec) setNoteStatus(t("Reading what you said…"));
        try {
          // With two languages, the language of the previous phrase decides a phrase that reads about as well in both.
          const response = await api("/v1/live?language=" + encodeURIComponent(rec.language) + "&speech=" + Math.round(phrase.speechMs) + (rec.recent ? "&recent=" + encodeURIComponent(rec.recent) : ""),
            { method: "POST", headers: { "Content-Type": "audio/wav" }, body: MbLive.encodeWav(phrase.pcm) });
          const answer = await response.json();
          const words = (answer.text || "").trim();
          if (words && answer.language) rec.recent = answer.language;
          rec.problem = false;
          if (words && notes.open) addWords(words);
        } catch (error) {
          if (error instanceof ApiError && error.status === 401) { signOut(t("The password is wrong.")); return; }
          rec.problem = true;
          setNoteStatus(error instanceof ApiError && error.status === 0 ? error.message : t(error.message));
        }
      }
      if (notes.rec === rec && !rec.problem) setNoteStatus(t("Listening…"));
    })().finally(() => { rec.busy = null; if (rec.queue.length) pumpPhrases(rec); });
  }

  /** The words of a phrase go to the end of the open note. */
  function addWords(words) {
    const open = notes.open;
    open.text = MbLive.appendWords(open.text, words);
    if (ui.nText) { ui.nText.value = open.text; ui.nText.scrollTop = ui.nText.scrollHeight; }
    noteEdited();
    if (notes.rec) setNoteStatus(t("Listening…"));
  }

  async function stopNoteRecording() {
    const rec = notes.rec;
    if (!rec) return;
    notes.rec = null;                                          // no new sound is taken from now on
    window.clearInterval(rec.meter);
    for (const track of rec.stream.getTracks()) track.stop();
    rec.node.port.onmessage = null;
    rec.detector.flush();                                      // a phrase that was half-way is read as well
    setNoteStatus(t("Finishing…"));
    while (rec.busy || rec.queue.length) { pumpPhrases(rec); await (rec.busy || Promise.resolve()); }
    try { rec.source.disconnect(); rec.node.disconnect(); await rec.context.close(); } catch { /* already closed */ }
    await flushNote();
    setNoteStatus(defaultNoteStatus());
    renderNotesList();
    renderNoteEditor();
  }

  // ---- the keys of recording ---------------------------------------------------------------------------------------------------------
  // They work while this page is open. Press Change keys, press the keys, click Done (with the mouse: every key goes to the keys being chosen).

  function renderNoteKeys() {
    if (!ui.nKeys) return;
    ui.nKeys.replaceChildren(h("label", null, t("Keys to start and stop recording")),
      h("div", { class: "row" }, h("span", { class: "keybox" }, notes.keys ? MbLive.comboLabel(notes.keys) : t("Not set")),
        h("button", { class: "btn", type: "button", onClick: () => beginCapture() }, t("Change keys"))),
      h("p", { class: "keys" }, t("The keys work while this page is open.")));
  }

  const keyProblemText = (combo) => {
    const problem = MbLive.comboProblem(combo);
    return problem === "needsModifier" ? t("Hold Alt (or Ctrl and Shift) while you press the key, or use a function key such as F9 on its own.")
      : problem === "common" ? t("Almost every program uses these keys (copying, pasting, closing and the like). Choose other keys.") : "";
  };

  function beginCapture() {
    const capture = notes.capture = { pending: null, held: "" };
    const combo = h("div", { class: "combo", "aria-live": "polite" });
    const problem = h("div", { class: "problem", role: "alert" });
    const hint = h("p", { class: "keys", hidden: true }, t("Click here first, then press the keys."));
    const button = (label, action, primary) => h("button", { class: "btn" + (primary ? " primary" : ""), type: "button", tabindex: "-1", onMouseDown: (event) => event.preventDefault(), onClick: action }, label);
    const done = button(t("Done"), () => finishCapture(capture.pending), true);
    const panel = h("div", { class: "capture", tabindex: "0", "aria-label": t("Press the keys you want to use") }, combo, h("p", { class: "muted" }, t("Press the keys together, then click Done.")), hint, problem,
      h("div", { class: "row" }, done, button(t("Cancel"), () => endCapture()), button(t("Turn off"), () => finishCapture(null))));
    const show = () => {
      combo.textContent = capture.held ? capture.held + "…" : capture.pending ? MbLive.comboLabel(capture.pending) : t("Press the keys you want to use");
      problem.textContent = capture.pending ? keyProblemText(capture.pending) : "";
      done.disabled = !capture.pending || !!problem.textContent;
    };
    const held = (event) => (event.ctrlKey ? "Ctrl+" : "") + (event.altKey ? "Alt+" : "") + (event.shiftKey ? "Shift+" : "");
    panel.addEventListener("keydown", (event) => {
      event.preventDefault(); event.stopPropagation();           // every key is for the keys being chosen
      if (event.repeat) return;
      const next = MbLive.comboOf(event);
      if (next) { capture.pending = next; capture.held = ""; } else capture.held = held(event);
      show();
    });
    // Letting go of a key shows nothing new, except that the modifiers shown while they are being held follow what is still held; the keys that were pressed stay on show.
    panel.addEventListener("keyup", (event) => { event.preventDefault(); if (capture.held) { capture.held = held(event); show(); } });
    panel.addEventListener("focus", () => { hint.hidden = true; });
    panel.addEventListener("blur", () => { hint.hidden = false; });
    panel.addEventListener("mousedown", () => panel.focus());
    ui.nKeys.replaceChildren(h("label", null, t("Keys to start and stop recording")), panel);
    show();
    panel.focus();
  }

  function endCapture() { notes.capture = null; renderNoteKeys(); }

  function finishCapture(combo) {
    notes.keys = combo;
    keep.set("localStorage", "mb-notes-keys", combo ? MbLive.comboId(combo) : null);
    endCapture();
  }

  document.addEventListener("keydown", (event) => {
    if (event.repeat || notes.capture || !notes.keys || !ui.notesView || (state.health && state.health.passwordRequired && !state.password)) return;
    const combo = MbLive.comboOf(event);
    if (!combo || !MbLive.same(combo, notes.keys)) return;
    event.preventDefault();
    selectTab("notes");
    toggleNoteRecording();
  });
  /** The person signed out (or the password was refused): the note that was open is let go of without being saved, and a recording stops. */
  function abandonNotes() {
    const rec = notes.rec;
    notes.rec = null;
    if (rec) { window.clearInterval(rec.meter); for (const track of rec.stream.getTracks()) track.stop(); rec.context.close().catch(() => { }); }
    window.clearTimeout(notes.timer);
    Object.assign(notes, { list: [], open: null, failed: "", notice: "", status: "", capture: null });
  }

  window.addEventListener("beforeunload", (event) => { if (state.dirty || recorder.media || notes.rec || (notes.open && notes.open.dirty)) { event.preventDefault(); event.returnValue = ""; } });

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
