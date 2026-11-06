const http = require('http');

const locationId = '3344f205-4478-44f5-aa2e-a23e98bb09bf';
const deviceId = '3b690355-7f10-45ea-8968-c8491cbb6431';
const userId = '709c758d-f123-4d5b-a519-6619a11ca797';
const expiry = new Date(Date.now() + 60 * 60 * 1000).toISOString();
const port = Number(process.argv[2] || 5188);

http.createServer(async (request, response) => {
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  const body = Buffer.concat(chunks).toString('utf8');
  const path = new URL(request.url, 'http://localhost').pathname;
  let value;
  let status = 200;
  if (path === '/api/v1/locations/sign-in')
    value = [{ id: locationId, code: 'PILOT', name: 'Pilot store', kind: 1 }];
  else if (path === '/api/v1/auth/login') {
    const credentials = JSON.parse(body);
    if (credentials.userName !== 'pilot' || credentials.password !== 'account-password') {
      status = 401;
      value = { detail: 'Invalid credentials.' };
    } else value = {
      accessToken: 'test-access-token', accessTokenExpiresAtUtc: expiry,
      refreshToken: 'test-refresh-token', refreshTokenExpiresAtUtc: expiry,
      user: { userId, displayName: 'Pilot User', roles: [],
        permissions: ['purchase.create', 'inventory.view'], locations: [locationId],
        hasAllLocations: false, policyVersion: 1 },
    };
  }
  else if (path === '/api/v1/devices/enrol') {
    if (JSON.parse(body).enrolmentCode !== 'pilot-enrolment') {
      status = 400;
      value = { detail: 'Invalid enrollment code.' };
    } else value = { deviceId, locationId };
  }
  else if (path === `/api/v1/devices/${deviceId}/locations`)
    value = { defaultLocationId: locationId, allowedLocationIds: [locationId] };
  else if (path === '/api/v1/sync/baseline')
    value = { cursor: 1, items: [] };
  else if (path === '/api/v1/inventory/stock-levels')
    value = { locationId, name: 'Pilot store', products: [] };
  else if (/^\/api\/v1\/catalog\/(categories|units|suppliers|products)$/.test(path) ||
           path === '/api/v1/purchasing/orders')
    value = [];
  else {
    status = 404;
    value = { detail: `No fixture for ${path}.` };
  }
  response.writeHead(status, { 'Content-Type': 'application/json' });
  response.end(JSON.stringify(value));
}).listen(port, '127.0.0.1', () => console.log(`Fake preparation API on ${port}`));
