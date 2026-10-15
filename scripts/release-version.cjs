'use strict';

const { execFileSync } = require('node:child_process');
const { versionFromDistance } = require('./release-policy.cjs');
const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
const commit = process.argv[2];
if (!/^[a-f0-9]{40}$/.test(commit || '')) throw new Error('Specify a full commit SHA.');
// Read the baseline from the release commit so later version-policy changes do
// not change the tag chosen by a rerun of an older release.
const config = JSON.parse(git('show', `${commit}:release-version.json`));
const history = git('rev-list', '--first-parent', 'origin/master').split('\n');
const commitIndex = history.indexOf(commit);
const baselineIndex = history.indexOf(config.baselineCommit);
if (commitIndex < 0 || baselineIndex < 0 || commitIndex >= baselineIndex) {
  throw new Error('The release commit and baseline must be on the first-parent master history, after the baseline.');
}
process.stdout.write(versionFromDistance(config.baselineVersion, baselineIndex - commitIndex));
