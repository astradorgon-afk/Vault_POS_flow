const sessions = new WeakMap();
let decoderPromise;
const decoderScript = new URL('../lib/zxing-wasm/reader.js', import.meta.url).href;
const decoderBinary = new URL('../lib/zxing-wasm/zxing_reader.wasm', import.meta.url).href;
const readerOptions = {
    formats: ['EAN13', 'EAN8', 'UPCA', 'UPCE', 'Code128', 'Code39', 'Code93', 'ITF', 'QRCode'],
    maxNumberOfSymbols: 1,
    tryHarder: true,
    tryRotate: true,
    tryInvert: true,
};

function loadDecoder() {
    decoderPromise ??= new Promise((resolve, reject) => {
        if (globalThis.ZXingWASM) { resolve(globalThis.ZXingWASM); return; }
        const script = document.createElement('script');
        script.src = decoderScript;
        script.onload = () => resolve(globalThis.ZXingWASM);
        script.onerror = () => reject(new Error('The barcode reader could not load.'));
        document.head.appendChild(script);
    }).then(async decoder => {
        if (!decoder?.readBarcodes) { throw new Error('The barcode reader is unavailable.'); }
        await decoder.prepareZXingModule({
            overrides: { locateFile: () => decoderBinary },
            fireImmediately: true,
        });
        return decoder;
    }).catch(error => {
        decoderPromise = null;
        throw error;
    });
    return decoderPromise;
}

// Alternate wide and close views so both large and small labels are found quickly.
const scanPlans = [
    { width: 960, crop: 1 },
    { width: 960, crop: 0.65 },
    { width: 640, crop: 1 },
    { width: 960, crop: 0.45 },
];

async function scanFrame(video, dotnet, session) {
    if (session.closed) { return; }

    if (!session.delivering && video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA && video.videoWidth && video.videoHeight) {
        const width = video.videoWidth;
        const height = video.videoHeight;
        const plan = scanPlans[session.frame++ % scanPlans.length];
        const sourceWidth = Math.round(width * plan.crop);
        const sourceHeight = Math.round(height * plan.crop);
        const sourceX = Math.round((width - sourceWidth) / 2);
        const sourceY = Math.round((height - sourceHeight) / 2);
        const scale = Math.min(1, plan.width / sourceWidth);
        const drawnWidth = Math.max(1, Math.round(sourceWidth * scale));
        const drawnHeight = Math.max(1, Math.round(sourceHeight * scale));
        const canvas = session.canvas;
        if (canvas.width !== drawnWidth) { canvas.width = drawnWidth; }
        if (canvas.height !== drawnHeight) { canvas.height = drawnHeight; }
        try {
            session.context.drawImage(video, sourceX, sourceY, sourceWidth, sourceHeight,
                0, 0, drawnWidth, drawnHeight);
            const image = session.context.getImageData(0, 0, drawnWidth, drawnHeight);
            const results = await session.decoder.readBarcodes(image, readerOptions);
            const code = results.find(result => result.text?.trim())?.text.trim();
            if (code && !session.delivering && code !== session.lastCode && !session.closed) {
                session.lastCode = code;
                session.delivering = true;
                dotnet.invokeMethodAsync('OnCameraBarcode', code)
                    .then(() => window.vaultflowScanBeep?.())
                    .catch(() => {
                        // A temporary connection failure must not block this code forever.
                        if (!session.closed && session.lastCode === code) { session.lastCode = null; }
                    })
                    .finally(() => { session.delivering = false; });
            }
        } catch { /* A frame without a readable barcode is normal. */ }
    }

    if (!session.closed) {
        session.timer = setTimeout(() => scanFrame(video, dotnet, session), 60);
    }
}

export async function start(video, dotnet, deviceId = null) {
    stop(video);

    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        return { error: 'Camera scanning needs HTTPS or localhost in a browser with camera access.' };
    }

    const session = {
        closed: false,
        decoder: null,
        canvas: document.createElement('canvas'),
        context: null,
        frame: 0,
        timer: null,
        track: null,
        lastCode: null,
        delivering: false,
    };
    session.context = session.canvas.getContext('2d', { willReadFrequently: true });
    sessions.set(video, session);

    try {
        const videoConstraints = deviceId
            ? { deviceId: { exact: deviceId } }
            : { facingMode: { ideal: 'environment' } };
        videoConstraints.width = { ideal: 1920 };
        videoConstraints.height = { ideal: 1080 };
        const stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: videoConstraints });

        if (session.closed) {
            stream.getTracks().forEach(track => track.stop());
            return { error: 'The camera was stopped.' };
        }

        video.srcObject = stream;
        await video.play();
        try {
            session.decoder = await loadDecoder();
        } catch {
            throw new Error('The barcode reader could not load. Refresh the page and try again.');
        }
        if (session.closed) { return { error: 'The camera was stopped.' }; }
        session.track = stream.getVideoTracks()[0] ?? null;
        const track = session.track;
        const capabilities = track?.getCapabilities?.() ?? {};
        if (capabilities.focusMode?.includes('continuous')) {
            try {
                await track.applyConstraints({ advanced: [{ focusMode: 'continuous' }] });
            } catch { /* Focus controls vary by browser and camera. */ }
        }

        void scanFrame(video, dotnet, session);

        let cameras = [];
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            cameras = devices.filter(device => device.kind === 'videoinput' && device.deviceId)
                .map((device, index) => ({
                    id: device.deviceId,
                    label: device.label || `Camera ${index + 1}`,
                }));
        } catch { /* Scanning still works if device enumeration is unavailable. */ }

        const zoom = capabilities.zoom;
        return {
            cameras,
            selectedDeviceId: track?.getSettings?.().deviceId ?? deviceId,
            canTorch: Boolean(capabilities.torch),
            minZoom: zoom?.min ?? null,
            maxZoom: zoom?.max ?? null,
            zoom: track?.getSettings?.().zoom ?? zoom?.min ?? null,
        };
    } catch (error) {
        stop(video);
        if (error?.message?.startsWith('The barcode reader')) {
            return { error: error.message };
        }
        if (error?.name === 'NotAllowedError' || error?.name === 'PermissionDeniedError') {
            return { error: 'Camera access was denied. Allow camera access in the browser and try again.' };
        }
        if (error?.name === 'NotFoundError' || error?.name === 'DevicesNotFoundError') {
            return { error: 'No camera was found on this device.' };
        }
        if (error?.name === 'OverconstrainedError') {
            return { error: 'That camera is unavailable. Try another camera.' };
        }
        return { error: 'The camera could not start. Check that another app is not using it, then try again.' };
    }
}

export function rearm(video) {
    const session = sessions.get(video);
    if (session && !session.closed) { session.lastCode = null; }
}

export async function setTorch(video, enabled) {
    const track = sessions.get(video)?.track;
    if (!track?.getCapabilities?.().torch) { return false; }
    try {
        await track.applyConstraints({ advanced: [{ torch: enabled }] });
        return true;
    } catch { return false; }
}

export async function setZoom(video, value) {
    const track = sessions.get(video)?.track;
    const range = track?.getCapabilities?.().zoom;
    if (!range) { return null; }
    const zoom = Math.min(range.max, Math.max(range.min, value));
    try {
        await track.applyConstraints({ advanced: [{ zoom }] });
        return track.getSettings().zoom ?? zoom;
    } catch { return null; }
}

export function stop(video) {
    const session = sessions.get(video);
    if (!session) { return; }

    session.closed = true;
    clearTimeout(session.timer);
    video.srcObject?.getTracks().forEach(track => track.stop());
    video.srcObject = null;
    sessions.delete(video);
}
