import { chromium } from "playwright";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const mockups = path.join(root, "mockups");
const imgOut = path.join(root, "static", "img");
const videoOut = path.join(root, "static", "video");

await mkdir(imgOut, { recursive: true });
await mkdir(videoOut, { recursive: true });

const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_PATH || "/usr/bin/google-chrome-stable",
  args: ["--font-render-hinting=none"],
});

async function shot(file, name, { width, height, scene, scale = 2 }) {
  const page = await browser.newPage({
    viewport: { width, height },
    deviceScaleFactor: scale,
  });
  const url = pathToFileURL(path.join(mockups, file)).href + (scene ? `?v=${scene}` : "");
  await page.goto(url, { waitUntil: "networkidle" });
  if (scene) {
    await page.evaluate((s) => { document.body.dataset.scene = s; }, scene);
  }
  await page.waitForTimeout(120);
  await page.screenshot({
    path: path.join(imgOut, name),
    type: "png",
  });
  await page.close();
  console.log("wrote", name);
}

await shot("app.html", "ui-chat.png", { width: 1440, height: 900, scene: "chat" });
await shot("app.html", "ui-agents.png", { width: 1440, height: 900, scene: "agents" });
await shot("app.html", "ui-terminal.png", { width: 1440, height: 900, scene: "terminal" });
await shot("app.html", "ui-settings.png", { width: 1440, height: 900, scene: "settings" });
await shot("app.html", "ui-cli.png", { width: 1440, height: 900, scene: "cli" });
await shot("og.html", "og.png", { width: 1200, height: 630, scale: 2 });
await shot("og.html", "twitter.png", { width: 1200, height: 630, scale: 1 });

const context = await browser.newContext({
  viewport: { width: 1280, height: 720 },
  deviceScaleFactor: 1,
  recordVideo: { dir: videoOut, size: { width: 1280, height: 720 } },
});
const page = await context.newPage();
await page.goto(pathToFileURL(path.join(mockups, "demo.html")).href, { waitUntil: "networkidle" });
await page.waitForTimeout(11000);
await page.screenshot({ path: path.join(imgOut, "demo-poster.png"), type: "png" });
const video = page.video();
await page.close();
const rawPath = await video.path();
await context.close();
await browser.close();

const { spawnSync } = await import("node:child_process");
const mp4 = path.join(videoOut, "demo.mp4");
const webm = path.join(videoOut, "demo.webm");
const ffmpeg = spawnSync("ffmpeg", [
  "-y", "-i", rawPath,
  "-an",
  "-vf", "fps=30,format=yuv420p",
  "-c:v", "libx264", "-preset", "slow", "-crf", "22",
  "-movflags", "+faststart",
  mp4,
], { encoding: "utf8" });
if (ffmpeg.status !== 0) {
  console.error(ffmpeg.stderr);
  process.exit(ffmpeg.status ?? 1);
}
spawnSync("ffmpeg", ["-y", "-i", mp4, "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "32", "-an", webm], { encoding: "utf8" });
spawnSync("rm", ["-f", rawPath]);
console.log("wrote demo.mp4 / demo.webm");
