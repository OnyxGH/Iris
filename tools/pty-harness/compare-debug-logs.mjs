import fs from "node:fs";
const load = (f) => {
  const t = fs.readFileSync(f, "utf8"); const out = []; let inR = false;
  for (const l of t.split("\n")) {
    if (l.startsWith("=== All")) { inR = true; continue; }
    if (l.startsWith("=== Agent")) break;
    const m = l.match(/^\[(\d+)\] \(w=(\d+)\) (.*)$/);
    if (inR && m) out.push(JSON.parse(m[3]));
  }
  return out;
};
const a = load(process.argv[2]), b = load(process.argv[3]);
const vis = (s) => s.replace(/\x1b/g, "⎋");
console.log(`pi lines=${a.length} pisharp lines=${b.length}`);
// align from the "run it" user message
const ia = a.findIndex((l) => l.includes("run it")), ib = b.findIndex((l) => l.includes("run it"));
let diffs = 0;
for (let k = 0; k < Math.max(a.length - ia, b.length - ib); k++) {
  const x = a[ia + k] ?? "<none>", y = b[ib + k] ?? "<none>";
  if (x !== y) { diffs++; if (diffs <= 12) console.log(`#${k}\n  pi : ${vis(x)}\n  cs : ${vis(y)}`); }
}
console.log("diff lines:", diffs);
