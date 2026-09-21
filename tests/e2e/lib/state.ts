/**
 * The state file written by global setup and read by the specs: tokens, user ids and the
 * item ids of the fixture library on BOTH servers.
 */
import fs from 'node:fs';
import path from 'node:path';
import { env } from './env';

export interface AdminState {
  username: string;
  password: string;
  userId: string;
  token: string;
  deviceId: string;
}

export interface MovieRef {
  id: string;
  /** Item name as the server reports it (Jellyfin strips "480p"-style tags, so it is not unique). */
  name: string;
  /** File base name, for example "Sample Movie 480p (2018)"; falls back to the name. */
  key: string;
  path?: string;
  year?: number;
}

export interface EpisodeRef {
  id: string;
  name: string;
  /** "S02E01" style key. */
  key: string;
  season: number;
  episode: number;
  seasonId?: string;
  path?: string;
}

export interface ServerState {
  /** URL as seen from the test runner. */
  url: string;
  /** GET /System/Info/Public Id. */
  serverId: string;
  serverName: string;
  version: string;
  admin: AdminState;
  /** Movies plus episodes. */
  itemCount: number;
  supernatural: {
    seriesId: string;
    seasonCount: number;
    /** "S01" -> season item id. */
    seasonIds: Record<string, string>;
  };
  /** "S02E01" -> episode. */
  episodes: Record<string, EpisodeRef>;
  /** file base name -> movie. */
  movies: Record<string, MovieRef>;
}

export interface E2EState {
  version: 1;
  createdAt: string;
  /** The parent URL the child was configured with (through the gate). */
  parentUrlForChild: string;
  gate: { url: string; controlUrl: string };
  parent: ServerState;
  child: ServerState;
}

const pad = (n: number): string => String(n).padStart(2, '0');

export function episodeKey(season: number, episode: number): string {
  return `S${pad(season)}E${pad(episode)}`;
}

export function seasonKey(season: number): string {
  return `S${pad(season)}`;
}

/** Stable, unique movie key: the file base name when the server reports a path, else the name. */
export function movieKey(item: { Name: string; Path?: string | null }): string {
  if (item.Path) {
    const base = path.posix.basename(item.Path.replace(/\\/g, '/'));
    return base.replace(/\.[^.]+$/, '');
  }
  return item.Name;
}

export function writeState(state: E2EState, file: string = env.stateFile): void {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  fs.writeFileSync(tmp, `${JSON.stringify(state, null, 2)}\n`, 'utf8');
  fs.renameSync(tmp, file);
}

export function readState(file: string = env.stateFile): E2EState {
  if (!fs.existsSync(file)) {
    throw new Error(
      `state file ${file} not found: global setup did not run. ` +
        'Run the suite with `bash tests/e2e/run.sh` or `npx playwright test` (which runs lib/global-setup.ts first).',
    );
  }
  const parsed = JSON.parse(fs.readFileSync(file, 'utf8')) as Partial<E2EState>;
  if (parsed.version !== 1 || !parsed.parent || !parsed.child) {
    throw new Error(`state file ${file} has an unexpected shape; delete it and run global setup again`);
  }
  return parsed as E2EState;
}
