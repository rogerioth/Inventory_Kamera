'use strict';

const { readFileSync } = require('node:fs');
const { versionFromDistance } = require('./release-policy.cjs');
const commit = process.argv[2];
if (!/^[a-f0-9]{40}$/.test(commit || '')) throw new Error('Specify a full commit SHA.');
// Read the baseline from the release commit so later version-policy changes do
// not change the tag chosen by a rerun of an older release.
// Git itself is invoked with fixed commands in the trusted workflow, before
// this helper. This process only reads the two exported data files.
const config = JSON.parse(readFileSync(process.argv[4], 'utf8'));
const history = readFileSync(process.argv[3], 'utf8').trim().split(/\r?\n/);
const commitIndex = history.indexOf(commit);
const baselineIndex = history.indexOf(config.baselineCommit);
if (commitIndex < 0 || baselineIndex < 0 || commitIndex >= baselineIndex) {
  throw new Error('The release commit and baseline must be on the first-parent master history, after the baseline.');
}
process.stdout.write(versionFromDistance(config.baselineVersion, baselineIndex - commitIndex));
