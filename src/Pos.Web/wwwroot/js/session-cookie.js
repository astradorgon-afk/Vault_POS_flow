// Collects and clears the session cookie on behalf of a Blazor circuit, which
// has no HTTP response of its own to set one on. The token is one-use and
// short-lived; the cookie it buys is HttpOnly, so nothing here can read it.
window.vaultflowSession = {
    async begin(token) {
        await fetch('/auth/session', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'X-VaultFlow-Session': '1' },
            body: JSON.stringify({ token })
        });
    },
    async end() {
        await fetch('/auth/session/end', {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'X-VaultFlow-Session': '1' }
        });
    }
};
