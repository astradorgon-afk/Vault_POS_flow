// Root navigation worker: server pages win while reachable; the cached client
// takes over supported VaultFlow routes only when navigation fails.
self.importScripts('/offline/service-worker-assets.js');
const cachePrefix = 'vaultflow-root-shell-';
const cacheName = cachePrefix + self.assetsManifest.version;
const shell = '/offline/index.html';
const readyMarker = '/offline/cache-ready';
const routes = new Set(['/login', '/', '/purchasing', '/receiving', '/inventory/stock', '/sync', '/team', '/offline', '/offline/']);
const cacheable = /\.(?:dll|pdb|wasm|html|js|json|css|woff2?|png|jpe?g|gif|ico|blat|dat|webmanifest)$/i;

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
        const cache = await caches.open(cacheName);
        await cache.addAll(assets);
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

self.addEventListener('install', event => event.waitUntil(downloadShell().then(() => self.skipWaiting())));

self.addEventListener('activate', event => event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(key => key.startsWith(cachePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));
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
    if (url.pathname.startsWith('/offline/') || url.pathname.startsWith('/_content/Pos.SharedUI/'))
        event.respondWith((async () => {
            const cache = await caches.open(cacheName);
            const alias = url.pathname.replace(/\.[a-z0-9]{10,}\.(js|css)$/i, '.$1');
            return await cache.match(request) ||
                (alias !== url.pathname ? await cache.match(alias) : null) ||
                fetch(request);
        })());
});
