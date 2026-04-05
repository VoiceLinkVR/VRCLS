<script setup lang="ts">
import { useI18n, type Locale } from '../i18n'

const { locale, localeOptions, setLocale, t } = useI18n()

function updateLocale(nextLocale: string) {
  setLocale(nextLocale as Locale)
}
</script>

<template>
  <label class="locale-switcher">
    <span>{{ t('common.language') }}</span>
    <select :value="locale" @change="updateLocale(($event.target as HTMLSelectElement).value)">
      <option v-for="item in localeOptions" :key="item.value" :value="item.value">
        {{ t(item.labelKey) }}
      </option>
    </select>
  </label>
</template>

<style scoped>
.locale-switcher {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  padding: 8px 12px;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.04);
  color: var(--text-soft);
  font-size: 0.86rem;
}

.locale-switcher select {
  border: 0;
  background: transparent;
  color: var(--text-main);
  outline: none;
  cursor: pointer;
}

.locale-switcher option {
  color: #101521;
}
</style>
