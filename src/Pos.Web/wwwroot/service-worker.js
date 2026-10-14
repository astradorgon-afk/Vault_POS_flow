// Root navigation worker: server pages win while reachable; the cached client
// takes over supported VaultFlow routes only when navigation fails.
self.importScripts('/offline/service-worker-assets.js');
const cachePrefix = 'vaultflow-root-shell-';
const cacheName = cachePrefix + self.assetsManifest.version;
const shell = '/offline/index.html';
const readyMarker = '/offline/cache-ready';
const routes = new Set(['/login', '/', '/purchasing', '/receiving', '/inventory/stock', '/restock', '/stores', '/sync', '/team', '/offline', '/offline/']);
const cacheable = /\.(?:dll|pdb|wasm|html|js|json|css|woff2?|png|jpe?g|gif|ico|blat|dat|webmanifest)$/i;

// Served by the server project, so a change to them does not move the client's
// asset-manifest version that names this cache. They were therefore frozen at
// whatever an installed device downloaded first; refreshing them on activation
// is what lets a restyle reach devices already in the field.
const shellAssets = ['/app.css', '/fonts/dm-sans-latin.woff2', '/fonts/manrope-latin.woff2'];

// Fetched one at a time with a retry rather than through cache.addAll, which
// rejects the whole batch if any single asset fails. On a store's connection
// that meant one flaky file left the device with no offline capability at all.
async function fill(cache, requests) {
    const failed = [];
    for (const request of requests) {
        let stored = false;
        for (let attempt = 0; attempt < 3 && !stored; attempt++) {
            try {
                const response = await fetch(request.clone());
                if (!response.ok) throw new Error(`HTTP ${response.status}`);
                await cache.put(request, response);
                stored = true;
            } catch {
                if (attempt === 2) failed.push(request.url);
                else await new Promise(resolve => setTimeout(resolve, 300 * (attempt + 1)));
            }
        }
    }
    if (failed.length > 0)
        throw new Error(`${failed.length} offline file(s) could not be downloaded, starting with ${failed[0]}.`);
}

let cacheDownload;
function downloadShell() {
    cacheDownload ??= (async () => {
        const assets = self.assetsManifest.assets
            .filter(asset => cacheable.test(asset.url) && !asset.url.endsWith('/service-worker.js'))
            .map(asset => new Request(new URL('/' + asset.url
                .replace(/^offline\/_content\//, '_content/')
                .replace(/^\/+/, ''), self.origin), {
                integrity: asset.hash, cache: 'no-cache'
            }));
        assets.push(new Request('/offline/_framework/blazor.webassembly.js', { cache: 'no-cache' }));
        assets.push(new Request('/offline/_framework/dotnet.js', { cache: 'no-cache' }));
        // The client uses the same visual shell as the server-rendered site.
        // These come from the server project, so they are not covered by the
        // asset manifest's version and are revalidated on every activation.
        for (const path of shellAssets)
            assets.push(new Request(path, { cache: 'no-cache' }));
        const cache = await caches.open(cacheName);
        await fill(cache, assets);
        // A proxy can answer a script request with an empty page or access notice.
        // Cache.addAll accepts HTTP 200, so validate the critical startup files.
        for (const [url, type] of [
            [shell, 'text/html'],
            ['/offline/_framework/blazor.webassembly.js', 'javascript'],
            ['/offline/_framework/dotnet.js', 'javascript'],
        ]) {
            const response = await cache.match(url);
            if (!response?.ok || !response.headers.get('content-type')?.includes(type) ||
                (await response.clone().arrayBuffer()).byteLength === 0)
                throw new Error(`The offline startup file ${url} was not downloaded correctly.`);
        }
        await cache.put(readyMarker, new Response('ready'));
    })().finally(() => { cacheDownload = null; });
    return cacheDownload;
}

self.addEventListener('install', event => event.waitUntil(downloadShell().then(async () => {
    // Taking over at once is right for a first install: there is no page
    // running yet and no older cache to pull out from under one. On an update
    // there is, and activating would delete the very assets an open tab still
    // lazy-loads from — so that one waits for the tabs to close, which is the
    // browser's own default.
    if (!self.registration.active) await self.skipWaiting();
})));

self.addEventListener('activate', event => event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(key => key.startsWith(cachePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    // Pick up a restyle shipped by the server project on its own.
    try {
        const cache = await caches.open(cacheName);
        await fill(cache, shellAssets.map(path => new Request(path, { cache: 'reload' })));
    } catch {
        // The cached copies still work; the next activation tries again.
    }

    await self.clients.claim();
})()));

self.addEventListener('message', event => {
    if (event.data?.type === 'VAULTFLOW_CACHE_STATUS')
        event.waitUntil(caches.has(cacheName).then(exists =>
            exists ? caches.open(cacheName).then(cache => cache.match(readyMarker)) : null)
            .then(marker => event.ports[0]?.postMessage({
                ready: Boolean(marker), version: self.assetsManifest.version
            })));
    if (event.data?.type === 'VAULTFLOW_CACHE_REPAIR')
        event.waitUntil(downloadShell().then(() =>
            event.ports[0]?.postMessage({ ready: true }))
            .catch(() => event.ports[0]?.postMessage({ ready: false })));
});

self.addEventListener('fetch', event => {
    const request = event.request;
    const url = new URL(request.url);
    if (url.origin !== self.origin || request.method !== 'GET' || url.pathname.startsWith('/offline/api/') ||
        url.pathname.startsWith('/api/')) return;
    if (request.mode === 'navigate') {
        event.respondWith((async () => {
            try {
                return await fetch(request);
            } catch {
                if (!routes.has(url.pathname)) throw new Error('This page needs a connection.');
                return (await caches.open(cacheName)).match(shell) || Response.error();
            }
        })());
        return;
    }
    if (url.pathname.startsWith('/offline/') || url.pathname.startsWith('/_content/Pos.SharedUI/') ||
        url.pathname === '/app.css' || url.pathname.startsWith('/fonts/'))
        event.respondWith((async () => {
            const cache = await caches.open(cacheName);
            const alias = url.pathname.replace(/\.[a-z0-9]{10,}\.(js|css)$/i, '.$1');
            return await cache.match(request) ||
                (alias !== url.pathname ? await cache.match(alias) : null) ||
                fetch(request);
        })());
});
