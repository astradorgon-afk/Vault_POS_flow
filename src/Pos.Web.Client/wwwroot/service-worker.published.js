// Upgrade path for installations that used the former /offline/ worker.
// The root worker owns the current app. Keep old caches as a fallback until a
// visit to /login installs the root worker; never remove IndexedDB saved work.
self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

async function cached(request, navigation = false) {
    const keys = await caches.keys();
    for (const key of keys.filter(name => name.startsWith('vaultflow-pwa-shell-')).reverse()) {
        const cache = await caches.open(key);
        const response = await cache.match(request) ||
            (navigation ? await cache.match('/offline/index.html') || await cache.match('index.html') : null);
        if (response) return response;
    }
    return Response.error();
}

self.addEventListener('fetch', event => {
    const request = event.request;
    const path = new URL(request.url).pathname;
    if (request.method !== 'GET' || path.startsWith('/offline/api/')) return;
    const navigation = request.mode === 'navigate' && path.startsWith('/offline/');
    if (!navigation && !path.startsWith('/offline/')) return;
    event.respondWith(fetch(request).catch(() => cached(request, navigation)));
});

self.addEventListener('message', event => {
    if (event.data?.type === 'VAULTFLOW_CACHE_STATUS')
        event.waitUntil(caches.keys().then(keys => event.ports[0]?.postMessage({
            ready: keys.some(key => key.startsWith('vaultflow-pwa-shell-')),
            version: 'legacy-migration',
        })));
});
