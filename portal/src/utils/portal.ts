import { formatCurrency, t } from '../i18n'

export function formatPlanLabel(price: number): string {
  if (!price || price <= 0) {
    return t('common.free')
  }
  return `${formatCurrency(price)} / ${t('common.month')}`
}

export function translateActionLabel(action: string): string {
  const normalized = (action || '').trim().toLowerCase()
  if (normalized === 'renew' || normalized === 'upgrade' || normalized === 'downgrade' || normalized === 'addon') {
    return t(`common.${normalized}`)
  }
  return t('common.unknown')
}

export function translateOrderStatus(status: string): string {
  const normalized = (status || '').trim().toLowerCase()
  if (normalized === 'paid' || normalized === 'pending' || normalized === 'cancelled' || normalized === 'expired') {
    return t(`common.${normalized}`)
  }
  return t('common.unknown')
}

export function translateAccountStatus(status: string): string {
  const normalized = (status || '').trim().toLowerCase()
  if (normalized === 'active' || normalized === 'inactive' || normalized === 'expired' || normalized === 'pending') {
    return t(`common.${normalized}`)
  }
  return t('common.unknown')
}
