<script setup lang="ts">
import { useI18n, type Locale } from '../i18n'

const { locale, setLocale } = useI18n()

const options: Array<{ value: Locale; label: string }> = [
  { value: 'zh-CN', label: '中文' },
  { value: 'en', label: 'English' },
  { value: 'ja', label: '日本語' },
]

function updateLocale(nextLocale: string) {
  setLocale(nextLocale as Locale)
}
</script>

<template>
  <label class="locale-switcher">
    <span>语言</span>
    <select :value="locale" @change="updateLocale(($event.target as HTMLSelectElement).value)">
      <option v-for="item in options" :key="item.value" :value="item.value">
        {{ item.label }}
      </option>
    </select>
  </label>
</template>

<style scoped>
.locale-switcher {
  display: flex;
  align-items: center;
  gap: 10px;
  color: var(--text-soft);
  font-size: 0.875rem;
}

.locale-switcher span {
  color: var(--text-soft);
  font-weight: 500;
}

.locale-switcher select {
  height: 36px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--bg-base);
  color: var(--text-main);
  outline: none;
  cursor: pointer;
  transition: border-color 0.2s ease, background 0.2s ease;
}

.locale-switcher select:hover,
.locale-switcher select:focus {
  border-color: var(--border-hover);
  background: var(--bg-panel);
}

.locale-switcher option {
  color: var(--text-main);
  background: var(--bg-base);
}
</style>
