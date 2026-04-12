const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

const configPath = path.join(__dirname, 'services.json');
const exampleConfigPath = path.join(__dirname, 'services.example.json');

function readConfig() {
  const sourcePath = fs.existsSync(configPath) ? configPath : exampleConfigPath;
  return JSON.parse(fs.readFileSync(sourcePath, 'utf8'));
}

let config = readConfig();
let incidents = [];
let serviceState = new Map();
let latestSnapshot = buildSnapshot([], 'loading');

function nowIso() {
  return new Date().toISOString();
}

function normalizeStatus(statusCode, expectedStatus) {
  if (statusCode === expectedStatus) return 'operational';
  if (statusCode > 0 && statusCode < 500) return 'degraded';
  return 'outage';
}

function overallStatus(services) {
  if (services.some((item) => item.status === 'outage')) return 'outage';
  if (services.some((item) => item.status === 'degraded')) return 'degraded';
  return 'operational';
}

function averageUptime30d(services) {
  if (!services.length) return null;

  const sum = services.reduce((total, item) => {
    const uptime = Number(item.uptime30d);
    return total + (Number.isFinite(uptime) ? uptime : 100);
  }, 0);

  return Number((sum / services.length).toFixed(2));
}

function buildSnapshot(services, statusOverride) {
  const overall = statusOverride || overallStatus(services);
  return {
    updatedAt: nowIso(),
    overall: {
      title: config.site.title,
      message: config.site.message,
      status: overall,
      uptime30d: averageUptime30d(services)
    },
    services,
    incidents: incidents.slice(0, 10)
  };
}

function normalizeAudience(value) {
  const audience = String(value || '').trim().toLowerCase();
  return audience || null;
}

function serviceAudience(service) {
  if (service.public === false) {
    return 'internal';
  }
  return normalizeAudience(service.audience || service.visibility) || 'public';
}

function filterSnapshot(snapshot, audience) {
  const normalizedAudience = normalizeAudience(audience);
  if (!normalizedAudience) {
    return JSON.parse(JSON.stringify(snapshot));
  }

  const services = (snapshot.services || [])
    .filter((service) => serviceAudience(service) === normalizedAudience)
    .map((service) => ({ ...service }));
  const visibleIds = new Set(services.map((service) => service.id).filter(Boolean));
  const visibleIncidents = (snapshot.incidents || [])
    .filter((incident) => Array.from(visibleIds).some((serviceId) => String(incident.id || '').startsWith(`${serviceId}-`)))
    .map((incident) => ({ ...incident }));

  const filtered = buildSnapshot(services);
  filtered.updatedAt = snapshot.updatedAt || filtered.updatedAt;
  filtered.incidents = visibleIncidents;
  return filtered;
}

function recordIncident(service, previousStatus, nextStatus) {
  incidents.unshift({
    id: `${service.id}-${Date.now()}`,
    title: `${service.name} 状态变化：${previousStatus} -> ${nextStatus}`,
    summary: `探测接口 ${service.probeUrl} 的返回结果发生变化。`,
    impact: nextStatus === 'outage' ? 'major' : 'minor',
    status: nextStatus === 'operational' ? 'resolved' : 'investigating',
    startedAt: nowIso(),
    resolvedAt: nextStatus === 'operational' ? nowIso() : null,
    updates: [
      {
        at: nowIso(),
        message: `自动探测记录到状态变化，当前状态为 ${nextStatus}。`
      }
    ]
  });

  incidents = incidents.slice(0, 20);
}

