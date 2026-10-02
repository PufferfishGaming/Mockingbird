"use strict";
// Hands the microphone's sound to the page in pieces of about a fiftieth of a second. It runs in the browser's audio thread, which is why it is a file of its own.
class MockingbirdTap extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buffer = new Float32Array(1024);
    this.filled = 0;
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (!channel) return true;
    let at = 0;
    while (at < channel.length) {
      const take = Math.min(this.buffer.length - this.filled, channel.length - at);
      this.buffer.set(channel.subarray(at, at + take), this.filled);
      this.filled += take; at += take;
      if (this.filled === this.buffer.length) {
        const piece = this.buffer;
        this.port.postMessage(piece, [piece.buffer]);
        this.buffer = new Float32Array(1024);
        this.filled = 0;
      }
    }
    return true;
  }
}

registerProcessor("mb-tap", MockingbirdTap);