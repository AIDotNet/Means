import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, renameSync, rmSync, statSync } from "node:fs";
import { resolve, join } from "node:path";

const packageDir = resolve(process.argv[2] ?? ".");

rmSync(join(packageDir, "dist"), { recursive: true, force: true });

run("tsc", ["-p", "tsconfig.esm.json"]);
run("tsc", ["-p", "tsconfig.cjs.json"]);
run("tsc", ["-p", "tsconfig.types.json"]);
renameJsToMjs(join(packageDir, "dist", "esm"));

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: packageDir,
    stdio: "inherit",
    shell: process.platform === "win32"
  });

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

function renameJsToMjs(directory) {
  if (!existsSync(directory)) {
    return;
  }

  for (const entry of readdirSync(directory)) {
    const path = join(directory, entry);
    if (statSync(path).isDirectory()) {
      renameJsToMjs(path);
      continue;
    }

    if (entry.endsWith(".js")) {
      renameSync(path, path.slice(0, -3) + ".mjs");
    }
  }
}

