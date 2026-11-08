const { chromium } = require("playwright");
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const root = path.resolve(process.argv[3]);
const rcHost = process.argv[5];
const types = { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json" };
const server = http.createServer((req, res) => {
  if (req.url === "/article") { res.setHeader("Content-Type", "text/html"); res.end(`<iframe title="lesson" src="${rcHost || "/"}" style="width:100%;height:1320px;border:0"></iframe>`); return; }
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
    const page = await browser.newPage({ viewport: { width: 1160, height: 1390 } });
    const errors = [];
    page.on("pageerror", error => { errors.push(error.message); console.error(error.message); });
    page.on("console", message => { if (message.type() === "error") console.error(message.text()); });
    page.on("response", response => { if (response.status() >= 400) console.error(response.status(), response.url()); });
    await page.goto("http://127.0.0.1:" + server.address().port + "/article");
    const lesson = page.frameLocator("iframe");
    const voltage = lesson.getByTestId("voltage");
    await voltage.waitFor();
    assert.equal(await voltage.textContent(), "2.5 V");
    await lesson.getByRole("button", {name:"Run DC sweep"}).click();
    const plot = lesson.getByRole("img", {name:/DC sweep plot/});
    assert.equal((await plot.locator("polyline").getAttribute("points")).split(" ").length, 21);
    await lesson.getByRole("button", {name:"Select R2", exact:true}).press("Enter");
    await lesson.getByLabel("Value").fill("3000");
    await lesson.getByRole("button", {name:"Apply value"}).click();
    assert.equal(await voltage.textContent(), "3.75 V");
    assert.match(await lesson.locator("figcaption").textContent(), /Final 2.5 V/);
    assert.equal(await lesson.getByText(/The circuit changed/).count(), 1);
    await lesson.getByRole("button", {name:"Run DC sweep"}).click();
    assert.match(await lesson.locator("figcaption").textContent(), /Final 3.75 V/);

    // Keep the R2 draft open while a wire edit advances the document revision.
    await lesson.getByLabel("Value").fill("9000");
    await lesson.getByRole("button", {name:/^V1 terminal 1 /}).click();
    await lesson.getByRole("button", {name:/^G1 terminal 1 /}).click();
    await lesson.getByRole("button", {name:"Apply value"}).click();
    assert.equal(await lesson.getByText(/The document changed/).count(), 1);
    assert.equal(await lesson.getByText("R2 3 kΩ", {exact:true}).count(), 1);
    assert.match(await lesson.locator("figcaption").textContent(), /Final 3.75 V/);
    await lesson.getByRole("button", {name:"Reset example"}).click();
    await lesson.getByRole("button", {name:"Select R2", exact:true}).click();
    await lesson.getByRole("button", {name:"Delete R2"}).click();
    assert.equal(await voltage.textContent(), "—");
    await lesson.getByRole("button", {name:"Run DC sweep"}).click();
    assert.equal(await plot.count(), 0);
    await lesson.getByRole("button", {name:"+ Resistor", exact:true}).click();
    await lesson.getByRole("button", {name:/Place Resistor/}).first().click();
    await lesson.getByRole("button", {name:/^R1 terminal 2 /}).click();
    await lesson.getByRole("button", {name:/^R3 terminal 1 /}).click();
    await lesson.getByRole("button", {name:/^R3 terminal 2 /}).click();
    await lesson.getByRole("button", {name:/^G1 terminal 1 /}).click();
    assert.equal(await voltage.textContent(), "2.5 V");
    await lesson.getByRole("button", {name:"Reset example"}).click();
    await lesson.getByRole("button", {name:"Parts library", exact:true}).click();
    await lesson.getByLabel("Find a generic symbol").fill("MOSFET");
    await lesson.getByRole("button", {name:"NMOS", exact:true}).click();
    await lesson.getByRole("button", {name:/Place Nmos/}).first().click();
    assert.equal(await lesson.getByRole("button", {name:"M1 terminal 4 · Bulk", exact:true}).count(), 1);
    assert.equal(await lesson.getByText("Placement and wiring only").count(), 1);
    assert.equal(await voltage.textContent(), "—");
    await lesson.getByRole("button", {name:"Reset example"}).click();
    await lesson.getByRole("button", {name:"Parts library", exact:true}).click();
    if (rcHost) {
      await lesson.getByRole("button", {name:"Open RC example"}).click();
      await lesson.getByRole("button", {name:"Select P1", exact:true}).press("Enter");
      await lesson.getByRole("button", {name:"Run RC transient"}).click();
      const rc = lesson.getByRole("img", {name:"RC probe voltage against time"});
      await rc.waitFor();
      const points = (await rc.locator("polyline").getAttribute("points")).split(" ")
        .map(point => point.split(",").map(Number));
      assert.ok(points.length > 400);
      // Recover SI coordinates from the SVG scales of the default 5 V, 1 ms circuit.
      const atTau = points.reduce((a,b) => Math.abs(b[0] - 146.67) < Math.abs(a[0] - 146.67) ? b : a);
      assert.ok(Math.abs((174 - atTau[1]) * 5 / 149 - 5 * (1 - Math.exp(-1))) < 0.04);
      assert.match(await lesson.getByTestId("rc-final").textContent(), /4\.96\d\d V/);
      console.log("Native browser RC probe plotted the expected charging waveform.");
    }
    await page.screenshot({path:path.resolve(process.argv[4])});
    assert.deepEqual(errors, []);
    console.log("Embedded lesson: edit, plot, reject stale draft, delete, place, reconnect and reset passed.");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; }).finally(() => server.close());