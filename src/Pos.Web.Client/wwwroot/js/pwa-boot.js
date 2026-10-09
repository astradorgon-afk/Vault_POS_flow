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
    const checkCache = async registration => {
        if (!registration?.active) return;
        const channel = new MessageChannel();
        const ready = await new Promise(resolve => {
            const timeout = setTimeout(() => resolve(false), 3000);
            channel.port1.onmessage = event => {
                clearTimeout(timeout);
                resolve(event.data?.ready === true);
            };
            registration.active.postMessage({ type: 'VAULTFLOW_CACHE_STATUS' }, [channel.port2]);
        });
        channel.port1.close();
        announce(ready);
        if (ready) warning.hidden = true;
        else showCacheWarning('Offline files are not ready. Keep this page open while they download. A development build cannot be used offline.');
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
                const watch = worker => worker?.addEventListener('statechange', () => {
                    if (worker.state === 'activated') void checkCache(registration);
                    if (worker.state === 'redundant' && !window.vaultFlowOffline.ready)
                        showCacheWarning('Offline download failed. Check your connection and reload to retry. Your saved work has not been cleared.');
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
            if (status) status.textContent = 'Check your connection, complete any website access notice, then try again. Your saved work has not been cleared.';
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
