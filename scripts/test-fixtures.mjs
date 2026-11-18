import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFile, readdir, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
const prepare = process.argv.includes("--prepare");
const fixtureRoot = path.resolve(process.argv.slice(2).find(arg => arg !== "--prepare") ?? fileURLToPath(new URL("../fixtures", import.meta.url)));
const provider = JSON.parse(await readFile(new URL("../toolchains/node-typescript.v1.json", import.meta.url), "utf8"));
const resources = [...new Set(provider.recipes.flatMap(recipe => recipe.certificationFixtures.map(fixture => fixture.resource)))];
if (!resources.length) throw new Error("The provider needs at least one certification fixture");
const fixtures = resources.map(resource => {
  if (!resource.startsWith("fixtures/")) throw new Error("Fixture must be a packaged resource: " + resource);
  const directory = path.resolve(fixtureRoot, resource.slice("fixtures/".length));
  const relative = path.relative(fixtureRoot, directory);
  if (!relative || relative === ".." || relative.startsWith(".." + path.sep) || path.isAbsolute(relative))
    throw new Error("Fixture directory must be inside the requested root: " + resource);
  return directory;
});
function command(cwd, ...args) {
  const result = spawnSync("npm", args, { cwd, stdio: "inherit", env: process.env });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`npm ${args.join(" ")} failed (${result.status})`);
}
async function manifest(root, relative = "") {
  const results = [];
  for (const item of (await readdir(path.join(root, relative), { withFileTypes: true })).sort((a,b) => a.name.localeCompare(b.name))) {
    const name = path.posix.join(relative, item.name);
    if (item.isSymbolicLink()) throw new Error("Fixture output cannot contain symbolic links");
    if (item.isDirectory()) results.push(...await manifest(root, name));
    else results.push([name, createHash("sha256").update(await readFile(path.join(root, name))).digest("hex")]);
  }
  return results;
}
if (prepare) {
  for (const cwd of fixtures) command(cwd, "ci", "--ignore-scripts", "--no-audit", "--fund=false");
  command(fixtures[0], "exec", "--offline", "--", "playwright", "install", "--with-deps", "chromium");
  process.exit(0);
}
for (const cwd of fixtures) {
  const recipe = path.basename(cwd);
  const runs = [];
  for (const pass of [1, 2]) {
    await rm(path.join(cwd, "dist"), { recursive: true, force: true });
    await rm(path.join(cwd, "test-results"), { recursive: true, force: true });
    command(cwd, "ci", "--offline", "--ignore-scripts", "--no-audit", "--fund=false");
    for (const step of ["lint", "test", "build", "smoke"]) command(cwd, "run", step);
    runs.push(JSON.stringify([await manifest(path.join(cwd, "dist")), await manifest(path.join(cwd, "test-results"))]));
    console.log(`${recipe}: offline fixture pass ${pass} completed`);
  }
  if (runs[0] !== runs[1]) throw new Error(`${recipe}: output or capture differs between clean passes`);
  console.log(`${recipe}: reproducible output and capture verified`);
}
