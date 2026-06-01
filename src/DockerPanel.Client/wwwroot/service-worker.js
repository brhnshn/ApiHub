// In development, always fetch from the network and do not cache resources.
// This allows you to see your changes immediately.
self.addEventListener('install', event => event.waitUntil(self.skipWaiting()));
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
    const url = new URL(event.request.url);
    if (url.protocol === 'http:' || url.protocol === 'https:') {
        event.respondWith(fetch(event.request));
    }
});
