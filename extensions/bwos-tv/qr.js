// Minimal QR code generator (byte mode, error correction level M, versions 1–6: up to 106 bytes –
// plenty for a URL). Self-contained so nothing is sent to a QR web service.
// Usage: BWOSQR.svg('http://…', { dark: '#000', light: '#fff' }) → SVG markup string.
(function (root) {
  'use strict';

  // [total codewords per block, data codewords per block, block count] for EC level M
  const BLOCKS = {
    1: [[26, 16, 1]], 2: [[44, 28, 1]], 3: [[70, 44, 1]],
    4: [[50, 32, 2]], 5: [[67, 43, 2]], 6: [[43, 27, 4]]
  };
  const ALIGN = { 1: [], 2: [6, 18], 3: [6, 22], 4: [6, 26], 5: [6, 30], 6: [6, 34] };
  const REMAINDER = { 1: 0, 2: 7, 3: 7, 4: 7, 5: 7, 6: 7 };

  function gfMul(x, y) {
    let z = 0;
    for (let i = 7; i >= 0; i--) {
      z = (z << 1) ^ ((z >>> 7) * 0x11d);
      z ^= ((y >>> i) & 1) * x;
    }
    return z & 0xff;
  }

  function rsDivisor(degree) {
    const r = new Array(degree).fill(0);
    r[degree - 1] = 1;
    let root = 1;
    for (let i = 0; i < degree; i++) {
      for (let j = 0; j < r.length; j++) {
        r[j] = gfMul(r[j], root);
        if (j + 1 < r.length) r[j] ^= r[j + 1];
      }
      root = gfMul(root, 0x02);
    }
    return r;
  }

  function rsRemainder(data, divisor) {
    const r = divisor.map(() => 0);
    for (const b of data) {
      const factor = b ^ r.shift();
      r.push(0);
      divisor.forEach((coef, i) => { r[i] ^= gfMul(coef, factor); });
    }
    return r;
  }

  function encode(text) {
    const bytes = Array.from(new TextEncoder().encode(text));
    let version = 1;
    for (; version <= 6; version++) {
      const capacity = BLOCKS[version].reduce((s, [, d, n]) => s + d * n, 0);
      if (4 + 8 + bytes.length * 8 <= capacity * 8) break;
    }
    if (version > 6) throw new Error('QR: text too long');
    const dataCapacity = BLOCKS[version].reduce((s, [, d, n]) => s + d * n, 0);

    // Bit stream: mode (byte = 0100), length (8 bits for v1–9), data, terminator, padding.
    const bits = [];
    const put = (val, len) => { for (let i = len - 1; i >= 0; i--) bits.push((val >>> i) & 1); };
    put(0b0100, 4);
    put(bytes.length, 8);
    bytes.forEach((b) => put(b, 8));
    put(0, Math.min(4, dataCapacity * 8 - bits.length));
    while (bits.length % 8) bits.push(0);
    const data = [];
    for (let i = 0; i < bits.length; i += 8) data.push(parseInt(bits.slice(i, i + 8).join(''), 2));
    for (let pad = 0xec; data.length < dataCapacity; pad ^= 0xec ^ 0x11) data.push(pad);

    // Split into blocks, add Reed–Solomon error correction, interleave.
    const blocks = [];
    let offset = 0;
    for (const [total, dataLen, count] of BLOCKS[version]) {
      const divisor = rsDivisor(total - dataLen);
      for (let i = 0; i < count; i++) {
        const d = data.slice(offset, offset + dataLen);
        offset += dataLen;
        blocks.push({ d, e: rsRemainder(d, divisor) });
      }
    }
    const codewords = [];
    const maxData = Math.max(...blocks.map((b) => b.d.length));
    for (let i = 0; i < maxData; i++) blocks.forEach((b) => { if (i < b.d.length) codewords.push(b.d[i]); });
    const ecLen = blocks[0].e.length;
    for (let i = 0; i < ecLen; i++) blocks.forEach((b) => codewords.push(b.e[i]));
    return { version, codewords };
  }

  function build(text) {
    const { version, codewords } = encode(text);
    const size = version * 4 + 17;
    const modules = Array.from({ length: size }, () => new Array(size).fill(false));
    const isFn = Array.from({ length: size }, () => new Array(size).fill(false));
    const set = (x, y, dark) => { modules[y][x] = dark; isFn[y][x] = true; };

    // Timing patterns
    for (let i = 0; i < size; i++) { set(6, i, i % 2 === 0); set(i, 6, i % 2 === 0); }
    // Finder patterns (+ separators)
    for (const [cx, cy] of [[3, 3], [size - 4, 3], [3, size - 4]]) {
      for (let dy = -4; dy <= 4; dy++) {
        for (let dx = -4; dx <= 4; dx++) {
          const x = cx + dx, y = cy + dy;
          if (x < 0 || y < 0 || x >= size || y >= size) continue;
          const dist = Math.max(Math.abs(dx), Math.abs(dy));
          set(x, y, dist !== 2 && dist !== 4);
        }
      }
    }
    // Alignment patterns
    const al = ALIGN[version];
    for (let i = 0; i < al.length; i++) {
      for (let j = 0; j < al.length; j++) {
        if ((i === 0 && j === 0) || (i === 0 && j === al.length - 1) || (i === al.length - 1 && j === 0)) continue;
        for (let dy = -2; dy <= 2; dy++) for (let dx = -2; dx <= 2; dx++) set(al[i] + dx, al[j] + dy, Math.max(Math.abs(dx), Math.abs(dy)) !== 1);
      }
    }
    // Reserve format areas (real bits drawn after masking) and the dark module
    const drawFormat = (mask) => {
      const dataBits = (0 << 3) | mask; // EC level M = 00
      let rem = dataBits;
      for (let i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >>> 9) * 0x537);
      const b = ((dataBits << 10) | rem) ^ 0x5412;
      const bit = (i) => ((b >>> i) & 1) !== 0;
      for (let i = 0; i <= 5; i++) set(8, i, bit(i));
      set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
      for (let i = 9; i < 15; i++) set(14 - i, 8, bit(i));
      for (let i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
      for (let i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
      set(8, size - 8, true);
    };
    drawFormat(0);

    // Data, in the zig-zag order of the spec
    const allBits = codewords.length * 8 + REMAINDER[version];
    let k = 0;
    for (let right = size - 1; right >= 1; right -= 2) {
      if (right === 6) right = 5;
      for (let vert = 0; vert < size; vert++) {
        for (let j = 0; j < 2; j++) {
          const x = right - j;
          const upward = ((right + 1) & 2) === 0;
          const y = upward ? size - 1 - vert : vert;
          if (isFn[y][x] || k >= allBits) continue;
          modules[y][x] = k < codewords.length * 8 ? ((codewords[k >>> 3] >>> (7 - (k & 7))) & 1) === 1 : false;
          k++;
        }
      }
    }

    // Try every mask, keep the one with the lowest penalty
    const MASKS = [
      (x, y) => (x + y) % 2 === 0, (x, y) => y % 2 === 0, (x) => x % 3 === 0, (x, y) => (x + y) % 3 === 0,
      (x, y) => (Math.floor(x / 3) + Math.floor(y / 2)) % 2 === 0, (x, y) => ((x * y) % 2) + ((x * y) % 3) === 0,
      (x, y) => (((x * y) % 2) + ((x * y) % 3)) % 2 === 0, (x, y) => (((x + y) % 2) + ((x * y) % 3)) % 2 === 0
    ];
    const applyMask = (m) => {
      for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) if (!isFn[y][x] && MASKS[m](x, y)) modules[y][x] = !modules[y][x];
    };
    const penalty = () => {
      let p = 0, dark = 0;
      for (let y = 0; y < size; y++) {
        for (let dir = 0; dir < 2; dir++) {
          let run = 1;
          for (let i = 1; i < size; i++) {
            const a = dir ? modules[i][y] : modules[y][i], b = dir ? modules[i - 1][y] : modules[y][i - 1];
            if (a === b) { run++; if (run === 5) p += 3; else if (run > 5) p++; } else run = 1;
          }
        }
        for (let x = 0; x < size; x++) {
          if (modules[y][x]) dark++;
          if (x < size - 1 && y < size - 1) {
            const c = modules[y][x];
            if (c === modules[y][x + 1] && c === modules[y + 1][x] && c === modules[y + 1][x + 1]) p += 3;
          }
        }
      }
      p += Math.floor(Math.abs(dark * 20 - size * size * 10) / (size * size)) * 10;
      return p;
    };
    let best = 0, bestPenalty = Infinity;
    for (let m = 0; m < 8; m++) {
      applyMask(m); drawFormat(m);
      const p = penalty();
      if (p < bestPenalty) { bestPenalty = p; best = m; }
      applyMask(m); // undo (XOR)
    }
    applyMask(best);
    drawFormat(best);
    return modules;
  }

  function svg(text, opts) {
    const o = opts || {};
    const m = build(text);
    const quiet = 4, n = m.length + quiet * 2;
    let d = '';
    m.forEach((row, y) => row.forEach((dark, x) => { if (dark) d += `M${x + quiet},${y + quiet}h1v1h-1z`; }));
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${n} ${n}" shape-rendering="crispEdges">` +
      `<rect width="${n}" height="${n}" fill="${o.light || '#fff'}"/><path d="${d}" fill="${o.dark || '#000'}"/></svg>`;
  }

  root.BWOSQR = { svg, build };
})(typeof globalThis !== 'undefined' ? globalThis : self);
