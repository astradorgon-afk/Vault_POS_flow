// Keyboard-wedge barcode scanners (USB, Bluetooth, and handhelds in keyboard
// mode) "type" a code in a few milliseconds and usually press Enter. This
// watches every key, groups fast bursts, and asks .NET whether a burst was a
// scanner. When it was, the characters it typed into whatever box had focus
// are taken back out, so a scan never lands in the wrong field.

let active = null;

export function start(dotnet, options) {
    stop();

    const state = { keys: [], target: null, before: null, timer: 0 };

    const reset = () => {
        clearTimeout(state.timer);
        state.keys = [];
        state.target = null;
        state.before = null;
    };

    // The same rule as KeyboardWedge.IsScan, needed synchronously to decide
    // whether to stop the Enter from submitting a form. .NET has the final word.
    const looksLikeScan = (keys, endAt) => {
        if (keys.length < options.minimumLength) { return false; }
        if (endAt - keys[keys.length - 1].t > options.maximumGapMilliseconds) { return false; }
        let total = 0;
        for (let i = 1; i < keys.length; i++) {
            const gap = keys[i].t - keys[i - 1].t;
            if (gap > options.maximumGapMilliseconds) { return false; }
            total += gap;
        }
        return total / (keys.length - 1) <= options.maximumAverageGapMilliseconds;
    };

    const finish = async () => {
        const keys = state.keys;
        const target = state.target;
        const before = state.before;
        reset();
        if (keys.length < options.minimumLength) { return; }

        let isScan = false;
        try {
            isScan = await dotnet.invokeMethodAsync('OnKeyboardBurst', keys.map(k => k.c).join(''), keys.map(k => k.t));
        } catch (_) {
            return; // The page is going away.
        }

        if (isScan && target && before && target.value !== before.value) {
            target.value = before.value;
            try { target.setSelectionRange(before.start, before.end); } catch (_) { /* not a text box */ }
            target.dispatchEvent(new Event('input', { bubbles: true }));
        }
    };

    const onKeyDown = (e) => {
        if (e.ctrlKey || e.altKey || e.metaKey || e.isComposing) { return; }
        const now = performance.now();

        if (e.key === 'Enter' || e.key === 'Tab') {
            if (state.keys.length > 0 && looksLikeScan(state.keys, now)) {
                e.preventDefault();
                e.stopPropagation();
                finish();
            } else {
                reset();
            }
            return;
        }

        // Shift arrives before every capital a scanner types; it is not a break.
        if (e.key === 'Shift' || e.key === 'CapsLock') { return; }
        if (e.key.length !== 1) { reset(); return; }

        const last = state.keys.length > 0 ? state.keys[state.keys.length - 1].t : null;
        if (last !== null && now - last > options.maximumGapMilliseconds) { reset(); }

        if (state.keys.length === 0) {
            const el = document.activeElement;
            if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA') && typeof el.value === 'string') {
                state.target = el;
                state.before = { value: el.value, start: el.selectionStart ?? 0, end: el.selectionEnd ?? 0 };
            }
        }

        state.keys.push({ c: e.key, t: now });

        // Scanners set to send no Enter still stop typing; a pause ends the burst.
        clearTimeout(state.timer);
        state.timer = setTimeout(finish, options.idleMilliseconds);
    };

    document.addEventListener('keydown', onKeyDown, true);
    active = { onKeyDown, reset };
}

export function stop() {
    if (active) {
        document.removeEventListener('keydown', active.onKeyDown, true);
        active.reset();
        active = null;
    }
}
