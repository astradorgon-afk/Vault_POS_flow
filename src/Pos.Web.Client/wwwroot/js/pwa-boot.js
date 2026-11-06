(() => {
    const warning = document.getElementById('pwa-cache-warning');
    const showCacheWarning = message => {
        warning.textContent = message;
        warning.hidden = false;
    };
    window.vaultFlowOffline = { ready: false };
    const announce = ready => {
        window.vaultFlowOffline.ready = ready;
        window.dispatchEvent(new Event('vaultflow-offline-status'));
    };
    let repairPending = false;
    const repairCache = async registration => {
        if (repairPending || !navigator.onLine || !registration?.active) return;
        const worker = registration.active;
        repairPending = true;
        showCacheWarning('Offline files are downloading again. Keep this page open and connected.');
        const channel = new MessageChannel();
        const ready = await new Promise(resolve => {
            const timeout = setTimeout(() => resolve(false), 120000);
            channel.port1.onmessage = event => {
                clearTimeout(timeout);
                resolve(event.data?.ready === true);
            };
            try { worker.postMessage({ type: 'VAULTFLOW_CACHE_REPAIR' }, [channel.port2]); }
            catch { clearTimeout(timeout); resolve(false); }
        });
        channel.port1.close();
        repairPending = false;
        if (registration.active !== worker) return;
        announce(ready);
        if (ready) warning.hidden = true;
        else showCacheWarning('Offline files could not be downloaded. Check the connection and reload to retry. If browser data was cleared, work saved only on this device may have been lost.');
    };
    const checkCache = async registration => {
        if (!registration?.active) return;
        const worker = registration.active;
        const channel = new MessageChannel();
        const ready = await new Promise(resolve => {
            const timeout = setTimeout(() => resolve(false), 3000);
            channel.port1.onmessage = event => {
                clearTimeout(timeout);
                resolve(event.data?.ready === true);
            };
            try { worker.postMessage({ type: 'VAULTFLOW_CACHE_STATUS' }, [channel.port2]); }
            catch { clearTimeout(timeout); resolve(false); }
        });
        channel.port1.close();
        if (registration.active !== worker) return;
        announce(ready);
        if (ready) warning.hidden = true;
        else {
            showCacheWarning('Offline files are not ready. Connect and keep this page open while they download.');
            void repairCache(registration);
        }
    };

    // Registration can fail independently of app startup (for example, a tunnel
    // can return its welcome page for this browser-managed script request).
    if (window.isSecureContext && 'serviceWorker' in navigator) {
        navigator.serviceWorker.register('/service-worker.js', { scope: '/', updateViaCache: 'none' })
            .then(registration => {
                if (!registration) {
                    showCacheWarning('This browser is blocking offline storage. Allow service workers to download the app for offline use.');
                    return;
                }
                void checkCache(registration);
                navigator.serviceWorker.addEventListener('controllerchange', () => void checkCache(registration));
                window.addEventListener('online', () => void checkCache(registration));
                const watch = worker => worker?.addEventListener('statechange', () => {
                    if (worker.state === 'activated') void checkCache(registration);
                    if (worker.state === 'redundant' && !window.vaultFlowOffline.ready)
                        showCacheWarning('Offline download failed. Connect to the internet and reload to retry. If browser data was cleared, work saved only on this device may have been lost.');
                });
                watch(registration.installing);
                registration.addEventListener('updatefound', () => watch(registration.installing));
            })
            .catch(() => showCacheWarning('The app can open online, but offline files could not be saved. Reload after checking your connection and completing any website access notice.'));
    } else {
        showCacheWarning('Offline installation needs a trusted HTTPS address. Open VaultFlow using its HTTPS link to install and save work on this device.');
    }

    const slowDownload = setTimeout(() => {
        const status = document.getElementById('startup-status');
        if (status) status.textContent = 'The first download is still running. Keep this page open and check your Wi-Fi connection. You can return to the online workspace instead.';
    }, 15000);

    let startupFailed = false;
    const showStartupFailure = error => {
            const title = document.getElementById('startup-title');
            if (!title || startupFailed) return;
            startupFailed = true;
            clearTimeout(slowDownload);
            const status = document.getElementById('startup-status');
            const retry = document.getElementById('startup-retry');
            if (title) title.textContent = 'The app could not finish loading';
            if (status) status.textContent = 'Connect to the internet, complete any website access notice, then try again. If browser data was cleared, offline files and work saved only on this device may have been removed. Open VaultFlow online and sign in to prepare offline access again.';
            document.querySelector('.loading-progress')?.remove();
            document.querySelector('.loading-progress-text')?.remove();
            if (retry) { retry.hidden = false; retry.addEventListener('click', () => location.reload()); }
            console.error('VaultFlow startup failed:', error);
    };
    // Some runtime download failures are reported as unhandled rejections by
    // Blazor rather than rejecting its public start promise.
    const onRejection = event => showStartupFailure(event.reason);
    const onError = event => showStartupFailure(event.error || event.message);
    window.addEventListener('unhandledrejection', onRejection);
    window.addEventListener('error', onError);
    const rendered = new MutationObserver(() => {
        if (!document.getElementById('startup-title')) {
            clearTimeout(slowDownload);
            window.removeEventListener('unhandledrejection', onRejection);
            window.removeEventListener('error', onError);
            rendered.disconnect();
        }
    });
    rendered.observe(document.getElementById('app'), { childList: true, subtree: true });

    const start = async () => {
        try {
            if (!window.Blazor) throw new Error('The startup script did not load.');
            await window.Blazor.start();
        } catch (error) { showStartupFailure(error); }
    };
    void start();
})();
