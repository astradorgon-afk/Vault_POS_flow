const databaseName = 'vaultflow-pwa-v1';
const maxOfflineAge = 7 * 24 * 60 * 60 * 1000;
let active = null;
let databasePromise;
let lifecycleListener;
let onlinePreparation = null;

function database() {
    databasePromise ??= new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, 1);
        request.onupgradeneeded = () => {
            const db = request.result;
            db.createObjectStore('meta');
            db.createObjectStore('profiles', { keyPath: 'scope' });
            db.createObjectStore('data');
            const outbox = db.createObjectStore('outbox', { keyPath: 'eventId' });
            outbox.createIndex('scope', 'scope');
            outbox.createIndex('sequence', 'sequence');
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
    return databasePromise;
}

function req(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

function completed(tx) {
    return new Promise((resolve, reject) => {
        tx.oncomplete = () => resolve();
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error || new Error('The local save was interrupted.'));
    });
}

const bytes = value => new TextEncoder().encode(value);
const b64 = buffer => {
    // Catalog snapshots can exceed the engine's function-argument limit.
    const value = new Uint8Array(buffer);
    let binary = '';
    for (let offset = 0; offset < value.length; offset += 0x8000)
        binary += String.fromCharCode(...value.subarray(offset, offset + 0x8000));
    return btoa(binary);
};
const fromB64 = value => Uint8Array.from(atob(value), char => char.charCodeAt(0));

async function keyFor(password, salt) {
    const base = await crypto.subtle.importKey('raw', bytes(password), 'PBKDF2', false, ['deriveKey']);
    return crypto.subtle.deriveKey({ name: 'PBKDF2', salt: fromB64(salt), iterations: 250000,
        hash: 'SHA-256' }, base, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
}

async function sealFor(value, key) {
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key,
        bytes(JSON.stringify(value)));
    return { iv: b64(iv), cipher: b64(cipher) };
}

async function seal(value) {
    if (!active) throw new Error('Unlock your local workspace first.');
    return sealFor(value, active.key);
}

async function open(sealed, key = active?.key) {
    if (!key || !sealed) throw new Error('The local workspace is locked.');
    const clear = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: fromB64(sealed.iv) },
        key, fromB64(sealed.cipher));
    return JSON.parse(new TextDecoder().decode(clear));
}

async function meta(name) {
    const db = await database();
    return req(db.transaction('meta').objectStore('meta').get(name));
}

async function setMeta(name, value) {
    const db = await database();
    const tx = db.transaction('meta', 'readwrite');
    tx.objectStore('meta').put(value, name);
    await completed(tx);
}

function scopeOf(userId, locationId) { return `${userId}:${locationId}`; }

export async function initialize() {
    const db = await database();
    const profiles = await req(db.transaction('profiles').objectStore('profiles').getAll());
    const device = await meta('device');
    const estimate = await navigator.storage?.estimate?.();
    return { device, profiles: profiles.map(({ scope, userName, displayName, lastOnlineUtc, unlockMethod }) =>
        ({ scope, userName, displayName, lastOnlineUtc, unlockMethod: unlockMethod ?? 'password' })),
        cacheReady: window.vaultFlowOffline?.ready === true, online: navigator.onLine,
        storageUsed: estimate?.usage ?? 0, storageQuota: estimate?.quota ?? 0,
        persisted: await navigator.storage?.persisted?.() ?? false };
}

export async function waitForAppFiles(timeoutMs = 20000) {
    if (!('serviceWorker' in navigator)) return false;
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        const registration = await navigator.serviceWorker.getRegistration('/');
        if (registration?.active?.scriptURL === `${location.origin}/service-worker.js`) {
            const ready = await new Promise(resolve => {
                const channel = new MessageChannel();
                const timeout = setTimeout(() => { channel.port1.close(); resolve(false); }, 2000);
                channel.port1.onmessage = event => {
                    clearTimeout(timeout);
                    channel.port1.close();
                    resolve(event.data?.ready === true);
                };
                try { registration.active.postMessage({ type: 'VAULTFLOW_CACHE_STATUS' }, [channel.port2]); }
                catch { clearTimeout(timeout); channel.port1.close(); resolve(false); }
            });
            if (ready) return true;
        }
        await new Promise(resolve => setTimeout(resolve, 500));
    }
    return false;
}

