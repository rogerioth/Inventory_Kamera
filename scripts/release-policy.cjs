'use strict';

function versionFromDistance(baseline, distance) {
  if (!/^\d+\.\d+\.\d+$/.test(baseline) || !Number.isSafeInteger(distance) || distance < 1) {
    throw new Error('A release requires a three-part baseline and at least one new master commit.');
  }
  const parts = baseline.split('.').map(Number);
  parts[2] += distance;
  if (parts.some(part => part > 65534)) throw new Error('Version exceeds the Windows assembly version limit.');
  return parts.join('.');
}

function circleBuildNumber(targetUrl, owner, repo) {
  const url = new URL(targetUrl);
  const prefix = `/gh/${owner}/${repo}/`;
  if (url.origin !== 'https://circleci.com' || !url.pathname.startsWith(prefix) ||
      !/^\d+$/.test(url.pathname.slice(prefix.length)) || url.search || url.hash) {
    throw new Error('The required status does not link to this repository on CircleCI.');
  }
  return Number(url.pathname.slice(prefix.length));
}

function verifyCircleBuild(build, sha, owner, repo) {
  if (build.status !== 'success' || build.branch !== 'master' || build.vcs_revision !== sha ||
      build.username !== owner || build.reponame !== repo || build.pull_requests?.length) {
    throw new Error('CircleCI did not successfully validate this exact master commit.');
  }
}

module.exports = { versionFromDistance, circleBuildNumber, verifyCircleBuild };
