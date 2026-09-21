/**
 * Environment of one e2e run. Defaults are the host ports published by docker-compose.yml,
 * so `npx playwright test` from a developer machine talks to a stack started with
 * `docker compose up -d parent gate child`. Inside the e2e container the compose file
 * overrides every URL with the service names.
 */
import path from 'node:path';

/** The tests/e2e directory (this file lives in tests/e2e/lib). */
export const E2E_ROOT = path.resolve(__dirname, '..');

function read(name: string, fallback: string): string {
  const value = process.env[name];
  return value === undefined || value.trim() === '' ? fallback : value.trim();
}

const url = (name: string, fallback: string): string => read(name, fallback).replace(/\/+$/, '');

export const env = {
  /** Child server as reached by the test runner. */
  childUrl: url('CHILD_URL', 'http://localhost:18196'),
  /** Parent server as reached by the test runner (direct, not through the gate). */
  parentUrl: url('PARENT_URL', 'http://localhost:18096'),
  /** Gate proxy as reached by the test runner (forwards to the parent). */
  gateUrl: url('GATE_URL', 'http://localhost:18097'),
  /** Gate control API as reached by the test runner. */
  gateControlUrl: url('GATE_CONTROL_URL', 'http://localhost:18098'),
  /** Parent URL the CHILD is configured with, inside the compose network: always the gate. */
  parentUrlForChild: url('PARENT_URL_FOR_CHILD', 'http://gate:8096'),
  /** Where the PARENT sees the fixture library: a container path in Docker, a host path locally. */
  parentMediaRoot: read('PARENT_MEDIA_ROOT', '/media').replace(/[\\/]+$/, ''),
  /** Where global setup writes ids and tokens for the specs. */
  stateFile: read('STATE_FILE', path.join(E2E_ROOT, '.work', 'state.json')),
  /** Skip screenshot assertions (playwright.config.ts `ignoreSnapshots`). */
  ignoreSnapshots: read('E2E_IGNORE_SNAPSHOTS', '0') === '1',
} as const;

export interface Credentials {
  readonly username: string;
  readonly password: string;
}

export const CREDENTIALS = {
  parentAdmin: { username: 'parentadmin', password: 'Parent!Pass123' },
  childAdmin: { username: 'childadmin', password: 'Child!Pass123' },
  /** The child signs in to the parent with the parent admin account. */
  childToParent: { username: 'parentadmin', password: 'Parent!Pass123' },
} as const satisfies Record<string, Credentials>;

export const SERVER_NAMES = {
  parent: 'E2E Parent',
  child: 'E2E Child',
} as const;

/** Client name sent in the MediaBrowser authorization header. */
export const CLIENT_NAME = 'Jellyfin Child Server E2E';

/** One device id per role so sessions on the servers are easy to tell apart. */
export const DEVICE_IDS = {
  parentAdmin: 'e2e-parent-admin',
  childAdmin: 'e2e-child-admin',
  childUser: 'e2e-child-user',
  browser: 'e2e-browser',
} as const;

export type Role = keyof typeof DEVICE_IDS;
