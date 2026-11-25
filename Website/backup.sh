#!/usr/bin/env bash
set -euo pipefail
docker exec craftpilot-site node --input-type=module -e '
import { DatabaseSync } from "node:sqlite";
import { mkdirSync, readdirSync, statSync, unlinkSync, existsSync, copyFileSync, chmodSync } from "node:fs";
import path from "node:path";
const folder = "/app/data/backups";
mkdirSync(folder, {recursive:true, mode:0o700});
const stamp = new Date().toISOString().replace(/[^0-9]/g, "");
const target = path.join(folder, stamp + ".sqlite");
const db = new DatabaseSync("/app/data/community.sqlite");
db.exec("PRAGMA busy_timeout=10000");
db.exec("VACUUM INTO " + "\x27" + target + "\x27");
db.close();
if (existsSync("/app/data/mail-key")) { copyFileSync("/app/data/mail-key", path.join(folder,"mail-key")); chmodSync(path.join(folder,"mail-key"),0o600); }
for (const name of readdirSync(folder)) {
  if (!/^[0-9]+\.sqlite$/.test(name)) continue;
  const file = path.join(folder, name);
  if (Date.now() - statSync(file).mtimeMs > 14 * 86400000) unlinkSync(file);
}
console.log("Database backup created.");
'