async function api(path, options = {}) {
    const response = await fetch(`/offline/api/${path}`, {
        credentials: 'same-origin', cache: 'no-store', ...options,
        headers: { 'X-VaultFlow-PWA': '1', ...(options.headers || {}) },
    });
    if (!response.ok) {
        let detail;
        try { const body = await response.json(); detail = body.detail || body.message || body.title; }
        catch { /* The server may be unreachable or return no body. */ }
        const error = new Error(detail || `Server returned ${response.status}.`);
        error.status = response.status;
        throw error;
    }
    return response.status === 204 ? null : response.json();
}

export async function enrol(enrolmentCode) {
    const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' },
        false, ['sign', 'verify']);
    const publicBytes = await crypto.subtle.exportKey('spki', pair.publicKey);
    const thumbprint = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', publicBytes)))
        .map(byte => byte.toString(16).padStart(2, '0')).join('');
    const result = await api('enrol', { method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enrolmentCode, publicKeyThumbprint: thumbprint,
            platform: 4, appVersion: 'PWA 1', osVersion: navigator.userAgent.slice(0, 64) }) });
    const device = { deviceId: result.deviceId, locationId: result.locationId };
    await setMeta('device', device);
    await setMeta('deviceKey', pair.privateKey);
    return device;
}

async function unlockProfile(profile, password) {
    const key = await keyFor(password, profile.salt);
    try {
        const verifier = await open(profile.verifier, key);
        if (verifier !== profile.scope) throw new Error();
    } catch { throw new Error(profile.unlockMethod === 'pin'
        ? 'Offline PIN did not unlock this employee’s local data.'
        : 'Password did not unlock this employee’s local data.'); }
    active = { scope: profile.scope, userId: profile.userId, locationId: profile.locationId,
        displayName: profile.displayName, key };
    return active;
}

export async function login(userName, password, twoFactorCode, offlinePin) {
    const device = await meta('device');
    if (!device) throw new Error('Enroll this browser installation first.');
    const result = await api('login', { method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ userName, password, twoFactorCode, ...device }) });
    const userId = result.user.userId;
    const scope = scopeOf(userId, device.locationId);
    const db = await database();
    let profile = await req(db.transaction('profiles').objectStore('profiles').get(scope));
    if (!profile) {
        profile = { scope, userId, locationId: device.locationId,
            displayName: result.user.displayName,
            userName, unlockMethod: 'password', permissions: result.user.permissions,
            salt: b64(crypto.getRandomValues(new Uint8Array(16))),
            lastOnlineUtc: new Date().toISOString() };
        const key = await keyFor(password, profile.salt);
        active = { scope, userId, locationId: device.locationId,
            displayName: profile.displayName, key };
        profile.verifier = await seal(scope);
    } else {
        if (profile.unlockMethod === 'pin' && !offlinePin)
            throw new Error('Enter this employee’s offline PIN to open saved data after online sign-in.');
        await unlockProfile(profile, profile.unlockMethod === 'pin' ? offlinePin : password);
        profile.lastOnlineUtc = new Date().toISOString();
        profile.displayName = result.user.displayName;
        profile.userName = userName;
        profile.permissions = result.user.permissions;
    }
    const tx = db.transaction('profiles', 'readwrite');
    tx.objectStore('profiles').put(profile);
    await completed(tx);
    try { await navigator.storage?.persist?.(); }
    catch { /* Storage persistence is optional; readiness still shows eviction risk. */ }
    return { user: result.user, scope };
}

async function rewrapProfile(profile, oldKey, password) {
    const db = await database();
    const keys = await req(db.transaction('data').objectStore('data').getAllKeys());
    const ownKeys = keys.filter(key => typeof key === 'string' && key.startsWith(`${profile.scope}:`));
    const oldValues = await Promise.all(ownKeys.map(key =>
        req(db.transaction('data').objectStore('data').get(key))));
    const clearValues = await Promise.all(oldValues.map(value => open(value, oldKey)));
    const salt = b64(crypto.getRandomValues(new Uint8Array(16)));
    const newKey = await keyFor(password, salt);
    const newValues = await Promise.all(clearValues.map(value => sealFor(value, newKey)));
    const updated = { ...profile, salt, unlockMethod: 'password',
        verifier: await sealFor(profile.scope, newKey), lastOnlineUtc: new Date().toISOString() };
    const tx = db.transaction(['profiles', 'data'], 'readwrite');
    ownKeys.forEach((key, index) => tx.objectStore('data').put(newValues[index], key));
    tx.objectStore('profiles').put(updated);
    await completed(tx);
    active = { scope: profile.scope, userId: profile.userId, locationId: profile.locationId,
        displayName: profile.displayName, key: newKey };
    return updated;
}

