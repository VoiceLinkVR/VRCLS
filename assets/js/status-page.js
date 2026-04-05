const statusConfig = window.VRCLS_STATUS_CONFIG || {}

const elements = {
  title: document.getElementById('overall-title'),
  message: document.getElementById('overall-message'),
  badge: document.getElementById('overall-badge'),
  updated: document.getElementById('stat-updated'),
  services: document.getElementById('stat-services'),
  latency: document.getElementById('stat-latency'),
  uptime: document.getElementById('stat-uptime'),
  servicesGrid: document.getElementById('services-grid'),
  incidentList: document.getElementById('incident-list'),
  apiBaseNote: document.getElementById('api-base-note'),
  refreshButton: document.getElementById('refresh-button'),
}

let refreshTimer = null

function formatDate(value) {
  if (!value) return '--'

  return new Date(value).toLocaleString('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  })
}

function statusLabel(status) {
  switch (status) {
    case 'operational':
      return '运行正常'
    case 'degraded':
      return '部分降级'
    case 'outage':
      return '服务中断'
    case 'loading':
      return '加载中'
    default:
      return '未知状态'
  }
}

function setBadge(status, text) {
  elements.badge.className = 'status-badge'
  elements.badge.classList.add(`status-${status || 'loading'}`)
  elements.badge.textContent = text
}

function averageLatency(services) {
  const latencies = services.map((item) => Number(item.responseTimeMs)).filter(Number.isFinite)
  if (!latencies.length) return '--'

  const avg = Math.round(latencies.reduce((sum, value) => sum + value, 0) / latencies.length)
  return `${avg} ms`
}

function renderServices(services) {
  if (!services.length) {
    elements.servicesGrid.innerHTML = '<div class="empty-state">当前暂无可展示的服务节点数据。</div>'
    return
  }

  elements.servicesGrid.innerHTML = services
    .map(
      (service) => `
    <article class="service-card">
      <div class="service-topline">
        <div>
          <h3>${service.name}</h3>
          <p>${service.description || ''}</p>
        </div>
        <span class="status-badge status-${service.status}">${statusLabel(service.status)}</span>
      </div>
      <div class="service-meta">
        <span><strong>节点区域</strong><small>${service.region || '--'}</small></span>
        <span><strong>响应延迟</strong><small>${Number.isFinite(Number(service.responseTimeMs)) ? `${service.responseTimeMs} ms` : '--'}</small></span>
        <span><strong>30 天可用率</strong><small>${Number.isFinite(Number(service.uptime30d)) ? `${service.uptime30d}%` : '--'}</small></span>
        <span><strong>最近检查</strong><small>${formatDate(service.lastCheckedAt)}</small></span>
        <span><strong>服务标识</strong><small>${service.publicEndpoint || '--'}</small></span>
      </div>
    </article>
  `,
    )
    .join('')
}

function renderIncidents(incidents) {
  if (!incidents.length) {
    elements.incidentList.innerHTML = '<div class="empty-state">最近暂无公开维护或异常事件。</div>'
    return
  }

  elements.incidentList.innerHTML = incidents
    .map(
      (incident) => `
    <article class="incident-card">
      <div class="incident-topline">
        <div>
          <h3>${incident.title}</h3>
          <p>${incident.summary || ''}</p>
        </div>
        <span class="severity ${incident.impact || 'minor'}">${incident.impact || 'minor'}</span>
      </div>
      <div class="incident-meta">
        <span><strong>当前阶段</strong><small>${incident.status || '--'}</small></span>
        <span><strong>开始时间</strong><small>${formatDate(incident.startedAt)}</small></span>
        <span><strong>恢复时间</strong><small>${formatDate(incident.resolvedAt)}</small></span>
      </div>
      ${
        (incident.updates || []).length
          ? `
        <div class="incident-updates">
          <ul>
            ${(incident.updates || []).map((update) => `<li>${formatDate(update.at)} · ${update.message}</li>`).join('')}
          </ul>
        </div>
      `
          : ''
      }
    </article>
  `,
    )
    .join('')
}

function render(payload, dataSource) {
  const services = payload.services || []
  const overall = payload.overall || {}

  elements.title.textContent = overall.title || 'VoiceLinkVR 平台运行状态'
  elements.message.textContent = overall.message || '实时监控 API 网关、翻译节点与云端数据库的健康状况。'
  setBadge(overall.status || 'loading', statusLabel(overall.status || 'loading'))
  elements.updated.textContent = formatDate(payload.updatedAt)
  elements.services.textContent = String(services.length)
  elements.latency.textContent = averageLatency(services)
  elements.uptime.textContent = Number.isFinite(Number(overall.uptime30d)) ? `${overall.uptime30d}%` : '--'
  elements.apiBaseNote.textContent = `Data Source: ${dataSource}`

  renderServices(services)
  renderIncidents(payload.incidents || [])
}

async function fetchJson(url) {
  const controller = new AbortController()
  const timeout = setTimeout(() => controller.abort(), 8000)

  try {
    const response = await fetch(url, { signal: controller.signal, cache: 'no-store' })
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`)
    }
    return await response.json()
  } finally {
    clearTimeout(timeout)
  }
}

function getBaseUrl() {
  const queryApi = new URLSearchParams(window.location.search).get('api')
  return queryApi || statusConfig.apiBaseUrl || ''
}

function buildApiUrl(forceRefresh) {
  const baseUrl = getBaseUrl()
  if (!baseUrl) return ''

  const normalizedBase = baseUrl.replace(/\/$/, '')
  return `${normalizedBase}${forceRefresh ? '/api/status/refresh' : '/api/status/public'}`
}

async function loadStatus(forceRefresh = false) {
  elements.refreshButton.disabled = true
  setBadge('loading', '加载中')

  try {
    const apiUrl = buildApiUrl(forceRefresh)

    if (apiUrl) {
      const payload = await fetchJson(apiUrl)
      render(payload, apiUrl)
      return
    }

    const samplePayload = await fetchJson(statusConfig.sampleDataPath)
    render(samplePayload, 'local sample')
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error)

    if (statusConfig.useSampleOnFailure) {
      try {
        const samplePayload = await fetchJson(statusConfig.sampleDataPath)
        render(samplePayload, 'fallback sample')
        elements.message.textContent = `实时状态接口获取失败，当前展示后备数据：${message}`
      } catch (sampleError) {
        const fallbackMessage = sampleError instanceof Error ? sampleError.message : String(sampleError)
        elements.message.textContent = `状态页加载失败：${fallbackMessage}`
        setBadge('outage', '加载失败')
      }
    } else {
      elements.message.textContent = `状态页加载失败：${message}`
      setBadge('outage', '加载失败')
    }
  } finally {
    elements.refreshButton.disabled = false
  }
}

elements.refreshButton.addEventListener('click', () => loadStatus(true))
loadStatus()

if (statusConfig.refreshIntervalMs > 0) {
  refreshTimer = window.setInterval(() => loadStatus(true), statusConfig.refreshIntervalMs)
}

window.addEventListener('beforeunload', () => {
  if (refreshTimer) {
    window.clearInterval(refreshTimer)
  }
})
