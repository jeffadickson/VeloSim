const { chromium } = require("playwright");
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const root = path.resolve(process.argv[3]);
const types = { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json" };
const server = http.createServer((req, res) => {
  if (req.url === "/article") { res.setHeader("Content-Type", "text/html"); res.end('<iframe title="lesson" src="/" style="width:100%;height:1050px;border:0"></iframe>'); return; }
  const name = path.resolve(root, "." + new URL(req.url, "http://localhost").pathname);
  const file = name === root ? path.join(root, "index.html") : name;
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404).end(); return; }
  res.setHeader("Content-Type", types[path.extname(file)] || "application/octet-stream");
  fs.createReadStream(file).pipe(res);
});
(async () => {
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const browser = await chromium.launch({ executablePath: process.argv[2], headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1160, height: 1130 } });
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    await page.goto("http://127.0.0.1:" + server.address().port + "/article");
    const lesson = page.frameLocator("iframe");
    const voltage = lesson.getByTestId("voltage");
    await voltage.waitFor();
    assert.equal(await voltage.textContent(), "2.5 V");
    await lesson.getByRole("button", {name:"Run DC sweep"}).click();
    const plot = lesson.getByRole("img", {name:/DC sweep plot/});
    assert.equal((await plot.locator("polyline").getAttribute("points")).split(" ").length, 21);
    await lesson.getByRole("button", {name:"Select R2", exact:true}).click();
    await lesson.getByLabel("Value").fill("3000");
    await lesson.getByRole("button", {name:"Apply value"}).click();
    assert.equal(await voltage.textContent(), "3.75 V");
    assert.match(await lesson.locator("figcaption").textContent(), /Final 3.75 V/);
    await lesson.getByRole("button", {name:"Delete R2"}).click();
    assert.equal(await voltage.textContent(), "—");
    assert.equal(await plot.count(), 0);
    await lesson.getByRole("button", {name:"+ Resistor", exact:true}).click();
    await lesson.getByRole("button", {name:/Place Resistor/}).first().click();
    await lesson.getByRole("button", {name:/^R1 terminal 2 /}).click();
    await lesson.getByRole("button", {name:/^R3 terminal 1 /}).click();
    await lesson.getByRole("button", {name:/^R3 terminal 2 /}).click();
    await lesson.getByRole("button", {name:/^G1 terminal 1 /}).click();
    assert.equal(await voltage.textContent(), "2.5 V");
    await lesson.getByRole("button", {name:"Reset example"}).click();
    await lesson.locator("summary").filter({hasText:"Parts library"}).click();
    await lesson.getByLabel("Find a generic symbol").fill("MOSFET");
    await lesson.getByRole("button", {name:"NMOS", exact:true}).click();
    await lesson.getByRole("button", {name:/Place Nmos/}).first().click();
    assert.equal(await lesson.getByRole("button", {name:"M1 terminal 4 · Bulk", exact:true}).count(), 1);
    assert.equal(await lesson.getByText("Placement and wiring only").count(), 1);
    assert.equal(await voltage.textContent(), "—");
    await lesson.getByRole("button", {name:"Reset example"}).click();
    await lesson.locator("summary").filter({hasText:"Parts library"}).click();
    await page.screenshot({path:path.resolve(process.argv[4])});
    assert.deepEqual(errors, []);
    console.log("Embedded lesson: edit, plot, delete, place, reconnect and reset passed.");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; }).finally(() => server.close());