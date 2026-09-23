// Only the public production site sends basic visits and performance metrics.
// The beacon token is a public site identifier, not an account API credential.
if (import.meta.env.PROD && window.location.hostname === 'zzz-pattern-practice.github.io') {
  const beacon = document.createElement('script');
  beacon.type = 'module';
  beacon.src = 'https://static.cloudflareinsights.com/beacon.min.js';
  beacon.dataset.cfBeacon = JSON.stringify({ token: 'f320d0f1bef643efa91e0b45b6a7edb0' });
  document.body.appendChild(beacon);
}
