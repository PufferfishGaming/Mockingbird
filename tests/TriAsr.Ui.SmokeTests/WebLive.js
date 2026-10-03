"use strict";
// Runs the code of the web page's live.js (src/TriAsr.App/Web/live.js) on the input a test gives it and prints the result as JSON. Only WebLiveTests uses this file.
const fs = require("fs");
const crypto = require("crypto");

const request = JSON.parse(fs.readFileSync(0, "utf8"));
const live = require(request.live);
const sha = (bytes) => crypto.createHash("sha256").update(bytes).digest("hex");
const samplesOf = (file) => { const bytes = fs.readFileSync(file); return new Int16Array(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.length)); };

function detect() {
  const samples = samplesOf(request.file);
  const found = [];
  const detector = new live.UtteranceDetector((phrase) => found.push({ durationMs: phrase.durationMs, speechMs: phrase.speechMs, sha: sha(Buffer.from(phrase.pcm.buffer, phrase.pcm.byteOffset, phrase.pcm.byteLength)) }));
  for (let at = 0; at < samples.length; at += request.chunk) detector.feed(samples.subarray(at, Math.min(samples.length, at + request.chunk)));
  if (request.flush) detector.flush();
  return found;
}

function wav() {
  const samples = samplesOf(request.file);
  const file = live.encodeWav(samples);
  return { sha: sha(Buffer.from(file.buffer, file.byteOffset, file.byteLength)), length: file.length };
}

function resample() {
  const resampler = new live.Resampler(request.rate);
  const total = Math.round(request.rate * request.seconds);
  const input = new Float32Array(total);
  for (let i = 0; i < total; i++) input[i] = Math.sin(2 * Math.PI * request.hz * i / request.rate) * 0.5;
  const pieces = [];
  for (let at = 0; at < total; at += request.chunk) pieces.push(resampler.push(input.subarray(at, Math.min(total, at + request.chunk))));
  const count = pieces.reduce((sum, piece) => sum + piece.length, 0);
  let sum = 0, peak = 0;
  for (const piece of pieces) for (const sample of piece) { const value = sample / 32768; sum += value * value; peak = Math.max(peak, Math.abs(value)); }
  return { count, rms: Math.sqrt(sum / Math.max(1, count)), peak };
}

function keys() {
  return request.events.map((event) => {
    const combo = live.comboOf(event);
    return { combo, label: live.comboLabel(combo), id: live.comboId(combo), problem: combo ? live.comboProblem(combo) : "none", again: live.parseCombo(live.comboId(combo)) };
  });
}

const operations = {
  detect, wav, resample, keys,
  append: () => request.cases.map(([text, words]) => live.appendWords(text, words)),
  languages: () => ({ split: request.saved.map((value) => live.splitLanguages(value, request.known)), joined: request.chosen.map(([first, second]) => live.joinLanguages(first, second)) }),
  defaults: () => live.DEFAULTS
};
process.stdout.write(JSON.stringify(operations[request.op]()));
