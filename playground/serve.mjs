// A static file server for ./site, for looking at the playground before deploying it.
//
// Not a general-purpose server and not meant to become one — GitHub Pages serves the real thing.
// It exists because .wasm must arrive as application/wasm or the browser refuses to stream-compile
// it, and the usual one-line Python server gets that wrong.

import { createServer } from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import { dirname, extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), 'site');
const port = Number(process.env.PORT) || 8080;

const TYPES = {
    '.html': 'text/html; charset=utf-8',
    '.js':   'text/javascript; charset=utf-8',
    '.mjs':  'text/javascript; charset=utf-8',
    '.css':  'text/css; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.wasm': 'application/wasm',
    '.dat':  'application/octet-stream',
    '.ttf':  'font/ttf',
    '.woff2':'font/woff2',
    '.map':  'application/json; charset=utf-8',
};

createServer(async (req, res) => {
    // Strip the query string, then normalise before joining — otherwise "../" in a request path
    // escapes the site directory and serves the rest of the disk.
    const requested = decodeURIComponent(new URL(req.url, 'http://localhost').pathname);
    const safe = normalize(requested).replace(/^(\.\.[/\\])+/, '');
    let path = join(root, safe);

    try {
        if ((await stat(path)).isDirectory()) path = join(path, 'index.html');
    } catch {
        res.writeHead(404, { 'content-type': 'text/plain' });
        return res.end(`not found: ${safe}`);
    }

    try {
        const body = await readFile(path);
        res.writeHead(200, {
            'content-type': TYPES[extname(path).toLowerCase()] ?? 'application/octet-stream',
            'cache-control': 'no-store',
        });
        res.end(body);
    } catch {
        res.writeHead(404, { 'content-type': 'text/plain' });
        res.end(`not found: ${safe}`);
    }
}).listen(port, () => console.log(`\n  Cufet playground → http://localhost:${port}\n`));
