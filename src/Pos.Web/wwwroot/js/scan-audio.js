(() => {
    if (window.vaultflowScanBeep) { return; }

    let audioContext;
    const AudioContextType = window.AudioContext || window.webkitAudioContext;

    // A native click handler keeps the browser's user gesture for mobile audio.
    document.addEventListener('click', event => {
        if (!event.target?.closest?.('.camera-open') || !AudioContextType) { return; }
        try {
            audioContext ??= new AudioContextType();
            if (audioContext.state !== 'running') {
                void audioContext.resume().catch(() => {});
            }
        } catch { /* Barcode scanning still works when audio is unavailable. */ }
    }, true);

    window.vaultflowScanBeep = () => {
        if (!audioContext) { return; }
        try {
            if (audioContext.state !== 'running') {
                void audioContext.resume().catch(() => {});
                return;
            }

            const now = audioContext.currentTime;
            const tone = audioContext.createOscillator();
            const volume = audioContext.createGain();
            tone.type = 'sine';
            tone.frequency.setValueAtTime(1250, now);
            tone.frequency.linearRampToValueAtTime(1550, now + 0.1);
            volume.gain.setValueAtTime(0.0001, now);
            volume.gain.exponentialRampToValueAtTime(0.16, now + 0.006);
            volume.gain.exponentialRampToValueAtTime(0.0001, now + 0.145);
            tone.connect(volume).connect(audioContext.destination);
            tone.onended = () => { tone.disconnect(); volume.disconnect(); };
            tone.start(now);
            tone.stop(now + 0.15);
        } catch { /* A sound failure must not interrupt scanning. */ }
    };
})();
