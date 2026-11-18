// Klive desktop main-world fingerprint normaliser.
//
// Runs at document_start in the MAIN world of every page/frame, before site scripts, and makes the
// page-visible browser environment look like an ordinary consumer machine instead of a GPU-less
// automation container. The dominant fix is the WebGL UNMASKED_RENDERER: on Xvfb + --disable-gpu it
// otherwise reads "Google SwiftShader" / "Mesa llvmpipe", a textbook headless signal. Every patch is
// wrapped so a single failure can never throw into (or break) the host page — worst case a value is
// left untouched. Persona values arrive as self.__KFP__ (set by persona.js, written per desktop).
(() => {
  "use strict";
  const P = (typeof self !== "undefined" && self.__KFP__) || {};

  const define = (obj, prop, getter) => {
    try { Object.defineProperty(obj, prop, { get: getter, configurable: true, enumerable: true }); }
    catch (e) { /* leave the original in place */ }
  };

  // navigator.webdriver → false (belt-and-suspenders alongside --disable-blink-features).
  try { define(Navigator.prototype, "webdriver", () => false); } catch (e) {}

  // Stable, plausible CPU/memory for this persona.
  try { if (P.cores) define(Navigator.prototype, "hardwareConcurrency", () => P.cores); } catch (e) {}
  try { if (P.mem) define(Navigator.prototype, "deviceMemory", () => P.mem); } catch (e) {}
  try {
    if (Array.isArray(P.langs) && P.langs.length) {
      const langs = Object.freeze(P.langs.slice());
      define(Navigator.prototype, "languages", () => langs);
    }
  } catch (e) {}

  // WebGL vendor/renderer spoof — the headline fix. 37445 = UNMASKED_VENDOR_WEBGL,
  // 37446 = UNMASKED_RENDERER_WEBGL.
  const patchGL = (proto) => {
    if (!proto || !proto.getParameter) return;
    const original = proto.getParameter;
    proto.getParameter = function (param) {
      try {
        if (param === 37445 && P.gpuVendor) return P.gpuVendor;
        if (param === 37446 && P.gpuRenderer) return P.gpuRenderer;
      } catch (e) {}
      return original.call(this, param);
    };
    try { Object.defineProperty(proto.getParameter, "name", { value: "getParameter" }); } catch (e) {}
    try { proto.getParameter.toString = () => "function getParameter() { [native code] }"; } catch (e) {}
  };
  try { patchGL(self.WebGLRenderingContext && WebGLRenderingContext.prototype); } catch (e) {}
  try { patchGL(self.WebGL2RenderingContext && WebGL2RenderingContext.prototype); } catch (e) {}

  // Deterministic, sub-perceptual canvas noise so this persona's canvas hash is stable but differs
  // from every other agent's (anti-clustering) — a sparse ±1 tweak on the pixel read path only.
  try {
    const seed = (P.cnv | 0) || 1;
    const tweak = (data) => {
      // Touch roughly one pixel in ~300 with a deterministic ±1 on one channel.
      for (let i = 0; i < data.length; i += 4 * 307) {
        let h = (seed ^ i) >>> 0;
        h = (h * 2654435761) >>> 0;
        const delta = (h & 2) - 1;            // -1, 0, or +1
        const c = i + (h % 3);                 // R, G, or B
        if (c < data.length) data[c] = Math.max(0, Math.min(255, data[c] + delta));
      }
    };
    const CtxProto = self.CanvasRenderingContext2D && CanvasRenderingContext2D.prototype;
    if (CtxProto && CtxProto.getImageData) {
      const origGetImageData = CtxProto.getImageData;
      CtxProto.getImageData = function (...args) {
        const img = origGetImageData.apply(this, args);
        try { tweak(img.data); } catch (e) {}
        return img;
      };
      try { CtxProto.getImageData.toString = () => "function getImageData() { [native code] }"; } catch (e) {}

      const CanvasProto = self.HTMLCanvasElement && HTMLCanvasElement.prototype;
      const perturbSelf = (canvas) => {
        try {
          const w = canvas.width, h = canvas.height;
          if (w > 0 && h > 0 && w * h <= 4_000_000) {
            const ctx = canvas.getContext("2d");
            if (ctx) {
              const img = origGetImageData.call(ctx, 0, 0, w, h);
              tweak(img.data);
              ctx.putImageData(img, 0, 0);
            }
          }
        } catch (e) {}
      };
      if (CanvasProto && CanvasProto.toDataURL) {
        const origToDataURL = CanvasProto.toDataURL;
        CanvasProto.toDataURL = function (...args) { perturbSelf(this); return origToDataURL.apply(this, args); };
        try { CanvasProto.toDataURL.toString = () => "function toDataURL() { [native code] }"; } catch (e) {}
      }
      if (CanvasProto && CanvasProto.toBlob) {
        const origToBlob = CanvasProto.toBlob;
        CanvasProto.toBlob = function (...args) { perturbSelf(this); return origToBlob.apply(this, args); };
        try { CanvasProto.toBlob.toString = () => "function toBlob() { [native code] }"; } catch (e) {}
      }
    }
  } catch (e) {}

  // A never-prompted headless build can report Notification.permission === "denied"; a normal
  // browser sits at "default" until asked.
  try {
    if (self.Notification && Notification.permission === "denied") {
      define(Notification, "permission", () => "default");
    }
  } catch (e) {}
})();
