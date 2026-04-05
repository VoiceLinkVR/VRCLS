<script setup lang="ts">
import { watchEffect } from 'vue'
import { useRoute } from 'vue-router'
import { t } from './i18n'

const route = useRoute()

watchEffect(() => {
  const titleKey = typeof route.meta.titleKey === 'string' ? route.meta.titleKey : 'route.landing'
  document.title = `${t(titleKey)} | VoiceLinkVR`
})
</script>

<template>
  <RouterView />
</template>
