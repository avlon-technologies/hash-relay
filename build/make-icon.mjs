// Generates assets/hashrelay.ico (16/24/32/48/64/128/256) and assets/hashrelay.png (256) with no dependencies.
// The mark: a rounded square with a violet→cyan diagonal gradient and a white "#" drawn as four rounded bars.
import { writeFileSync, mkdirSync } from "node:fs";
import { deflateSync } from "node:zlib";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, "..", "assets");
mkdirSync(out, { recursive: true });

const crcTable = new Int32Array(256).map((_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c; });
const crc32 = (buf) => { let c = -1; for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8); return (c ^ -1) >>> 0; };
const chunk = (type, data) => {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
};
function png(size, rgba) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) { raw[y * (size * 4 + 1)] = 0; rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4); }
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
}

// geometry in a 64-unit design space (matches wwwroot/favicon.svg)
const bars = [
  [[24, 14], [21, 50]], [[43, 14], [40, 50]],   // two slanted uprights
  [[14, 25], [52, 25]], [[12, 39], [50, 39]],   // two horizontals
];
const barHalf = 3.1;
const radius = 16;
const lerp = (a, b, t) => a + (b - a) * t;
const grad = (t) => [lerp(0x7c, 0x22, t), lerp(0x6c, 0xd3, t), lerp(0xff, 0xee, t)];
function segDist(px, py, [[x1, y1], [x2, y2]]) {
  const dx = x2 - x1, dy = y2 - y1, l2 = dx * dx + dy * dy;
  const t = Math.max(0, Math.min(1, ((px - x1) * dx + (py - y1) * dy) / l2));
  return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}
function roundRectInside(x, y) {
  const qx = Math.abs(x - 32) - (32 - radius), qy = Math.abs(y - 32) - (32 - radius);
  return Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - radius;
}
function render(size) {
  const rgba = Buffer.alloc(size * size * 4);
  const ss = size >= 64 ? 3 : 4; // supersampling
  const scale = 64 / size;
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    let r = 0, g = 0, b = 0, a = 0;
    for (let sy = 0; sy < ss; sy++) for (let sx = 0; sx < ss; sx++) {
      const px = (x + (sx + 0.5) / ss) * scale, py = (y + (sy + 0.5) / ss) * scale;
      const d = roundRectInside(px, py);
      if (d > 0) continue;
      const t = Math.max(0, Math.min(1, (px + py) / 128));
      let [cr, cg, cb] = grad(t);
      const dist = Math.min(...bars.map((s) => segDist(px, py, s)));
      const edge = Math.max(0.6, 1.2 * scale);
      const w = dist < barHalf - edge ? 1 : dist > barHalf + edge ? 0 : 1 - (dist - (barHalf - edge)) / (2 * edge);
      cr = lerp(cr, 255, w); cg = lerp(cg, 255, w); cb = lerp(cb, 255, w);
      r += cr; g += cg; b += cb; a += 1;
    }
    const n = ss * ss, i = (y * size + x) * 4;
    if (a === 0) continue;
    rgba[i] = r / a; rgba[i + 1] = g / a; rgba[i + 2] = b / a; rgba[i + 3] = Math.round((a / n) * 255);
  }
  return rgba;
}

const sizes = [16, 24, 32, 48, 64, 128, 256];
const pngs = sizes.map((s) => png(s, render(s)));
writeFileSync(join(out, "hashrelay.png"), pngs[sizes.indexOf(256)]);
writeFileSync(join(out, "hashrelay-48.png"), pngs[sizes.indexOf(48)]);

// ICO container with PNG-compressed entries
const header = Buffer.alloc(6); header.writeUInt16LE(0, 0); header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
const dir = [];
let offset = 6 + 16 * sizes.length;
sizes.forEach((s, i) => {
  const e = Buffer.alloc(16);
  e[0] = s === 256 ? 0 : s; e[1] = s === 256 ? 0 : s; e[2] = 0; e[3] = 0;
  e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6); e.writeUInt32LE(pngs[i].length, 8); e.writeUInt32LE(offset, 12);
  offset += pngs[i].length; dir.push(e);
});
writeFileSync(join(out, "hashrelay.ico"), Buffer.concat([header, ...dir, ...pngs]));
console.log(`wrote ${join(out, "hashrelay.ico")} (${sizes.join("/")}) and hashrelay.png`);
