// Uploads for the share page. The browser's XMLHttpRequest does the sending: fetch reports nothing about a body on
// its way out, while XHR streams a File off the disk and says how far it got - so the bytes never pass through .NET.

const active = new Map();
const claimed = new Map();
let nextClaim = 0;

function claim(files) {
    const key = 'c' + (nextClaim++);
    claimed.set(key, files.map(x => x.file));
    return {
        key,
        files: files.map((x, index) => ({ index, name: x.file.name, size: x.file.size, dir: x.dir }))
    };
}

/** The files picked in an <input type=file> (cleared, so picking the same file again still works). */
export function claimInput(input) {
    const files = Array.from(input.files || []).map(file => ({ file, dir: '' }));
    input.value = '';
    return claim(files);
}

export function release(key) {
    claimed.delete(key);
}

// ---------------------------------------------------------------------------------------------- drag and drop

async function readEntries(reader) {
    const all = [];
    // a directory reader hands its entries over in batches until it returns none
    for (;;) {
        const batch = await new Promise((resolve, reject) => reader.readEntries(resolve, reject));
        if (batch.length === 0) return all;
        all.push(...batch);
    }
}

async function walk(entry, dir, out) {
    if (entry.isFile) {
        const file = await new Promise((resolve, reject) => entry.file(resolve, reject));
        out.push({ file, dir });
    } else if (entry.isDirectory) {
        const sub = dir ? `${dir}/${entry.name}` : entry.name;
        for (const child of await readEntries(entry.createReader()))
            await walk(child, sub, out);
    }
}

/** Whole-window drag and drop: folders are walked, and each file keeps the folder it was in. */
export function watchDrops(dotnet) {
    let depth = 0;
    const hasFiles = e => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');

    window.addEventListener('dragenter', e => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        if (depth++ === 0) dotnet.invokeMethodAsync('OnDragging', true);
    });
    window.addEventListener('dragover', e => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'copy';
    });
    window.addEventListener('dragleave', e => {
        if (!hasFiles(e)) return;
        if (--depth <= 0) {
            depth = 0;
            dotnet.invokeMethodAsync('OnDragging', false);
        }
    });
    window.addEventListener('drop', async e => {
        if (!hasFiles(e)) return;
        e.preventDefault();
        depth = 0;
        dotnet.invokeMethodAsync('OnDragging', false);

        const items = Array.from(e.dataTransfer.items || []);
        const entries = items.map(x => x.webkitGetAsEntry ? x.webkitGetAsEntry() : null);
        const out = [];
        if (entries.every(x => x)) {
            for (const entry of entries)
                await walk(entry, '', out);
        } else {
            for (const file of Array.from(e.dataTransfer.files || []))
                out.push({ file, dir: '' });
        }
        if (out.length > 0)
            dotnet.invokeMethodAsync('OnDropped', claim(out));
    });
}

// ---------------------------------------------------------------------------------------------- one request

function throttled(dotnet) {
    let last = 0;
    return (loaded, total) => {
        const now = performance.now();
        if (now - last < 150 && loaded < total) return;
        last = now;
        dotnet.invokeMethodAsync('OnProgress', loaded, total);
    };
}

/** A file sent as the body of one PUT. Resolves with {status, body}: 0 for a lost connection, -1 for a cancel. */
export function put(id, key, index, url, dotnet) {
    const file = (claimed.get(key) || [])[index];
    if (!file) return Promise.resolve({ status: 400, body: '' });

    const report = throttled(dotnet);
    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        active.set(id, { abort: () => xhr.abort() });
        const done = (status, body) => {
            active.delete(id);
            resolve({ status, body });
        };

        xhr.upload.onprogress = e => report(e.loaded, e.total);
        xhr.onload = () => done(xhr.status, xhr.responseText);
        xhr.onerror = () => done(0, '');
        xhr.onabort = () => done(-1, '');
        xhr.open('PUT', url);
        xhr.setRequestHeader('Content-Type', 'application/octet-stream');
        xhr.send(file);
    });
}

export function abort(id) {
    const job = active.get(id);
    if (job) job.abort();
}

// ---------------------------------------------------------------------------------------------- resumable (tus 1.0.0)
//
// A big file goes up as PATCHes against an upload the server creates for it. A dropped connection costs at most the
// chunk in flight: the upload waits, asks the server how far it got and carries on. The upload's address is kept in
// this browser too, so picking the same file again after a reload resumes it.

const RETRY_DELAYS = [1, 2, 4, 8, 15, 30, 30, 60, 60, 60];
const RETRYABLE = new Set([0, 408, 423, 500, 502, 503, 504]);
const REMEMBERED = 'shinycommander.uploads';

function remembered() {
    try { return JSON.parse(localStorage.getItem(REMEMBERED) || '{}'); } catch { return {}; }
}

function remember(fingerprint, url) {
    try {
        const map = remembered();
        if (url) map[fingerprint] = url; else delete map[fingerprint];
        localStorage.setItem(REMEMBERED, JSON.stringify(map));
    } catch { /* private window: still resumes, just not across a reload */ }
}

function base64(text) {
    let binary = '';
    for (const byte of new TextEncoder().encode(text)) binary += String.fromCharCode(byte);
    return btoa(binary);
}

function message(body) {
    if (!body) return '';
    try {
        const parsed = JSON.parse(body);
        return parsed.message || parsed.detail || parsed.title || '';
    } catch {
        return body;
    }
}

