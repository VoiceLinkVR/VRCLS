<script setup lang="ts">
import { watchEffect } from 'vue'
import { useRoute } from 'vue-router'

const route = useRoute()

const titleMap: Record<string, string> = {
  auth: '登录',
  authorize: '控制台授权登录',
  'reset-password': '重置密码',
  'verify-email': '验证邮箱',
  overview: '总览',
  billing: '订阅充值',
  orders: '账单历史',
  usage: '用量监控',
  faq: '常见问题',
  center: '账户设置',
}

watchEffect(() => {
  const routeName = String(route.name || '')
  document.title = `${titleMap[routeName] || 'VoiceLinkVR 控制台'} | VoiceLinkVR`
})
</script>

<template>
  <RouterView />
</template>
