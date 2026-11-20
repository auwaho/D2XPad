// Run with "node tests/Test-LiveBrowser.cjs". Exercises the actual embedded page.
const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");

const html = readFileSync(path.join(__dirname, "../src/D2XPad.Live/live-gamepad.html"), "utf8");
const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];

function page(transport) {
  const elements = new Map(), statusHistory = [], delays = [];
  function element(id) {
    if (!elements.has(id)) {
      let text = "";
      elements.set(id, {
        style: {},
        get textContent() { return text; },
        set textContent(value) { text = value; if (id === "status") statusHistory.push(value); }
      });
    }
    return elements.get(id);
  }
  let snapshots = 0;
  const context = vm.createContext({
    window: {isSecureContext:true}, document: {getElementById:element,visibilityState:"visible",hasFocus:() => true},
    navigator: {userAgent:"test",getGamepads:() => []}, performance: {now:() => ++snapshots},
    fetch:transport, AbortSignal, requestAnimationFrame() {},
    setTimeout(callback, ms) { delays.push(ms); queueMicrotask(callback); }
  });
  // Observe the completion of the single poll loop started by the page.
  vm.runInContext(script, context);
  return { element, statusHistory, delays, get snapshots() { return snapshots; } };
}

const success = body => ({ok:true,json:async () => body});
const state = (generation, prompt, finished=false) => ({generation,prompt,finished,selectedIndex:null,selectedId:null});
const flush = () => new Promise(resolve => setImmediate(resolve));

test("recovers from a failed GET and lost sample acknowledgement without resampling", async () => {
  let gets = 0, finished = 0;
  const posts = [];
  const view = page(async (url, options) => {
    if (url.startsWith("next?")) {
      if (++gets === 1) throw new TypeError("Failed to fetch");
      if (gets === 2) return success(state(8, "Hold LT."));
      if (gets === 3) return success(state(9, "Hold RT."));
      return success(state(0, "Completed 2/2 checks.", true));
    }
    if (url === "samples") {
      posts.push(options.body);
      if (posts.length === 1) throw new TypeError("Acknowledgement lost after delivery");
    } else if (url === "finished") finished++;
    else throw new Error("Unexpected URL " + url);
    return success();
  });
  await flush();
  assert.equal(finished, 1);
  assert.equal(gets, 4);
  assert.equal(posts.length, 3);
  assert.equal(posts[0], posts[1]);
  assert.deepEqual(posts.map(text => JSON.parse(text).generation), [8,8,9]);
  assert.equal(view.snapshots, 13); // Initial display plus six frames for each of two generations.
  assert.equal(view.statusHistory.filter(s => s.includes("Reconnecting")).length, 2);
  assert.equal(view.element("status").textContent, "");
  assert.equal(view.element("prompt").textContent, "Completed 2/2 checks.");
  await flush();
  assert.equal(gets, 4); // No further polling after completion.
});

test("bounds retries if the session closes before returning its final state", async () => {
  let attempts = 0;
  const view = page(async () => { attempts++; throw new TypeError("Failed to fetch"); });
  await flush();
  assert.equal(attempts, 12);
  assert.ok(Math.max(...view.delays) <= 1500);
  assert.match(view.element("status").textContent, /unreachable/);
});