// Called after the online form has authenticated once. The handoff establishes
// the PWA cookie without reusing an authenticator or recovery code.
export async function prepareFromHandoff(token, userName, password, oldSecret = '') {
    const device = await meta('device');
    if (!device) return { status: 'unenrolled' };
    if (!onlinePreparation) {
        const result = await api('handoff', { method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ token, deviceId: device.deviceId, locationId: device.locationId }) });
        onlinePreparation = { user: result.user, device };
    }
    const { user } = onlinePreparation;
    const scope = scopeOf(user.userId, device.locationId);
    const db = await database();
    let profile = await req(db.transaction('profiles').objectStore('profiles').get(scope));
    if (profile) {
        const currentKey = await keyFor(password, profile.salt);
        let passwordWorks = false;
        try { passwordWorks = await open(profile.verifier, currentKey) === scope; }
        catch { /* An old PIN or password still protects this profile. */ }
        if (passwordWorks) {
            active = { scope, userId: user.userId, locationId: device.locationId,
                displayName: user.displayName, key: currentKey };
        } else {
            if (!oldSecret) return { status: 'old-secret-required',
                secretKind: profile.unlockMethod === 'pin' ? 'pin' : 'previous-password' };
            const oldKey = await keyFor(oldSecret, profile.salt);
            try {
                if (await open(profile.verifier, oldKey) !== scope) throw new Error();
            } catch { throw new Error('The previous offline PIN or password did not unlock saved data.'); }
            profile = await rewrapProfile(profile, oldKey, password);
        }
        profile = { ...profile, userName, displayName: user.displayName,
            permissions: user.permissions, lastOnlineUtc: new Date().toISOString() };
    } else {
        const salt = b64(crypto.getRandomValues(new Uint8Array(16)));
        const key = await keyFor(password, salt);
        profile = { scope, userId: user.userId, locationId: device.locationId,
            displayName: user.displayName, userName, unlockMethod: 'password',
            permissions: user.permissions, salt, lastOnlineUtc: new Date().toISOString(),
            verifier: await sealFor(scope, key) };
        active = { scope, userId: user.userId, locationId: device.locationId,
            displayName: user.displayName, key };
    }
    const tx = db.transaction('profiles', 'readwrite');
    tx.objectStore('profiles').put(profile);
    await completed(tx);
    await download();
    onlinePreparation = null;
    return { status: 'prepared' };
}

export async function unlock(scope, password) {
    if (!scope) throw new Error('Choose an employee to unlock saved data.');
    const db = await database();
    const profile = await req(db.transaction('profiles').objectStore('profiles').get(scope));
    if (!profile) throw new Error('Saved employee data was not found. Sign in online to download it.');
    const lastOnline = Date.parse(profile.lastOnlineUtc);
    if (!Number.isFinite(lastOnline) || Date.now() - lastOnline > maxOfflineAge)
        throw new Error('Offline unlock expired. Sign in online to renew it.');
    await unlockProfile(profile, password);
    const snapshot = await loadSnapshot();
    if (!snapshot) {
        active = null;
        throw new Error('Offline data has not finished downloading. Connect and prepare this account again.');
    }
    return { scope, displayName: profile.displayName, permissions: profile.permissions ?? [],
        snapshot };
}

export async function employees() {
    return api('employees');
}

export function newPin() {
    const range = 90000000;
    const limit = Math.floor(0x100000000 / range) * range;
    let value;
    do { value = crypto.getRandomValues(new Uint32Array(1))[0]; }
    while (value >= limit);
    return String(10000000 + value % range);
}

