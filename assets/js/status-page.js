const statusConfig = window.VRCLS_STATUS_CONFIG || {}

const elements = {
  headerBox: document.getElementById('header-box'),
  title: document.getElementById('overall-title'),
  message: document.getElementById('overall-message'),
  updated: document.getElementById('stat-updated'),
  services: document.getElementById('stat-services'),
  latency: document.getElementById('stat-latency'),
  uptime: document.getElementById('stat-uptime'),
  servicesGrid: document.getElementById('services-grid'),
  incidentList: document.getElementById('incident-list'),
  refreshButton: document.getElementById('refresh-button'),
}

function formatDate(value) {
  if (!value) return '--'
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}

function averageLatency(services) {
  const latencies = services.map((service) => Number(service.responseTimeMs)).filter(Number.isFinite)
  if (!latencies.length) return '--'
  return `${Math.round(latencies.reduce((sum, latency) => sum + latency, 0) / latencies.length)} ms`
}

function isPublicService(service) {
  if (service.public === false) return false
  const visibility = String(service.visibility || service.audience || '').toLowerCase()
  if (visibility === 'internal' || visibility === 'private') return false
  return true
}

function renderServices(services) {
  if (!services.length) {
    elements.servicesGrid.innerHTML = '<div style="padding:24px; color:var(--text-muted); text-align:center;">暂无服务节点数据</div>'
    return
  }
  elements.servicesGrid.innerHTML = services.map((service) => `
    <div class="service-item">
      <div>
        <div class="service-name">
          <div class="status-indicator ${service.status}"></div>
          ${service.name}
        </div>
        <div class="service-desc">${service.description || ''}</div>
      </div>
      <div class="service-meta">
        <div style="margin-bottom: 4px;">延迟: <strong>${service.responseTimeMs || '--'} ms</strong></div>
        <div>30天可用率: <strong>${service.uptime30d || '--'}%</strong></div>
      </div>
    </div>
  `).join('')
}

function renderIncidents(incidents) {
  if (!incidents.length) {
    elements.incidentList.innerHTML = '<div style="padding:24px; text-align:center; color:var(--text-muted);">近期无异常或维护记录。</div>'
    return
  }
  elements.incidentList.innerHTML = incidents.map((incident) => `
    <div class="incident-item">
      <div class="incident-title">
        <span style="color: ${incident.impact === 'major' ? 'var(--danger)' : 'var(--text-main)'}">${incident.title}</span>
        <span class="incident-time">${formatDate(incident.startedAt)} - ${incident.status === 'resolved' ? '已解决' : '调查中'}</span>
      </div>
      <p style="color: var(--text-main); font-size:0.875rem;">${incident.summary}</p>
      <div class="incident-updates">
        ${(incident.updates || []).map((update) => `<div><strong>${formatDate(update.at)}</strong> · ${update.message}</div>`).join('')}
      </div>
    </div>
  `).join('')
}

function render(payload) {
  const services = (payload.services || []).filter(isPublicService)
  const overallStatus = payload.overall?.status || 'loading'

  elements.headerBox.className = `status-header-box ${overallStatus === 'operational' ? 'success' : overallStatus === 'outage' ? 'danger' : overallStatus === 'loading' ? '' : 'warning'}`
  elements.title.textContent = payload.overall?.title || '所有系统运行正常'
  elements.message.textContent = payload.overall?.message || '目前未发现任何问题。'
  elements.updated.textContent = formatDate(payload.updatedAt)
  elements.services.textContent = String(services.length)
  elements.latency.textContent = averageLatency(services)
  elements.uptime.textContent = payload.overall?.uptime30d ? `${payload.overall.uptime30d}%` : '--'

  renderServices(services)
  renderIncidents(payload.incidents || [])
}

async function fetchJson(url) {
  const response = await fetch(url, { cache: 'no-store' })
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return await response.json()
}

async function loadStatus(forceRefresh = false) {
  elements.refreshButton.disabled = true
  try {
    const baseUrl = statusConfig.apiBaseUrl || ''
    const apiPath = forceRefresh
      ? statusConfig.refreshStatusPath || '/api/status/refresh'
      : statusConfig.publicStatusPath || '/api/status/public'
    const url = baseUrl ? `${baseUrl}${apiPath}` : statusConfig.sampleDataPath
    const payload = await fetchJson(url)
    render(payload)
  } catch (error) {
    elements.headerBox.className = 'status-header-box danger'
    elements.title.textContent = '状态读取失败'
    elements.message.textContent = `无法获取实时状态：${error instanceof Error ? error.message : String(error)}`
    elements.servicesGrid.innerHTML = '<div style="padding:24px; color:var(--text-muted); text-align:center;">请稍后重试。</div>'
    elements.incidentList.innerHTML = '<div style="padding:24px; text-align:center; color:var(--text-muted);">暂无可用事件数据。</div>'
  } finally {
    elements.refreshButton.disabled = false
  }
}

elements.refreshButton.addEventListener('click', () => loadStatus(true))
loadStatus()

if (statusConfig.refreshIntervalMs > 0) {
  window.setInterval(() => loadStatus(true), statusConfig.refreshIntervalMs)
}
