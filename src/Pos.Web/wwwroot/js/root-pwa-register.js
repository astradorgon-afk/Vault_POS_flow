(() => {
    if (!window.isSecureContext || !('serviceWorker' in navigator)) return;
    navigator.serviceWorker.register('/service-worker.js', { scope: '/', updateViaCache: 'none' })
        .then(async registration => {
            await navigator.serviceWorker.ready;
            const old = await navigator.serviceWorker.getRegistrations();
            await Promise.all(old.filter(item => item.scope.endsWith('/offline/') && item !== registration)
                .map(item => item.unregister()));
        })
        .catch(error => console.warn('Offline files could not be prepared:', error));
})();
