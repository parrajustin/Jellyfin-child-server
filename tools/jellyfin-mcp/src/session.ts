/**
 * The connection the tools share.
 *
 * An agent signs in once and then calls the other tools; keeping the client here means each tool
 * does not have to be handed a URL and a token again. Nothing is persisted to disk: the session
 * lives for as long as the MCP server process does.
 */

import { JellyfinClient } from './client.js';
import type { ParentRequestHeader } from './types.js';

export class NotSignedInError extends Error {
  constructor() {
    super('Not signed in. Call jellyfin_login first.');
    this.name = 'NotSignedInError';
  }
}

let client: JellyfinClient | null = null;

export function setClient(next: JellyfinClient): void {
  client = next;
}

export function currentClient(): JellyfinClient {
  if (!client) {
    throw new NotSignedInError();
  }
  return client;
}

export function clearClient(): void {
  client = null;
}

/** Turns the wire format (`[{Name, Value}]`) into the fetch format (`{name: value}`). */
export function headersToRecord(headers: ParentRequestHeader[] | undefined): Record<string, string> {
  const record: Record<string, string> = {};
  for (const header of headers ?? []) {
    if (header.Name && header.Name.trim()) {
      record[header.Name.trim()] = header.Value ?? '';
    }
  }
  return record;
}

/** Turns `{name: value}` into the wire format the child server stores. */
export function recordToHeaders(record: Record<string, string> | undefined): ParentRequestHeader[] {
  return Object.entries(record ?? {})
    .filter(([name]) => name.trim().length > 0)
    .map(([name, value]) => ({ Name: name.trim(), Value: value ?? '' }));
}
