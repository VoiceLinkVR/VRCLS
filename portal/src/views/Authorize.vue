<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useUserStore, type DeviceAuthorizationRequest } from '../stores/user'

const route = useRoute()
const router = useRouter()
const userStore = useUserStore()

const lookupCode = ref('')
const requestPayload = ref<DeviceAuthorizationRequest | null>(null)
const loading = ref(false)
const actionLoading = ref(false)
const errorMessage = ref('')
const successMessage = ref('')

const normalizedCode = computed(() => {
  const raw =
    (typeof route.query.user_code === 'string' && route.query.user_code) ||
    (typeof route.query.code === 'string' && route.query.code) ||
    lookupCode.value
  return raw.trim().toUpperCase()
})

const canAct = computed(() => requestPayload.value?.status === 'pending')

async function loadRequest() {
  if (!normalizedCode.value) {
    errorMessage.value = '请输入客户端显示的授权码。'
    return
  }

  loading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    requestPayload.value = await userStore.loadDeviceAuthorization(normalizedCode.value)
    lookupCode.value = requestPayload.value.user_code
    if (route.query.user_code !== requestPayload.value.user_code) {
      await router.replace({ path: '/authorize', query: { user_code: requestPayload.value.user_code } })
    }
  } catch (error) {
    requestPayload.value = null
    errorMessage.value = error instanceof Error ? error.message : '加载授权请求失败。'
  } finally {
    loading.value = false
  }
}

async function handleDecision(decision: 'approve' | 'deny') {
  if (!normalizedCode.value) {
    return
  }

  actionLoading.value = true
  errorMessage.value = ''
  successMessage.value = ''
  try {
    const response = await userStore.decideDeviceAuthorization(normalizedCode.value, decision)
    successMessage.value = response.message
    await loadRequest()
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '提交授权结果失败。'
  } finally {
    actionLoading.value = false
  }
}

onMounted(() => {
  lookupCode.value = normalizedCode.value
  if (normalizedCode.value) {
    void loadRequest()
  }
})
</script>

<template>
  <div class="single-page">
    <section class="glass-panel single-card">
      <p class="eyebrow">Device Authorization</p>
      <h1>控制台授权登录</h1>
      <p class="lead">
        客户端会先申请一枚授权码，你在控制台确认后，客户端才能换取登录 token。这样可以把桌面端登录和密码输入分离。
      </p>

      <label class="input-group">
        <span>授权码</span>
        <div class="input-row">
          <input
            v-model="lookupCode"
            class="input-field"
            type="text"
            placeholder="例如 ABCD-EFGH"
            autocomplete="off"
          />
          <button class="btn btn-outline" type="button" :disabled="loading" @click="loadRequest">
            {{ loading ? '加载中...' : '读取请求' }}
          </button>
        </div>
      </label>

      <p v-if="errorMessage" class="error-text">{{ errorMessage }}</p>
      <p v-if="successMessage" class="success-text">{{ successMessage }}</p>

      <div v-if="requestPayload" class="request-card glass-panel">
        <div class="request-grid">
          <div>
            <span class="meta-label">客户端</span>
            <strong>{{ requestPayload.client_name }}</strong>
          </div>
          <div>
            <span class="meta-label">作用域</span>
            <strong>{{ requestPayload.requested_scope }}</strong>
          </div>
          <div>
            <span class="meta-label">状态</span>
            <strong>{{ requestPayload.status }}</strong>
          </div>
          <div>
            <span class="meta-label">过期时间</span>
            <strong>{{ requestPayload.expires_at }}</strong>
          </div>
          <div v-if="requestPayload.username">
            <span class="meta-label">已绑定账户</span>
            <strong>{{ requestPayload.display_name || requestPayload.username }}</strong>
          </div>
        </div>

        <div class="action-row">
          <button class="btn btn-primary" type="button" :disabled="actionLoading || !canAct" @click="handleDecision('approve')">
            {{ actionLoading ? '提交中...' : '批准登录' }}
          </button>
          <button class="btn btn-outline" type="button" :disabled="actionLoading || !canAct" @click="handleDecision('deny')">
            拒绝请求
          </button>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.single-page {
  min-height: 100vh;
  padding: 24px;
  display: grid;
  place-items: center;
}

.single-card {
  width: min(100%, 860px);
  padding: 32px;
  display: grid;
  gap: 20px;
}

.lead {
  color: var(--text-muted);
  line-height: 1.8;
}

.input-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 12px;
}

.request-card {
  padding: 24px;
  display: grid;
  gap: 20px;
}

.request-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.meta-label {
  display: block;
  margin-bottom: 8px;
  color: var(--text-muted);
  font-size: 0.88rem;
}

.action-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.error-text {
  color: #ffb4b4;
}

.success-text {
  color: #9af0c8;
}

@media (max-width: 720px) {
  .single-page {
    padding: 16px;
  }

  .single-card {
    padding: 24px 18px;
  }

  .input-row,
  .request-grid {
    grid-template-columns: 1fr;
  }
}
</style>
