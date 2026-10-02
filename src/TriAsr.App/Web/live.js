"use strict";
// What the web page needs to take notes by voice, without anything from outside this server: cutting the microphone's sound into phrases at the pauses (the same rules as
// UtteranceDetector.cs, and a test feeds both the same sound), turning a phrase into a WAV file, bringing the microphone's rate down to 16 kHz, and the keys a person can choose
// to start and stop recording. app.js uses it as window.MbLive; a test loads the same file in Node.
(() => {
  const SAMPLE_RATE = 16000, FRAME_MS = 20, FRAME = SAMPLE_RATE / 1000 * FRAME_MS;
  const FLOOR_START = 0.004, CALIBRATION_FRAMES = 5;
  const DEFAULTS = { startLevel: 0.012, noiseMultiplier: 3.0, startMs: 80, preRollMs: 300, endSilenceMs: 700, tailMs: 250, minSpeechMs: 300, maxMs: 25000 };

  const clamp = (value, low, high) => Math.min(high, Math.max(low, value));

  function rmsOf(frame) {
    let sum = 0;
    for (let i = 0; i < frame.length; i++) { const sample = frame[i] / 32768; sum += sample * sample; }
    return frame.length === 0 ? 0 : Math.sqrt(sum / frame.length);
  }

  /**
   * Cuts a stream of 16 kHz 16-bit sound into phrases. It listens for sound that is clearly louder than the room, collects it with a little before and after, and hands the phrase on
   * when the speaker pauses. The room's noise is learnt as it goes. onUtterance gets { pcm: Int16Array, durationMs, speechMs }.
   */
  class UtteranceDetector {
    constructor(onUtterance, options) {
      this.onUtterance = onUtterance;
      this.o = { ...DEFAULTS, ...(options || {}) };
      this.partial = new Int16Array(FRAME); this.partialLength = 0;
      this.preRoll = []; this.frames = [];
      this.loudRun = 0; this.quietRun = 0; this.speaking = false;
      this.floor = FLOOR_START; this.calibrated = 0; this.calibrationMinimum = Infinity; this.level = 0;
    }

    get threshold() { return Math.max(this.o.startLevel, this.floor * this.o.noiseMultiplier); }
    get inSpeech() { return this.speaking; }

    /** Takes sound of any length; a phrase that is complete is handed to the callback. */
    feed(samples) {
      const ready = [];
      let at = 0;
      while (at < samples.length) {
        const take = Math.min(FRAME - this.partialLength, samples.length - at);
        this.partial.set(samples.subarray(at, at + take), this.partialLength);
        this.partialLength += take; at += take;
        if (this.partialLength < FRAME) break;
        this.partialLength = 0;
        this.step(this.partial.slice(), ready);
      }
      for (const utterance of ready) this.onUtterance(utterance);
    }

    /** The recording ends: a phrase that is half-way is handed on, and the detector is ready for the next time. */
    flush() {
      const ready = [];
      if (this.speaking) this.finish(ready);
      this.partialLength = 0; this.preRoll = []; this.frames = [];
      this.loudRun = 0; this.quietRun = 0; this.speaking = false; this.level = 0;
      for (const utterance of ready) this.onUtterance(utterance);
    }

    step(pcm, ready) {
      const rms = rmsOf(pcm);
      this.level = rms;
      // The first moments teach the detector how loud the room is (the quietest of them). Nothing starts a phrase before that is done, but the sound is kept in the pre-roll.
      const calibrating = this.calibrated < CALIBRATION_FRAMES;
      if (calibrating) {
        this.calibrationMinimum = Math.min(this.calibrationMinimum, rms);
        if (++this.calibrated === CALIBRATION_FRAMES) this.floor = clamp(this.calibrationMinimum, 0.0005, 0.05);
      }
      const threshold = Math.max(this.o.startLevel, this.floor * this.o.noiseMultiplier);
      const loud = !calibrating && rms >= threshold;
      const holds = rms >= threshold * 0.7;       // inside a phrase a dip to 70% of the threshold is still the same word
      const frame = { pcm, rms, loud };
      if (!this.speaking) {
        if (!loud && this.calibrated >= CALIBRATION_FRAMES) this.floor = clamp(this.floor * 0.95 + rms * 0.05, 0.0005, 0.05);
        this.preRoll.push(frame);
        const keep = Math.floor((this.o.preRollMs + this.o.startMs) / FRAME_MS) + 1;
        while (this.preRoll.length > keep) this.preRoll.shift();
        this.loudRun = loud ? this.loudRun + 1 : 0;
        if (this.loudRun >= Math.max(1, Math.floor(this.o.startMs / FRAME_MS))) {
          this.speaking = true; this.quietRun = 0;
          this.frames.push(...this.preRoll);
          this.preRoll = [];
        }
        return;
      }
      this.frames.push(frame);
      this.quietRun = holds ? 0 : this.quietRun + 1;
      if (this.quietRun * FRAME_MS >= this.o.endSilenceMs) { this.finish(ready); return; }
      if (this.frames.length * FRAME_MS >= this.o.maxMs) this.split(ready);
    }

    /** The phrase ends at a pause: the silence after the words is cut down to a short tail. */
    finish(ready) {
      const keepSilence = Math.floor(this.o.tailMs / FRAME_MS);
      let trailing = 0;
      for (let i = this.frames.length - 1; i >= 0 && !this.frames[i].loud; i--) trailing++;
      if (trailing > keepSilence) this.frames.splice(this.frames.length - (trailing - keepSilence), trailing - keepSilence);
      this.emit(this.frames, ready);
      this.frames = []; this.speaking = false; this.loudRun = 0; this.quietRun = 0;
    }

    /** A phrase that goes on for too long is cut at its quietest moment in the last two seconds; the rest is the start of the next phrase. */
    split(ready) {
      const from = Math.max(1, this.frames.length - Math.floor(2000 / FRAME_MS));
      let cut = from;
      for (let i = from; i < this.frames.length; i++) if (this.frames[i].rms < this.frames[cut].rms) cut = i;
      const first = this.frames.slice(0, cut + 1);
      const rest = this.frames.slice(cut + 1);
      this.emit(first, ready);
      this.frames = rest;
      this.quietRun = 0;
    }

    emit(frames, ready) {
      const speechFrames = frames.filter((frame) => frame.loud).length;
      if (speechFrames * FRAME_MS < this.o.minSpeechMs) return;
      const pcm = new Int16Array(frames.length * FRAME);
      frames.forEach((frame, index) => pcm.set(frame.pcm, index * FRAME));
      ready.push({ pcm, durationMs: frames.length * FRAME_MS, speechMs: speechFrames * FRAME_MS });
    }
  }

  /** A WAV file (16 kHz, mono, 16-bit) around the sound of a phrase. */
  function encodeWav(pcm) {
    const bytes = new ArrayBuffer(44 + pcm.length * 2);
    const view = new DataView(bytes);
    const text = (at, value) => { for (let i = 0; i < value.length; i++) view.setUint8(at + i, value.charCodeAt(i)); };
    text(0, "RIFF"); view.setUint32(4, 36 + pcm.length * 2, true); text(8, "WAVE"); text(12, "fmt ");
    view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
    view.setUint32(24, SAMPLE_RATE, true); view.setUint32(28, SAMPLE_RATE * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
    text(36, "data"); view.setUint32(40, pcm.length * 2, true);
    new Int16Array(bytes, 44).set(pcm);
    return new Uint8Array(bytes);
  }

  /**
   * Brings the microphone's sound (floats, at the rate of the browser's audio) down to 16 kHz 16-bit by averaging. Each output sample is the mean of the input samples it covers.
   * The position of the next output sample is kept between pieces (a piece may end in the middle of one), so that the sound does not slip against the clock.
   */
  class Resampler {
    constructor(fromRate) { this.ratio = fromRate / SAMPLE_RATE; this.carry = new Float32Array(0); this.phase = 0; }

    push(chunk) {
      const data = new Float32Array(this.carry.length + chunk.length);
      data.set(this.carry); data.set(chunk, this.carry.length);
      const out = [];
      let position = this.phase;
      while (position + this.ratio <= data.length + 1e-9) {
        const from = Math.floor(position), to = Math.min(data.length, Math.max(from + 1, Math.floor(position + this.ratio + 1e-9)));
        let sum = 0;
        for (let i = from; i < to; i++) sum += data[i];
        out.push(clamp(Math.round(sum / (to - from) * 32767), -32768, 32767));
        position += this.ratio;
      }
      const keep = Math.min(data.length, Math.floor(position));
      this.carry = data.slice(keep);
      this.phase = position - keep;
      return Int16Array.from(out);
    }
  }
  /** Puts the words of a phrase after the text of a note: with a space between them, except where the text is empty or ends in a space or a line break, or where either side is written without spaces. */
  const unspaced = (c) => /[぀-ヿ㐀-鿿가-힯＀-￯。、]/.test(c);
  function appendWords(text, words) {
    if (!words) return text;
    if (!text || /\s/.test(text[text.length - 1]) || unspaced(text[text.length - 1]) || unspaced(words[0])) return text + words;
    return text + " " + words;
  }

  // ---- the keys of recording -----------------------------------------------------------------------------------------------------------
  // A page cannot listen to the keyboard while another program has it, so these keys work while the page is open. They are chosen the way the programs' are: press them, then click Done.

  const MODIFIER_CODES = new Set(["ControlLeft", "ControlRight", "AltLeft", "AltRight", "ShiftLeft", "ShiftRight", "MetaLeft", "MetaRight", "CapsLock", "NumLock", "ScrollLock", "ContextMenu", "OS"]);

  function keyName(code) {
    if (/^Key[A-Z]$/.test(code)) return code.slice(3);
    if (/^Digit\d$/.test(code)) return code.slice(5);
    if (/^F\d{1,2}$/.test(code)) return code;
    if (/^Numpad\d$/.test(code)) return "Num" + code.slice(6);
    const names = { Space: "Space", Enter: "Enter", Tab: "Tab", Escape: "Esc", Backspace: "Backspace", Insert: "Insert", Delete: "Delete", Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown",
      ArrowLeft: "Left", ArrowUp: "Up", ArrowRight: "Right", ArrowDown: "Down", Minus: "-", Equal: "=", Comma: ",", Period: ".", Slash: "/", Semicolon: ";", Quote: "'", Backquote: "`",
      BracketLeft: "[", BracketRight: "]", Backslash: "\\", NumpadAdd: "Num+", NumpadSubtract: "Num-", NumpadMultiply: "Num*", NumpadDivide: "Num/", NumpadDecimal: "Num." };
    return names[code] || null;
  }

  /** The keys of a key event: { ctrl, alt, shift, code } — or null while only modifier keys are down. */
  function comboOf(event) {
    if (MODIFIER_CODES.has(event.code) || !keyName(event.code)) return null;
    return { ctrl: !!event.ctrlKey, alt: !!event.altKey, shift: !!event.shiftKey, code: event.code };
  }

  const comboLabel = (combo) => combo ? (combo.ctrl ? "Ctrl+" : "") + (combo.alt ? "Alt+" : "") + (combo.shift ? "Shift+" : "") + keyName(combo.code) : "";
  const comboId = (combo) => combo ? (combo.ctrl ? "ctrl+" : "") + (combo.alt ? "alt+" : "") + (combo.shift ? "shift+" : "") + combo.code : "";

  function parseCombo(text) {
    if (!text) return null;
    const parts = text.split("+");
    const code = parts.pop();
    const combo = { ctrl: parts.includes("ctrl"), alt: parts.includes("alt"), shift: parts.includes("shift"), code };
    return keyName(code) && parts.every((part) => ["ctrl", "alt", "shift"].includes(part)) ? combo : null;
  }

  /** Why the keys cannot be used: "needsModifier", "common" or null. A page sees nothing of the keys the browser keeps for itself (Ctrl+T, Ctrl+W...), so only Alt, or Ctrl with Shift, or a function key will do. */
  function comboProblem(combo) {
    if (!combo || !keyName(combo.code)) return "none";
    const functionKey = /^F\d{1,2}$/.test(combo.code);
    if (!functionKey && !combo.alt && !(combo.ctrl && combo.shift)) return "needsModifier";
    if (combo.alt && !combo.ctrl && !combo.shift && ["F4", "Tab", "Space", "Escape", "ArrowLeft", "ArrowRight", "Home"].includes(combo.code)) return "common";
    return null;
  }

  const same = (first, second) => !!first && !!second && comboId(first) === comboId(second);

  const api = { SAMPLE_RATE, FRAME_MS, DEFAULTS, UtteranceDetector, encodeWav, Resampler, appendWords, comboOf, comboLabel, comboId, parseCombo, comboProblem, same, keyName };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else window.MbLive = api;
})();
