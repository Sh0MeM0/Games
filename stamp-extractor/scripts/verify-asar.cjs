#!/usr/bin/env node
// Used by verify-portable.py. Verifies package structure and actual source bytes;
// this is deliberately not a claim that the Windows executable was launched.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const asar = require('@electron/asar');

function check(condition, message) { if (!condition) throw new Error(message); }
function filesBelow(root, folder) {
  const output = [];
  for (const entry of fs.readdirSync(path.join(root, folder), { withFileTypes: true })) {
    const relative = path.posix.join(folder, entry.name);
    if (entry.isDirectory()) output.push(...filesBelow(root, relative));
    else if (entry.isFile()) output.push(relative);
  }
  return output;
}

try {
  const [, , archiveArgument, projectArgument, ...flags] = process.argv;
  check(archiveArgument && projectArgument, 'Usage: node scripts/verify-asar.cjs APP.ASAR PROJECT_ROOT [--skip-source]');
  const archive = path.resolve(archiveArgument), project = path.resolve(projectArgument);
  const entries = asar.listPackage(archive).map(value => value.replaceAll('\\', '/').replace(/^\//, ''));
  const packaged = JSON.parse(asar.extractFile(archive, 'package.json').toString('utf8'));
  const forbidden = ['tests', 'test-results', 'examples', 'scripts', 'release', '.env', '.git'];
  for (const entry of entries) {
    check(!forbidden.some(prefix => entry === prefix || entry.startsWith(`${prefix}/`)), `Development/private path found in ASAR: ${entry}`);
    check(!/^node_modules\/(electron|electron-builder|playwright|playwright-core)(\/|$)/.test(entry), `Development tool found in ASAR: ${entry}`);
  }
  const matched = [];
  if (!flags.includes('--skip-source')) {
    const current = JSON.parse(fs.readFileSync(path.join(project, 'package.json'), 'utf8'));
    check(packaged.version === current.version, `Packaged version ${packaged.version} differs from source version ${current.version}`);
    const expected = ['main.cjs', 'preload.cjs', 'README.md', 'LICENSE', ...filesBelow(project, 'lib'), ...filesBelow(project, 'renderer')].sort();
    const sourceSet = new Set(expected);
    for (const name of expected) {
      const original = fs.readFileSync(path.join(project, name));
      const shipped = asar.extractFile(archive, name);
      check(original.equals(shipped), `Packaged source differs from current checkout: ${name}`);
      matched.push({ file: name, sha256: crypto.createHash('sha256').update(shipped).digest('hex') });
    }
    for (const entry of entries) if (/^(lib|renderer)\//.test(entry) && !asar.statFile(archive, entry).files) {
      check(sourceSet.has(entry), `Stale source file found in ASAR: ${entry}`);
    }
  }
  process.stdout.write(`${JSON.stringify({ version: packaged.version, archive_entries: entries.length, no_development_paths: true, source_comparison: flags.includes('--skip-source') ? 'skipped' : 'passed', matched_source_files: matched })}\n`);
} catch (error) {
  process.stderr.write(`ASAR verification failed: ${error.message}\n`);
  process.exitCode = 1;
}
