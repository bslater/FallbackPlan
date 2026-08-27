// The setup wizard's end-to-end drill: drives a real browser through the
// whole first-run ceremony — invalid inputs first, so every gate is proven
// closed before it is proven open.
//
// What it verifies, in order:
//   step 2  a short passphrase, a long-but-lowercase one, and a mismatched
//           confirmation each leave "Build the recovery kit" disabled; a
//           compliant, matching pair enables it and the kit arrives.
//   step 3  the acknowledgement checkbox is locked until a kit form is
//           taken; "Finish setup" is locked until it is ticked.
//   step 4  a short password, one missing the composition, one equal to the
//           passphrase, and a mismatched confirmation each leave "Create
//           User" disabled; a valid set enables it, the account is created,
//           and the console lands signed in as the new owner.
//
// This is a manual/e2e drill, deliberately not wired into CI: it needs a
// Chromium and a display-less environment. To run it:
//
//   1. npm install playwright-core        (anywhere on NODE_PATH)
//   2. build and start a FRESH installation (empty state dir):
//        fallbackplan-agent            (or with FALLBACKPLAN_STATE pointing at scratch)
//        fallbackplan-web --port 5099
//   3. node eng/wizard-e2e.mjs "<the tokenised console URL>" [chromium-path]
//
// Exit code 0 means every gate held; anything else names the step that did
// not. The structural half of these guarantees lives in
// tests/FallbackPlan.Web.Tests/SetupWizardScriptTests.cs, which runs in CI.

import { createRequire } from "node:module";
const require = createRequire(import.meta.url);
const { chromium } = require("playwright-core");

const url = process.argv[2];
const executablePath = process.argv[3]
  ?? process.env.WIZARD_CHROMIUM
  ?? "/opt/pw-browsers/chromium-1194/chrome-linux/chrome";
if (!url) {
  console.error("usage: node eng/wizard-e2e.mjs <console-url-with-token> [chromium-path]");
  process.exit(2);
}

const PASSPHRASE = "A drill Passphrase 42! long enough";
const PASSWORD = "Owner-Pass-19!";

let failures = 0;
function check(label, ok) {
  console.log(`${ok ? "  ok " : "FAIL "} ${label}`);
  if (!ok) failures++;
}

const browser = await chromium.launch({ executablePath, headless: true });
try {
  const context = await browser.newContext();
  await context.addInitScript(() => { window.print = () => {}; });
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", e => errors.push(e.message));

  const disabled = selector => page.$eval(selector, el => el.disabled);
  const type = async (selector, text) => {
    await page.fill(selector, text);
    await page.waitForTimeout(600); // past the 250ms debounce + round trip
  };
  const closeDialog = async () => {
    const close = await page.$('#dialog[open] [data-action="close-dialog"]');
    if (close) await close.click();
  };

  await page.goto(url, { waitUntil: "networkidle" });

  // ---- step 1: the acknowledgement
  check("intro: begin disabled before the acknowledgement",
    await disabled('[data-action="setup-begin"]'));
  await page.check('#setup-ack');
  await page.click('[data-action="setup-begin"]');
  await page.waitForSelector("#setup-confirm");

  // ---- step 2: passphrase + confirmation, invalid shapes first
  const build = '[data-action="setup-finish"]';
  await type("#setup-pass", "short");
  check("passphrase: a short entry leaves Build disabled", await disabled(build));

  await type("#setup-pass", "twenty lowercase characters here");
  check("passphrase: long but composition-failing leaves Build disabled", await disabled(build));

  await type("#setup-pass", PASSPHRASE);
  check("passphrase: compliant but unconfirmed leaves Build disabled", await disabled(build));

  await type("#setup-confirm", PASSPHRASE + "x");
  check("passphrase: a mismatched confirmation leaves Build disabled", await disabled(build));

  await type("#setup-confirm", PASSPHRASE);
  check("passphrase: compliant and matching enables Build", !await disabled(build));

  await page.click(build);
  await page.waitForSelector('[data-action="setup-kit-file"]', { timeout: 30000 });
  await closeDialog();

  // ---- step 3: the kit gate
  check("kit: the acknowledgement is locked before a form is taken",
    await disabled("#setup-kit-ack"));
  check("kit: Finish is locked before the acknowledgement",
    await disabled('[data-action="setup-kit-done"]'));

  const download = page.waitForEvent("download", { timeout: 10000 });
  await page.click('[data-action="setup-kit-file"]');
  const file = await download;
  check("kit: the download is the framed kit file",
    file.suggestedFilename() === "fallbackplan-recovery-kit.fbpkrkit");

  check("kit: taking a form unlocks the acknowledgement", !await disabled("#setup-kit-ack"));
  check("kit: Finish stays locked until the box is ticked",
    await disabled('[data-action="setup-kit-done"]'));
  await page.check("#setup-kit-ack");
  check("kit: the ticked box enables Finish", !await disabled('[data-action="setup-kit-done"]'));

  await page.click('[data-action="setup-kit-done"]');
  await page.waitForSelector("#setup-user", { timeout: 20000 });

  // ---- step 4: the first account, invalid shapes first
  const create = '[data-action="setup-create-user"]';
  await page.fill("#setup-user", "ben");

  await type("#setup-user-pass", "Aa1!x");
  check("account: a short password leaves Create User disabled", await disabled(create));

  await type("#setup-user-pass", "long-enough-42!");
  check("account: a composition-failing password leaves Create User disabled", await disabled(create));

  await type("#setup-user-pass", PASSPHRASE);
  await type("#setup-user-confirm", PASSPHRASE);
  check("account: the installation passphrase is refused as the password", await disabled(create));
  check("account: the refusal names the rule", (await page.$eval(
    "#setup-account-rules", el => el.textContent)).includes("must not be the installation passphrase"));

  await type("#setup-user-pass", PASSWORD);
  check("account: a mismatched confirmation leaves Create User disabled", await disabled(create));

  await type("#setup-user-confirm", PASSWORD);
  check("account: valid entries enable Create User", !await disabled(create));

  await page.click(create);
  await page.waitForFunction(() => !document.getElementById("app").hidden, { timeout: 20000 });
  await closeDialog();

  const signedIn = await page.$eval("#signed-in", el => !el.hidden && el.textContent.trim());
  check("finish: the console is signed in as the new owner", signedIn === "ben");
  check("finish: no page errors along the way", errors.length === 0);
  if (errors.length) console.error(errors.join("\n"));
} finally {
  await browser.close();
}

console.log(failures === 0 ? "wizard drill: every gate held" : `wizard drill: ${failures} gate(s) FAILED`);
process.exit(failures === 0 ? 0 : 1);
