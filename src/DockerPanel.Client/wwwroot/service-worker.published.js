// Caution! Be sure you understand the caveats before using an offline-first service worker.
// See https://aka.ms/blazor-offline-first for details.

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);
    if (url.protocol === 'http:' || url.protocol === 'https:') {
        event.respondWith(onFetch(event));
    }
});

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.svg$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Only cache resources that match the list of assets in service-worker-assets.js
async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching assets from the manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { cache: 'no-cache' }));
    
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete old caches
    const cacheKeys = await caches.keys();
    await Promise.all(
        cacheKeys.filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
                 .map(key => caches.delete(key))
    );

    // Take control of all pages/clients immediately
    await self.clients.claim();
}

async function onFetch(event) {
    // API veya indirme linklerini Service Worker intercept etmesin, doğrudan network'e gitsin
    if (event.request.url.indexOf('/api/') !== -1) {
        try {
            return await fetch(event.request);
        } catch (err) {
            console.warn('API Fetch failed:', event.request.url, err);
            return Response.error();
        }
    }

    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache
        const shouldServeIndexHtml = event.request.mode === 'navigate';

        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        try {
            const cache = await caches.open(cacheName);
            cachedResponse = await cache.match(request);
        } catch (err) {
            console.warn('Cache match failed:', err);
        }
    }

    try {
        return cachedResponse || await fetch(event.request);
    } catch (err) {
        console.warn('Fetch fallback failed for:', event.request.url, err);
        return cachedResponse || Response.error();
    }
}

self.addEventListener('message', event => {
    if (event.data && event.data.action === 'skipWaiting') {
        self.skipWaiting();
    }
});