function request(job, method, url, headers, body, onProgress) {
    return new Promise(resolve => {
        const xhr = new XMLHttpRequest();
        job.xhr = xhr;
        if (onProgress) xhr.upload.onprogress = e => onProgress(e.loaded);
        const done = status => {
            job.xhr = null;
            resolve({ status, body: status > 0 ? xhr.responseText : '', header: n => (status > 0 ? xhr.getResponseHeader(n) : null) });
        };
        xhr.onload = () => done(xhr.status);
        xhr.onerror = () => done(0);
        xhr.onabort = () => done(-1);
        xhr.open(method, url);
        xhr.setRequestHeader('Tus-Resumable', '1.0.0');
        for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);
        xhr.send(body);
    });
}

function sleep(job, seconds) {
    return new Promise(resolve => {
        const timer = setTimeout(resolve, seconds * 1000);
        job.wake = () => { clearTimeout(timer); resolve(); };
    });
}

async function offsetOf(job) {
    const r = await request(job, 'HEAD', job.url, {}, null);
    if (r.status === 200 || r.status === 204) return { status: r.status, offset: parseInt(r.header('Upload-Offset'), 10) };
    return { status: r.status, offset: -1 };
}

/** Sends one file over tus. Resolves like put(), plus savedAs: where it landed (a taken name gets " (2)"). */
export async function tus(id, key, index, endpoint, metadata, chunkSize, dotnet) {
    const file = (claimed.get(key) || [])[index];
    if (!file) return { status: 400, body: 'The file is no longer available. Pick it again.' };

    const job = { xhr: null, wake: null, cancelled: false, url: null };
    active.set(id, { abort: () => { job.cancelled = true; if (job.xhr) job.xhr.abort(); if (job.wake) job.wake(); } });
    const report = throttled(dotnet);
    const status = text => dotnet.invokeMethodAsync('OnStatus', text);
    const fingerprint = [endpoint, metadata.path, file.name, file.size, file.lastModified].join('|');
    const finish = result => { active.delete(id); return result; };
    const cancelled = async () => {
        if (job.url) await request({ xhr: null }, 'DELETE', job.url, {}, null);
        remember(fingerprint, null);
        return finish({ status: -1, body: '' });
    };

    job.url = remembered()[fingerprint] || null;
    let offset = -1;
    if (job.url) {
        offset = (await offsetOf(job)).offset;
        if (offset < 0) { remember(fingerprint, null); job.url = null; }
    }
    if (job.cancelled) return cancelled();

    if (offset < 0) {
        const pairs = Object.entries(metadata).map(([k, v]) => `${k} ${base64(String(v))}`);
        const created = await request(job, 'POST', endpoint, { 'Upload-Length': String(file.size), 'Upload-Metadata': pairs.join(',') }, null);
        if (job.cancelled) return cancelled();
        if (created.status !== 201) return finish({ status: created.status, body: message(created.body) });

        // the path alone: behind a tunnel the host the server sees isn't the one this page came from
        job.url = new URL(created.header('Location'), window.location.href).pathname;
        remember(fingerprint, job.url);
        offset = 0;
    }

    let attempt = 0;
    while (offset < file.size) {
        const from = offset;
        const end = Math.min(from + chunkSize, file.size);
        const r = await request(job, 'PATCH', job.url,
            { 'Upload-Offset': String(from), 'Content-Type': 'application/offset+octet-stream' },
            file.slice(from, end), loaded => report(from + loaded, file.size));
        if (job.cancelled) return cancelled();

        if (r.status === 204) {
            offset = parseInt(r.header('Upload-Offset'), 10);
            report(offset, file.size);
            if (attempt > 0) { attempt = 0; status(null); }
            if (offset >= file.size) {
                remember(fingerprint, null);
                const saved = r.header('Upload-Saved-As');
                return finish({ status: 200, body: '', savedAs: saved ? decodeURIComponent(saved) : null });
            }
        } else if (r.status === 409 || RETRYABLE.has(r.status)) {
            attempt++;
            if (attempt > RETRY_DELAYS.length)
                return finish({ status: 0, body: 'The connection was lost and did not come back. Pick the file again to resume.' });
            if (r.status !== 409) {
                status(`Reconnecting… (${attempt} of ${RETRY_DELAYS.length})`);
                await sleep(job, RETRY_DELAYS[attempt - 1]);
                if (job.cancelled) return cancelled();
            }
            const known = await offsetOf(job);
            if (job.cancelled) return cancelled();
            if (known.offset >= 0) {
                offset = known.offset;
                report(offset, file.size);
            } else if (!RETRYABLE.has(known.status)) {
                remember(fingerprint, null);
                return finish({ status: known.status, body: 'The upload expired on the server. Start it again.' });
            }
            if (offset >= file.size) {
                remember(fingerprint, null);
                return finish({ status: 0, body: 'The file arrived, but the connection dropped before it was confirmed. Refresh to check.' });
            }
        } else {
            remember(fingerprint, null);
            return finish({ status: r.status, body: message(r.body) });
        }
    }
    remember(fingerprint, null);
    return finish({ status: 200, body: '', savedAs: null });
}

// ---------------------------------------------------------------------------------------------- small things

export function copy(text) {
    return navigator.clipboard ? navigator.clipboard.writeText(text).then(() => true, () => false) : Promise.resolve(false);
}

export function replaceUrl(url) {
    history.replaceState(history.state, '', url);
}

export function focus(element) {
    if (element) { element.focus(); if (element.select) element.select(); }
}
