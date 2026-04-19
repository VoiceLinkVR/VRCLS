import { formatCurrency } from '../i18n'

export function formatPlanLabel(price: number): string {
  if (!price || price <= 0) {
    return '免费额度'
  }
  return `${formatCurrency(price)} / 月`
}

export function translateActionLabel(action: string): string {
  const normalized = (action || '').trim().toLowerCase()
  const labels: Record<string, string> = {
    renew: '续费',
    upgrade: '升级',
    downgrade: '降级',
    addon: '资源包',
  }
  return labels[normalized] || '未知'
}

export function translateOrderStatus(status: string): string {
  const normalized = (status || '').trim().toLowerCase()
  const labels: Record<string, string> = {
    paid: '已支付',
    pending: '待支付',
    cancelled: '已取消',
    expired: '已过期',
  }
  return labels[normalized] || '未知'
}

export function translateAccountStatus(status: string): string {
  const normalized = (status || '').trim().toLowerCase()
  const labels: Record<string, string> = {
    active: '正常',
    inactive: '已停用',
    expired: '已过期',
    pending: '处理中',
  }
  return labels[normalized] || '未知'
}
