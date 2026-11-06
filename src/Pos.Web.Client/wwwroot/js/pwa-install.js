(() => {
    let installPrompt = null;

    const installed = () => window.matchMedia('(display-mode: standalone)').matches ||
        navigator.standalone === true;
    const installRecorded = () => {
        try { return localStorage.getItem('vaultflow-app-installed') === 'true'; }
        catch { return false; }
    };
    const rememberInstall = () => {
        try { localStorage.setItem('vaultflow-app-installed', 'true'); }
        catch { /* Installation still works when storage is unavailable. */ }
    };

    window.addEventListener('beforeinstallprompt', event => {
        event.preventDefault();
        installPrompt = event;
    });

    window.addEventListener('appinstalled', () => {
        installPrompt = null;
        rememberInstall();
        showResult('installed');
    });

    function showResult(outcome) {
        const status = document.getElementById('offline-install-status');
        const steps = document.getElementById('offline-install-steps');
        if (status) {
            status.textContent = {
                accepted: 'Installation accepted. VaultFlow will appear in your apps when your browser finishes.',
                installed: 'VaultFlow is installed on this device.',
                dismissed: 'Installation was canceled. You can try again from your browser menu.',
                instructions: 'This browser did not offer an install prompt. Use the browser menu below.',
                error: 'The browser could not open an install prompt. Use the browser menu below.'
            }[outcome] || '';
        }
        if (steps) steps.hidden = outcome === 'installed' || outcome === 'accepted';
    }

    document.addEventListener('click', event => {
        const button = event.target.closest('[data-vaultflow-install]');
        if (!button) return;
        event.preventDefault();
        // Run directly from the click so the browser keeps its user activation.
        window.vaultFlowInstall.prompt().then(showResult).catch(() => showResult('error'));
    }, true);

    window.vaultFlowInstall = {
        isInstalled: () => installed() || installRecorded(),
        async prompt() {
            if (installed() || installRecorded()) return 'installed';
            if (!installPrompt) return 'instructions';

            const event = installPrompt;
            installPrompt = null;
            const choice = await event.prompt();
            return choice?.outcome === 'accepted' ? 'accepted' : 'dismissed';
        }
    };
})();
