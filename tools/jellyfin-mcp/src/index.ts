#!/usr/bin/env node
/**
 * stdio entry point for the Jellyfin child server MCP server.
 *
 * Nothing is written to stdout except the protocol itself; diagnostics go to stderr, because
 * stdout is the transport.
 */

import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';

import { registerTools } from './tools.js';

export function createServer(): McpServer {
  const server = new McpServer(
    { name: 'jellyfin-child-server', version: '1.0.0' },
    {
      instructions:
        'Configure and exercise a Jellyfin child server. Call jellyfin_login first, then ' +
        'jellyfin_test_parent_connection to try a parent, jellyfin_get_logs when it fails, ' +
        'jellyfin_connect_parent to save it, jellyfin_sync to mirror, and jellyfin_test_playback ' +
        'to prove a video starts. For a parent behind Cloudflare Access pass the service token as ' +
        'headers: {"CF-Access-Client-Id": "...", "CF-Access-Client-Secret": "..."}.',
    },
  );
  registerTools(server);
  return server;
}

async function main(): Promise<void> {
  const server = createServer();
  const transport = new StdioServerTransport();
  await server.connect(transport);
  process.stderr.write('jellyfin-child-server MCP server ready on stdio\n');
}

// Only run when executed directly, so the tests can import createServer without starting stdio.
const invokedDirectly =
  process.argv[1] !== undefined && import.meta.url === `file://${process.argv[1]}`;

if (invokedDirectly) {
  main().catch((error: unknown) => {
    process.stderr.write(`fatal: ${error instanceof Error ? error.stack ?? error.message : String(error)}\n`);
    process.exitCode = 1;
  });
}
