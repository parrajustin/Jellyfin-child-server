#!/usr/bin/env node
// Lays the sample clips out as a Jellyfin library tree for the parent server.
// Usage: node build-library.mjs <outDir>
import { copyFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const samples = [
  'file_example_AVI_480_750kB.avi',
  'file_example_AVI_640_800kB.avi',
  'file_example_AVI_1280_1_5MG.avi',
  'file_example_AVI_1920_2_3MG.avi'
].map((name) => join(here, 'samples', name));

const outDir = process.argv[2];
if (!outDir) {
  console.error('usage: build-library.mjs <outDir>');
  process.exit(2);
}

/** @type {Array<[string, number]>} target path (relative) and sample index */
// Movie names avoid resolution tokens such as "720p": Jellyfin strips those from titles,
// which would make all three movies share one name.
const layout = [
  ['Movies/Sample Movie Alpha (2018)/Sample Movie Alpha (2018).avi', 0],
  ['Movies/Sample Movie Beta (2018)/Sample Movie Beta (2018).avi', 2],
  ['Movies/Sample Movie Gamma (2018)/Sample Movie Gamma (2018).avi', 3],
  ['TV Shows/Supernatural/Season 01/Supernatural S01E01.avi', 0],
  ['TV Shows/Supernatural/Season 01/Supernatural S01E02.avi', 1],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E01.avi', 0],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E02.avi', 1],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E03.avi', 2],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E04.avi', 3],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E05.avi', 0],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E06.avi', 1],
  ['TV Shows/Supernatural/Season 02/Supernatural S02E07.avi', 2],
  ['TV Shows/Supernatural/Season 03/Supernatural S03E01.avi', 3]
];

let written = 0;
for (const [relative, sampleIndex] of layout) {
  const target = join(outDir, relative);
  mkdirSync(dirname(target), { recursive: true });
  if (!existsSync(target)) {
    copyFileSync(samples[sampleIndex], target);
    written++;
  }
}
console.log(`library ready at ${outDir} (${written} files written, ${layout.length} total)`);
