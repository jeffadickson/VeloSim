// Exercise the real hosted profile inside a presentation-like iframe. No mock
// numerical solver: the ASP.NET host must have its packaged executable available.
const { chromium } = require("playwright");
const assert = require("node:assert/strict");
const http = require("node:http");
const server = http.createServer((req, res) => {
  res.setHeader("Content-Type", "text/html");
  res.end(`<h1>Live RC lesson</h1><iframe title="Circuit lesson" src="${process.argv[3]}" style="width:100%;height:1800px;border:0"></iframe>`);
});
(async () => {
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const browser = await chromium.launch({executablePath:process.argv[2],headless:true});
  try {
    const page = await browser.newPage({viewport:{width:1200,height:1500}});
    const errors = []; page.on("pageerror", e => errors.push(e.message));
    await page.goto(`http://127.0.0.1:${server.address().port}/`);
    const lesson = page.frameLocator("iframe");
    const lock = lesson.getByRole("checkbox", {name:"Lock layout"});
    await lock.waitFor({timeout:60000});
    assert.equal(await lock.isChecked(),true);
    assert.equal(await lock.isDisabled(),true);
    const resistor = lesson.getByRole("button",{name:"Select R1",exact:true});
    const before = await resistor.locator("..").getAttribute("transform");
    await lesson.getByRole("button",{name:"Reset example",exact:true}).click();
    await lesson.getByText(/author has locked/).waitFor();
    await resistor.click();
    await lesson.getByRole("spinbutton",{name:/^Value \(/}).fill("2000");
    await lesson.getByRole("button",{name:"Apply value",exact:true}).click();
    assert.equal(await resistor.locator("..").getAttribute("transform"),before);
    await lesson.getByRole("button",{name:"Select P1",exact:true}).click();
    await lesson.getByRole("button",{name:"Run RC transient",exact:true}).click();
    const plot=lesson.getByRole("img",{name:"RC probe voltage against time"});
    await plot.waitFor({timeout:60000});
    assert.ok((await plot.locator("polyline").getAttribute("points")).split(" ").length > 2);
    assert.deepEqual(errors,[]);
    await page.screenshot({path:process.argv[4],fullPage:true});
    await plot.screenshot({path:process.argv[4].replace(/\.png$/, "-waveform.png")});
    console.log("Embedded author-locked RC: reset refused, value changed, geometry preserved, real waveform rendered.");
  } finally { await browser.close(); }
})().catch(error => {console.error(error);process.exitCode=1;}).finally(()=>server.close());
