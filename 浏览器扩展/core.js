/* Shared input validation, no external network destinations. */
(function(root) {
  function parseCode(raw) {
    const text = String(raw || '').trim();
    const match = /^52637:([A-Za-z0-9_-]{32})$/.exec(text);
    if (!match) throw new Error('连接码格式不正确，请从桌面工具复制完整连接码。');
    return {url: 'http://127.0.0.1:52637', key: match[1]};
  }
  function normalizedCrop(roi, width, height) {
    if (!roi || !Number.isFinite(roi.left + roi.top + roi.right + roi.bottom)
      || roi.left < 0 || roi.top < 0 || roi.right > 1 || roi.bottom > 1
      || roi.right - roi.left < .01 || roi.bottom - roi.top < .01) return {x:0,y:0,width,height};
    const x = Math.floor(roi.left * width), y = Math.floor(roi.top * height);
    return {x, y, width:Math.max(1,Math.ceil(roi.right * width)-x), height:Math.max(1,Math.ceil(roi.bottom * height)-y)};
  }
  function withTimeout(task, milliseconds, message='操作超时，请点击“修复并重连”。') {
    let timer;
    return Promise.race([
      Promise.resolve(task),
      new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(message)), milliseconds); })
    ]).finally(() => clearTimeout(timer));
  }
  const value = {parseCode, normalizedCrop, withTimeout};
  root.HoverCore = value;
  if (typeof module !== 'undefined') module.exports = value;
})(globalThis);
