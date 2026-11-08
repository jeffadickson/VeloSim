const { chromium } = require("playwright");
const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const root = path.resolve(process.argv[3]);
const rcHost = process.argv[5];
const types = { ".html": "text/html", ".css": "text/css", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json" };
const server = http.createServer((req, res) => {
  if (req.url === "/article") { res.setHeader("Content-Type", "text/html"); res.end(`<iframe title="lesson" src="${rcHost || "/"}" style="width:100%;height:1770px;border:0"></iframe>`); return; }
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
    const page = await browser.newPage({ viewport: { width: 1160, height: 1800 } });
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
    await lesson.getByRole("button", {name:"Select R1", exact:true}).press("Enter");
    await lesson.getByRole("button", {name:"Move part", exact:true}).click();
    await lesson.getByRole("button", {name:"Destination 200, 70", exact:true}).press("Enter");
    const moved = lesson.getByRole("button", {name:"Select R1", exact:true}).locator("..");
    assert.equal(await moved.getAttribute("transform"), "translate(200 70)");
    await lesson.getByRole("button", {name:"Rotate clockwise"}).click();
    assert.match(await moved.locator(".art").getAttribute("transform"), /rotate\(90\)/);
    assert.equal(await voltage.textContent(), "2.5 V");
    await lesson.getByRole("button", {name:"Undo", exact:true}).click();
    assert.match(await moved.locator(".art").getAttribute("transform"), /rotate\(0\)/);
    await lesson.getByRole("button", {name:"Redo", exact:true}).click();
    assert.match(await moved.locator(".art").getAttribute("transform"), /rotate\(90\)/);
    await lesson.getByRole("button", {name:"Select G1", exact:true}).click();
    await lesson.getByRole("button", {name:"Delete G1", exact:true}).click();
    await lesson.getByRole("button", {name:"Check circuit"}).click();
    const findings = lesson.getByRole("region", {name:"Circuit findings"});
    assert.match(await findings.textContent(), /Place ground/);
    await findings.getByRole("button", {name:"Highlight V1 pin 2", exact:true}).click();
    assert.match(await lesson.getByRole("button", {name:/^V1 terminal 2 /}).getAttribute("class"), /pending/);
    await lesson.getByRole("button", {name:"Undo", exact:true}).click();
    await lesson.getByRole("button", {name:"Check circuit"}).click();
    assert.match(await findings.textContent(), /No circuit findings/);
    console.log("Browser geometry, history and actionable preflight passed.");

    const button = name => lesson.getByRole("button", {name, exact:true});
    const select = (name, value) => lesson.getByLabel(name, {exact:true}).selectOption(value);
    const bends = lesson.getByLabel("Bends or junction point (x,y)", {exact:true});
    await button("Reset example").click();
    await select("Wire", "V1:0|R1:0");
    await bends.fill("250,145");
    await button("Apply route").click();
    await select("Wire", "V1:1|G1:0");
    await bends.fill("250,145");
    await button("Apply route").click();
    assert.equal(await voltage.textContent(), "2.5 V");
    await select("Second wire for junction", "V1:0|R1:0");
    await button("Join wires at point").click();
    assert.equal(await voltage.textContent(), "—");
    await button("Save sheet").click();
    await button("Reset example").click();
    await button("Reload sheet").click();
    await lesson.getByRole("button", {name:/Delete junction/}).waitFor();
    assert.equal(await voltage.textContent(), "—");
    await lesson.getByRole("button", {name:/Delete junction/}).click();
    assert.equal(await voltage.textContent(), "2.5 V");
    console.log("Browser crossing, junction and junction round trip passed.");

    if (rcHost) {
      await lesson.getByRole("button", {name:"Open RC example"}).click();
      await select("Net terminal", "R1:1");
      await lesson.getByLabel("Net name", {exact:true}).fill("output");
      await button("Apply net label").click();
      await select("Net terminal", "P1:0");
      await lesson.getByLabel("Net name", {exact:true}).fill("OUTPUT");
      await button("Apply net label").click();
      await select("Wire", "R1:1|P1:0");
      await button("Delete wire").click();
      await button("Save sheet").click();
      await page.reload();
      await button("Reload sheet").click();
      await lesson.locator("svg .net-name").first().waitFor();
      assert.deepEqual((await lesson.locator("svg .net-name").allTextContents()).sort(), ["OUTPUT","output"]);

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
      await select("Net terminal", "P1:0");
      await button("Highlight net").click();
      assert.equal(await lesson.locator("svg .pin.net-highlight").count(), 3);
      assert.equal(await lesson.locator("svg .wire.net-highlight").count(), 1);
      await button("Parts library").click();
      await lesson.getByLabel("Find a generic symbol").fill("resistor");
      await button("Save sheet").click();
      await page.reload();
      await button("Reload sheet").click();
      await rc.waitFor();
      assert.equal(await lesson.locator("svg .pin.net-highlight").count(), 3);
      assert.equal(await lesson.locator("svg .wire.net-highlight").count(), 1);
      assert.equal(await lesson.locator("svg .net-name.net-highlight").count(), 2);
      assert.equal(await lesson.getByLabel("Find a generic symbol").inputValue(), "resistor");
      assert.equal(await button("Run RC transient").count(), 1);
      assert.equal(await lesson.getByLabel("Analysis history").locator("option").count(), 2);
      assert.deepEqual((await rc.locator("polyline").getAttribute("points")).split(" ")
        .map(point => point.split(",").map(Number)), points);
      assert.match(await voltage.textContent(), /4\.966 V/);
      await page.screenshot({path:path.resolve(process.argv[4])});
      console.log("Browser restored the highlighted RC net, view and exact native waveform without rerunning.");
      await button("Select C1").click();
      await button("Delete C1").click();
      await lesson.getByLabel("Find a generic symbol").fill("polarized");
      await button("Polarized capacitor").click();
      await button("Place PolarizedCapacitor at 320, 250").click();
      await lesson.getByRole("button", {name:/^C2 terminal 1 /}).click();
      await lesson.getByRole("button", {name:/^R1 terminal 2 /}).click();
      await lesson.getByRole("button", {name:/^C2 terminal 2 /}).click();
      await lesson.getByRole("button", {name:/^G1 terminal 1 /}).click();
      await button("Select P1").click();
      await button("Run RC transient").click();
      await lesson.getByText("RC transient completed.", {exact:true}).waitFor();
      assert.deepEqual((await rc.locator("polyline").getAttribute("points")).split(" ")
        .map(point => point.split(",").map(Number)), points);
      const polarScreenshot = path.parse(path.resolve(process.argv[4]));
      await page.screenshot({path:path.join(polarScreenshot.dir, polarScreenshot.name + "-polarized.png")});
      console.log("Browser verified the forward polarized capacitor with the native solver.");
      await button("Open mixed RLC example").click();
      const analysis = lesson.getByRole("region", {name:"General circuit analysis"});
      const samples = analysis.getByRole("table", {name:"Probe samples"});
      for (const [kind, count] of [["Dc", 1], ["Ac", 2], ["Transient", 1500]]) {
        await analysis.getByRole("button", {name:"Analyze " + kind, exact:true}).click();
        await analysis.getByTestId("bridge-count").filter({hasText:kind}).waitFor();
        assert.ok(Number((await analysis.getByTestId("bridge-count").textContent()).split(" ")[0]) >= count);
        if (kind === "Ac") {
          const cells = await samples.locator("tbody tr").first().locator("td").allTextContents();
          assert.equal(Number(cells[0]), 100);
          assert.notEqual(Number(cells[2]), 0);
        }
      }
      const bridgeScreenshot = path.parse(path.resolve(process.argv[4]));
      await analysis.scrollIntoViewIfNeeded();
      await page.screenshot({path:path.join(bridgeScreenshot.dir, bridgeScreenshot.name + "-bridge.png")});
      console.log("Browser ran mixed RLC and controlled sources in DC, AC and transient.");
      await button("Open semiconductor example").click();
      for (const probe of ["P1", "P2"]) {
        await analysis.getByLabel("Probe ID", {exact:true}).fill(probe);
        for (const kind of ["Dc", "Ac", "Transient"]) {
          await analysis.getByRole("button", {name:"Analyze " + kind, exact:true}).click();
          await analysis.getByTestId("bridge-count").filter({hasText:probe + " · " + kind}).waitFor();
        }
      }
      await page.screenshot({path:path.join(bridgeScreenshot.dir, bridgeScreenshot.name + "-models.png")});
      console.log("Browser ran the diode and MOS1 channel in all three analyses.");
    }
    if (!rcHost) await page.screenshot({path:path.resolve(process.argv[4])});
    assert.deepEqual(errors, []);
    console.log("Embedded lesson: edit, plot, reject stale draft, delete, place, reconnect and reset passed.");
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode=1; }).finally(() => server.close());