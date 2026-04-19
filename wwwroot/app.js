const API_BASE = '';

const urlInput = document.getElementById('url-input');
const shortenBtn = document.getElementById('shorten-btn');
const errorBox = document.getElementById('error-box');
const resultBox = document.getElementById('result-box');
const resultLink = document.getElementById('result-link');
const resultOriginal = document.getElementById('result-original');
const copyBtn = document.getElementById('copy-btn');
const refreshBtn = document.getElementById('refresh-btn');
const currentDate = document.getElementById('current-date');

// Set edition date
const now = new Date();
currentDate.textContent = now.toLocaleDateString('en-US', {
    weekday: 'long', year: 'numeric', month: 'long', day: 'numeric'
}).toUpperCase();

// ── SHORTEN ──
shortenBtn.addEventListener('click', shorten);
urlInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') shorten();
});

async function shorten() {
    const url = urlInput.value.trim();

    hideError();
    hideResult();

    if (!url) {
        showError('The address line cannot be left vacant, sir.');
        return;
    }

    if (!isValidUrl(url)) {
        showError('That does not appear to be a proper web address. Please include https://');
        return;
    }

    shortenBtn.textContent = '';
    shortenBtn.classList.add('loading-dots');
    shortenBtn.disabled = true;

    try {
        const res = await fetch(`${API_BASE}/shorten`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url })
        });

        if (!res.ok) {
            const text = await res.text();
            throw new Error(text || 'The telegraph office returned an error.');
        }

        const data = await res.json();

        resultLink.href = data.shortUrl;
        resultLink.textContent = data.shortUrl;
        resultOriginal.textContent = '↳ ' + url;
        showResult();
        fetchStats();

    } catch (err) {
        showError(err.message);
    } finally {
        shortenBtn.textContent = '— Dispatch —';
        shortenBtn.classList.remove('loading-dots');
        shortenBtn.disabled = false;
    }
}

// ── COPY ──
copyBtn.addEventListener('click', () => {
    const text = resultLink.href;
    navigator.clipboard.writeText(text).then(() => {
        copyBtn.textContent = '✓ Copied to Clipboard';
        setTimeout(() => { copyBtn.textContent = '⊕ Copy to Clipboard'; }, 2000);
    });
});

// ── STATS ──
async function fetchStats() {
    try {
        const res = await fetch(`${API_BASE}/stats`);
        if (!res.ok) return;
        const data = await res.json();

        document.getElementById('stat-hits').textContent =
            data.cacheHits.toLocaleString();
        document.getElementById('stat-misses').textContent =
            data.cacheMisses.toLocaleString();
        document.getElementById('stat-hitrate').textContent =
            data.hitRatePercent.toFixed(1) + '%';

    } catch (err) {
        console.error('Stats fetch failed:', err);
    }
}

refreshBtn.addEventListener('click', fetchStats);

// ── HELPERS ──
function showError(msg) {
    errorBox.textContent = '⚠ ' + msg;
    errorBox.classList.add('visible');
}

function hideError() {
    errorBox.classList.remove('visible');
    errorBox.textContent = '';
}

function showResult() {
    resultBox.classList.add('visible');
}

function hideResult() {
    resultBox.classList.remove('visible');
}

function isValidUrl(str) {
    try {
        const u = new URL(str);
        return u.protocol === 'http:' || u.protocol === 'https:';
    } catch {
        return false;
    }
}

// Load stats on page load
fetchStats();