export async function provision(userId, pin) {
    if (!active) throw new Error('Sign in as an administrator first.');
    if (!navigator.onLine) throw new Error('Connect before preparing employees.');
    if (!/^\d{8,12}$/.test(pin)) throw new Error('Use an 8 to 12 digit offline PIN.');
    const device = await meta('device');
    const scope = scopeOf(userId, device.locationId);
    const db = await database();
    const existing = await req(db.transaction('profiles').objectStore('profiles').get(scope));
    if (existing) throw new Error('This employee already has saved data on this device. Their PIN and pending work were kept.');
    const result = await api('provision', { method: 'POST',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ userId }) });
    if (result.employee.userId !== userId) throw new Error('The server returned a different employee.');
    const salt = b64(crypto.getRandomValues(new Uint8Array(16)));
    const key = await keyFor(pin, salt);
    const profile = { scope, userId, locationId: device.locationId,
        userName: result.employee.userName, displayName: result.employee.displayName,
        unlockMethod: 'pin', permissions: result.employee.permissions,
        salt, lastOnlineUtc: new Date().toISOString(),
        verifier: await sealFor(scope, key) };
    const snapshot = await sealFor(result.snapshot, key);
    const tx = db.transaction(['profiles', 'data', 'meta'], 'readwrite');
    tx.objectStore('profiles').put(profile);
    tx.objectStore('data').put(snapshot, `${scope}:snapshot`);
    tx.objectStore('meta').put(result.snapshot.baseline.cursor, `${scope}:cursor`);
    await completed(tx);
    return { scope, displayName: profile.displayName };
}

export async function lock() {
    active = null;
    try { await api('logout', { method: 'POST' }); }
    catch { /* Local locking must still work when disconnected. */ }
}
export function currentScope() { return active?.scope ?? null; }

async function assertOnlineIdentity() {
    if (!active) throw new Error('Unlock your local workspace first.');
    const session = await api('me');
    if (session.user?.userId !== active.userId || session.locationId !== active.locationId)
        throw new Error('Sign in online as this employee at this location before syncing.');
}

export function watchLifecycle(dotnet) {
    unwatchLifecycle();
    lifecycleListener = () => {
        if (document.visibilityState === 'visible') {
            dotnet.invokeMethodAsync('OnBrowserResume').catch(() => {});
        }
    };
    window.addEventListener('online', lifecycleListener);
    window.addEventListener('offline', lifecycleListener);
    window.addEventListener('vaultflow-offline-status', lifecycleListener);
    window.addEventListener('pageshow', lifecycleListener);
    document.addEventListener('visibilitychange', lifecycleListener);
}

export function unwatchLifecycle() {
    if (!lifecycleListener) return;
    window.removeEventListener('online', lifecycleListener);
    window.removeEventListener('offline', lifecycleListener);
    window.removeEventListener('vaultflow-offline-status', lifecycleListener);
    window.removeEventListener('pageshow', lifecycleListener);
    document.removeEventListener('visibilitychange', lifecycleListener);
    lifecycleListener = null;
}

export async function download() {
    if (!active) throw new Error('Unlock the local workspace first.');
    await assertOnlineIdentity();
    const snapshot = await api('snapshot');
    if (snapshot.baseline?.errorCode) throw new Error(snapshot.baseline.message || 'Baseline refused.');
    const sealed = await seal(snapshot);
    const db = await database();
    const tx = db.transaction(['data', 'meta'], 'readwrite');
    tx.objectStore('data').put(sealed, `${active.scope}:snapshot`);
    tx.objectStore('meta').put(snapshot.baseline.cursor, `${active.scope}:cursor`);
    const device = await req(tx.objectStore('meta').get('device'));
    if (device && snapshot.stockLevels?.name)
        tx.objectStore('meta').put({ ...device, locationName: snapshot.stockLevels.name }, 'device');
    await completed(tx);
    return snapshot;
}

export async function loadSnapshot() {
    if (!active) throw new Error('Unlock the local workspace first.');
    const db = await database();
    const sealed = await req(db.transaction('data').objectStore('data').get(`${active.scope}:snapshot`));
    return sealed ? open(sealed) : null;
}

export async function saveWorking(kind, value) {
    if (!active) throw new Error('Unlock the local workspace first.');
    const sealed = await seal(value);
    const db = await database();
    const tx = db.transaction('data', 'readwrite');
    tx.objectStore('data').put(sealed, `${active.scope}:working:${kind}`);
    await completed(tx);
}

export async function loadWorking(kind) {
    if (!active) throw new Error('Unlock the local workspace first.');
    const db = await database();
    const sealed = await req(db.transaction('data').objectStore('data').get(`${active.scope}:working:${kind}`));
    return sealed ? open(sealed) : null;
}

