const sessions = new WeakMap();
// Wide 1D codes can fail at the camera's native resolution; try several scales.
const scanWidths = [640, 960, 640, 480];

async function scanFrame(video, dotnet, session) {
    if (session.closed) { return; }

    if (video.readyState >= HTMLMediaElement.HAVE_CURRENT_DATA && video.videoWidth && video.videoHeight) {
        const width = video.videoWidth;
        const height = video.videoHeight;
        const frame = session.frame++ % scanWidths.length;
        const centerCrop = frame === 2;
        const sourceWidth = centerCrop ? Math.round(width * 0.65) : width;
        const sourceHeight = centerCrop ? Math.round(height * 0.65) : height;
        const sourceX = Math.round((width - sourceWidth) / 2);
        const sourceY = Math.round((height - sourceHeight) / 2);
        const scale = Math.min(1, scanWidths[frame] / sourceWidth);
        const canvas = session.canvas;
        canvas.width = Math.max(1, Math.round(sourceWidth * scale));
        canvas.height = Math.max(1, Math.round(sourceHeight * scale));
        try {
            session.context.drawImage(video, sourceX, sourceY, sourceWidth, sourceHeight,
                0, 0, canvas.width, canvas.height);
            const result = await session.reader.decodeFromCanvas(canvas);
            const code = result.getText()?.trim();
            if (code && !session.delivering && code !== session.lastCode && !session.closed) {
                session.lastCode = code;
                session.delivering = true;
                dotnet.invokeMethodAsync('OnCameraBarcode', code)
                    .catch(() => { /* The page may have closed while a scan was in flight. */ })
                    .finally(() => { session.delivering = false; });
            }
        } catch { /* A frame without a readable barcode is normal. */ }
    }

    if (!session.closed) {
        session.timer = setTimeout(() => scanFrame(video, dotnet, session), 180);
    }
}

export async function start(video, dotnet, deviceId = null) {
    stop(video);

    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
        return { error: 'Camera scanning needs HTTPS or localhost in a browser with camera access.' };
    }

    const session = {
        closed: false,
        reader: null,
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
        await import('../lib/zxing/zxing-browser.min.js');
        const { BrowserMultiFormatReader, BarcodeFormat } = globalThis.ZXingBrowser;
        const reader = new BrowserMultiFormatReader();
        reader.possibleFormats = [
            BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
            BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
            BarcodeFormat.CODE_128, BarcodeFormat.CODE_39,
            BarcodeFormat.CODE_93, BarcodeFormat.ITF,
            BarcodeFormat.QR_CODE,
        ];
        session.reader = reader;

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