async function probeService(service) {
  const controller = new AbortController();
  const startedAt = Date.now();
  const timeout = setTimeout(() => controller.abort(), service.timeoutMs || 5000);

  try {
    const response = await fetch(service.probeUrl, {
      method: service.method || 'GET',
      signal: controller.signal,
      headers: { 'user-agent': 'voicelinkvr-status-probe/1.0.0' }
    });

    const responseTimeMs = Date.now() - startedAt;
    const status = normalizeStatus(response.status, service.expectedStatus || 200);
    const history = serviceState.get(service.id) || { checks: 0, success: 0, status: 'loading' };
    history.checks += 1;
    if (status === 'operational') history.success += 1;
    if (history.status !== 'loading' && history.status !== status) {
      recordIncident(service, history.status, status);
    }
    history.status = status;
    serviceState.set(service.id, history);

    return {
      id: service.id,
      name: service.name,
      description: service.description,
      audience: service.audience || service.visibility || (service.public === false ? 'internal' : 'public'),
      public: service.public,
      status,
      region: service.region || '--',
      responseTimeMs,
      uptime30d: Number(((history.success / history.checks) * 100).toFixed(2)),
      lastCheckedAt: nowIso(),
      publicEndpoint: service.publicEndpoint || service.probeUrl
    };
  } catch (error) {
    const history = serviceState.get(service.id) || { checks: 0, success: 0, status: 'loading' };
    history.checks += 1;
    if (history.status !== 'loading' && history.status !== 'outage') {
      recordIncident(service, history.status, 'outage');
    }
    history.status = 'outage';
    serviceState.set(service.id, history);

    return {
      id: service.id,
      name: service.name,
      description: `${service.description} (${error.name || 'Error'})`,
      audience: service.audience || service.visibility || (service.public === false ? 'internal' : 'public'),
      public: service.public,
      status: 'outage',
      region: service.region || '--',
      responseTimeMs: null,
      uptime30d: Number(((history.success / history.checks) * 100).toFixed(2)),
      lastCheckedAt: nowIso(),
      publicEndpoint: service.publicEndpoint || service.probeUrl
    };
  } finally {
    clearTimeout(timeout);
  }
}

async function refreshSnapshot() {
  config = readConfig();
  const services = await Promise.all(config.services.map(probeService));
  latestSnapshot = buildSnapshot(services);
}

function writeJson(response, payload, origin) {
  response.writeHead(200, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    'access-control-allow-origin': origin,
    'access-control-allow-methods': 'GET, OPTIONS'
  });
  response.end(JSON.stringify(payload, null, 2));
}

function writeNotFound(response, origin) {
  response.writeHead(404, {
    'content-type': 'application/json; charset=utf-8',
    'access-control-allow-origin': origin
  });
  response.end(JSON.stringify({ error: 'Not Found' }));
}

const server = http.createServer(async (request, response) => {
  const origin = config.site.publicOrigin || '*';
  const requestUrl = new URL(request.url, `http://${request.headers.host || 'localhost'}`);
  const audience = requestUrl.searchParams.get('audience');

  if (request.method === 'OPTIONS') {
    response.writeHead(204, {
      'access-control-allow-origin': origin,
      'access-control-allow-methods': 'GET, OPTIONS'
    });
    response.end();
    return;
  }

  if (requestUrl.pathname === '/healthz') {
    writeJson(response, { ok: true, updatedAt: latestSnapshot.updatedAt }, origin);
    return;
  }

  if (requestUrl.pathname === '/api/status/public') {
    writeJson(response, filterSnapshot(latestSnapshot, audience), origin);
    return;
  }

  if (requestUrl.pathname === '/api/status/refresh') {
    await refreshSnapshot();
    writeJson(response, filterSnapshot(latestSnapshot, audience), origin);
    return;
  }

  writeNotFound(response, origin);
});

refreshSnapshot().catch((error) => {
  latestSnapshot = buildSnapshot([], 'outage');
  latestSnapshot.overall.message = `初始化探测失败：${error.message}`;
});

setInterval(() => {
  refreshSnapshot().catch(() => {
    latestSnapshot = buildSnapshot(latestSnapshot.services || [], 'degraded');
  });
}, config.site.refreshIntervalMs || 60000);

server.listen(config.site.port || 8787, () => {
  console.log(`VoiceLinkVR status probe listening on port ${config.site.port || 8787}`);
});