export async function queue(eventType, payload, label) {
    if (!active) throw new Error('Unlock the local workspace first.');
    const eventId = crypto.randomUUID();
    const sealed = await seal(payload);
    const db = await database();
    const tx = db.transaction(['meta', 'data', 'outbox'], 'readwrite');
    const currentSequence = (await req(tx.objectStore('meta').get('sequence'))) ?? 0;
    const sequence = currentSequence + 1;
    const now = new Date().toISOString();
    tx.objectStore('meta').put(sequence, 'sequence');
    tx.objectStore('data').put(sealed, `${active.scope}:operation:${eventId}`);
    tx.objectStore('outbox').put({ eventId, sequence, scope: active.scope,
        eventType, label, status: 'Saved on device', occurredAtUtc: now,
        updatedAtUtc: now, message: null });
    try { await completed(tx); }
    catch (error) {
        if (error?.name === 'QuotaExceededError')
            throw new Error('Device storage is full. The operation was not queued. Free space without clearing VaultFlow site data.');
        throw error;
    }
    return eventId;
}

export async function operations() {
    if (!active) throw new Error('Unlock the local workspace first.');
    const db = await database();
    const all = await req(db.transaction('outbox').objectStore('outbox').index('scope').getAll(active.scope));
    return all.sort((a, b) => b.sequence - a.sequence);
}

export async function operationPayload(eventId) {
    if (!active) throw new Error('Unlock your local workspace first.');
    const db = await database();
    const item = await req(db.transaction('outbox').objectStore('outbox').get(eventId));
    if (!item || item.scope !== active.scope) throw new Error('This operation belongs to another employee.');
    const sealed = await req(db.transaction('data').objectStore('data').get(`${active.scope}:operation:${eventId}`));
    return open(sealed);
}

async function mark(item, status, message, entityId = item.entityId ?? null) {
    const db = await database();
    const tx = db.transaction('outbox', 'readwrite');
    tx.objectStore('outbox').put({ ...item, status, message, entityId,
        updatedAtUtc: new Date().toISOString() });
    await completed(tx);
}

export async function synchronize() {
    if (!active) throw new Error('Unlock the local workspace first.');
    await assertOnlineIdentity();
    const scope = active.scope;
    const db = await database();
    const all = await req(db.transaction('outbox').objectStore('outbox').getAll());
    let sent = 0;
    for (const item of all.sort((a, b) => a.sequence - b.sequence)) {
        if (item.scope !== scope || !['Saved on device', 'Syncing'].includes(item.status)) continue;
        const sealed = await req(db.transaction('data').objectStore('data').get(`${scope}:operation:${item.eventId}`));
        if (!sealed) { await mark(item, 'Needs review', 'Local event payload is missing.'); continue; }
        const payload = await open(sealed);
        await mark(item, 'Syncing', null);
        try {
            const result = await api('push', { method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ events: [{ eventId: item.eventId,
                    deviceSequence: item.sequence, eventType: item.eventType,
                    payload, occurredAtUtc: item.occurredAtUtc }] }) });
            const event = result.results?.find(row => row.eventId === item.eventId);
            if (!event) throw new Error('The server did not return an event result.');
            const status = ['Accepted'].includes(event.outcome) ? 'Confirmed'
                : event.outcome === 'Conflict' ? 'Conflict'
                : event.outcome === 'Duplicate' ? 'Confirmed'
                : event.outcome === 'Deferred' ? 'Saved on device' : 'Needs review';
            await mark(item, status, event.message || event.errorCode || null, event.entityId);
            sent++;
            if (status === 'Saved on device') break;
        } catch (error) {
            await mark(item, 'Saved on device', error.message);
            break;
        }
    }
    try {
        let cursor = await meta(`${scope}:cursor`) ?? 0;
        let changed = false;
        for (let page = 0; page < 20; page++) {
            const result = await api(`pull?cursor=${cursor}`);
            if (result.errorCode) throw new Error(result.message || result.errorCode);
            changed ||= result.changes?.length > 0;
            cursor = result.nextCursor;
            await setMeta(`${scope}:cursor`, cursor);
            if ((result.changes?.length ?? 0) < 500) break;
        }
        if (changed || sent > 0) await download();
    } catch (error) {
        if (error.status === 410) await download();
        // Pending events are still intact when a feed refresh cannot finish.
    }
    return { sent, operations: await operations() };
}

export async function readiness() {
    const state = await initialize();
    const pending = active ? (await operations()).filter(item => item.status !== 'Confirmed').length : 0;
    return { ...state, scopedPending: pending };
}
