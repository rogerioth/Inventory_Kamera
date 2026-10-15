'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const { versionFromDistance, circleBuildNumber, verifyCircleBuild } = require('./release-policy.cjs');
const sha = 'a'.repeat(40);
const validBuild = { status: 'success', branch: 'master', vcs_revision: sha,
  username: 'rogerioth', reponame: 'Inventory_Kamera', pull_requests: [] };

test('release versions follow master history rather than run/rerun order', () => {
  assert.equal(versionFromDistance('1.3.17', 1), '1.3.18');
  assert.equal(versionFromDistance('1.3.17', 3), '1.3.20');
  assert.equal(versionFromDistance('1.3.17', 1), '1.3.18');
  for (const distance of [0, -1, 0.5, 65535]) {
    assert.throws(() => versionFromDistance('1.3.17', distance));
  }
});

test('only the repository CircleCI URL can identify the successful build', () => {
  assert.equal(circleBuildNumber('https://circleci.com/gh/rogerioth/Inventory_Kamera/9', 'rogerioth', 'Inventory_Kamera'), 9);
  for (const url of ['https://circleci.com.evil.example/gh/rogerioth/Inventory_Kamera/9',
    'https://circleci.com/gh/another/Inventory_Kamera/9',
    'https://circleci.com/gh/rogerioth/Inventory_Kamera/9?redirect=1']) {
    assert.throws(() => circleBuildNumber(url, 'rogerioth', 'Inventory_Kamera'));
  }
});

test('passing PR builds, other SHAs, failures, and another repository never authorize release', () => {
  verifyCircleBuild(validBuild, sha, 'rogerioth', 'Inventory_Kamera');
  for (const override of [{ branch: 'feature/example' }, { vcs_revision: 'b'.repeat(40) },
    { status: 'failed' }, { username: 'someone' }, { reponame: 'other' },
    { pull_requests: [{ url: 'https://github.com/rogerioth/Inventory_Kamera/pull/20' }] }]) {
    assert.throws(() => verifyCircleBuild({ ...validBuild, ...override }, sha, 'rogerioth', 'Inventory_Kamera'));
  }
});
