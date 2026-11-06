const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../../src/Pos.Web/wwwroot/service-worker.js'), 'utf8');
const handlers = new Map();
const contents = new Map();
let failDownload = false;
let invalidRuntime = false;

const caches = {
    has: async name => contents.has(name),
    keys: async () => [...contents.keys()],
    delete: async name => contents.delete(name),
    open: async name => {
        if (!contents.has(name)) contents.set(name, new Map());
        const entries = contents.get(name);
        return {
            addAll: async requests => {
                if (failDownload) throw new Error('Network unavailable');
                for (const request of requests) {
                    const url = new URL(request.url, 'https://vaultflow.test').pathname;
                    entries.set(url, new Response(invalidRuntime && url.endsWith('/dotnet.js') ? '' : 'file',
                        { headers: { 'Content-Type': url.endsWith('.html') ? 'text/html' : 'text/javascript' } }));
                }
            },
            match: async request => entries.get(new URL(request.url || request, 'https://vaultflow.test').pathname),
            put: async (request, response) => entries.set(new URL(request, 'https://vaultflow.test').pathname, response),
        };
    },
};

const worker = {
    origin: 'https://vaultflow.test',
    assetsManifest: { version: 'test-version', assets: [{ url: 'offline/index.html', hash: '' }] },
    addEventListener: (type, handler) => handlers.set(type, handler),
    importScripts: () => {},
    skipWaiting: async () => {},
    clients: { claim: async () => {} },
};
class WorkerRequest {
    constructor(url) { this.url = new URL(url, worker.origin).href; }
}

vm.runInNewContext(source, { self: worker, caches, Request: WorkerRequest, Response, URL },
    { filename: 'service-worker.js' });

async function dispatch(type, data) {
    let task;
    let reply;
    handlers.get(type)({ data, ports: [{ postMessage: message => { reply = message; } }],
        waitUntil: promise => { task = promise; } });
    await task;
    return reply;
}

(async () => {
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_STATUS' })).ready, false);
    await dispatch('install');
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_STATUS' })).ready, true);

    await caches.delete('vaultflow-root-shell-test-version');
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_STATUS' })).ready, false);
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_REPAIR' })).ready, true);
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_STATUS' })).ready, true);

    await caches.delete('vaultflow-root-shell-test-version');
    failDownload = true;
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_REPAIR' })).ready, false);
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_STATUS' })).ready, false);
    failDownload = false;
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_REPAIR' })).ready, true);
    await caches.delete('vaultflow-root-shell-test-version');
    invalidRuntime = true;
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_REPAIR' })).ready, false);
    assert.equal((await dispatch('message', { type: 'VAULTFLOW_CACHE_STATUS' })).ready, false);
    console.log('Cache repair passed: missing cache redownloads; failed or empty runtime stays unready; retry succeeds.');
})().catch(error => { console.error(error); process.exitCode = 1; });
