<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useUserStore } from '../stores/user'

const router = useRouter()
const userStore = useUserStore()
const mode = ref<'login' | 'register'>('login')
const form = ref({ email: '', password: '', code: '' })
const codeToken = ref('')

async function submit() {
  if (mode.value === 'login') {
    await userStore.login(form.value.email, form.value.password)
    router.push('/overview')
  } else {
    await userStore.register(form.value.email, form.value.password, codeToken.value, form.value.code)
    router.push('/overview')
  }
}

async function sendCode() {
  const res = await userStore.sendRegisterCode(form.value.email)
  codeToken.value = res.verification_token
}
</script>

<template>
  <div class="auth-split">
    <div class="auth-info">
      <div class="info-content">
        <div class="brand">
          <div class="brand-logo">V</div>
          <strong>VoiceLinkVR</strong>
        </div>
        <h2>连接虚拟与现实的<br>语言中枢。</h2>
        <p>
          VoiceLinkVR 致力于为 VRChat 玩家提供极低延迟的实时语音识别与机器翻译服务。
          通过原生 OSC 协议，将多语种字幕无缝投射到虚拟世界中。
        </p>

        <div class="feature-list">
          <div class="feature-item">
            <strong>毫秒级响应</strong>
            <span>针对实时流式语音优化的识别管线。</span>
          </div>
          <div class="feature-item">
            <strong>原生集成</strong>
            <span>无需复杂配置，自动对接 VRChat Chatbox。</span>
          </div>
          <div class="feature-item">
            <strong>云端算力</strong>
            <span>弹性扩展的翻译能力，支持全球 20+ 语种。</span>
          </div>
        </div>
      </div>
    </div>

    <div class="auth-form-wrap">
      <div class="auth-card">
        <div class="auth-header">
          <h1>{{ mode === 'login' ? '登录控制台' : '创建账户' }}</h1>
          <p>{{ mode === 'login' ? '欢迎回来，请登录您的账户。' : '注册以获取您的初始翻译额度。' }}</p>
        </div>

        <form @submit.prevent="submit" class="auth-form">
          <div class="input-group">
            <label>邮箱地址</label>
            <input v-model="form.email" type="email" class="input-field" placeholder="name@example.com" required />
          </div>

          <div class="input-group" v-if="mode === 'register'">
            <label>验证码</label>
            <div class="flex-row">
              <input v-model="form.code" type="text" class="input-field" placeholder="6位数验证码" required />
              <button class="btn btn-outline" type="button" @click="sendCode">获取验证码</button>
            </div>
          </div>

          <div class="input-group">
            <label>密码</label>
            <input v-model="form.password" type="password" class="input-field" placeholder="••••••••" required />
          </div>

          <p v-if="userStore.authError" class="text-error auth-error">{{ userStore.authError }}</p>

          <button class="btn btn-primary w-full" type="submit" :disabled="userStore.loading">
            {{ mode === 'login' ? '登 录' : '注 册' }}
          </button>

          <div class="auth-footer">
            <a href="#" @click.prevent="mode = mode === 'login' ? 'register' : 'login'">
              {{ mode === 'login' ? '还没有账号？立即注册' : '已有账号？返回登录' }}
            </a>
            <div class="back-home">
              <a href="/" class="text-muted">返回官网首页</a>
            </div>
          </div>
        </form>
      </div>
    </div>
  </div>
</template>

<style scoped>
.auth-split {
  display: flex;
  min-height: 100vh;
  width: 100%;
  background: var(--bg-base);
}

.auth-info {
  flex: 1;
  background: var(--bg-panel);
  border-right: 1px solid var(--border);
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 40px;
}

.info-content {
  max-width: 480px;
  width: 100%;
}

.brand {
  display: flex;
  align-items: center;
  gap: 12px;
  font-size: 1.125rem;
  margin-bottom: 48px;
}

.brand-logo {
  width: 28px;
  height: 28px;
  background: var(--text-main);
  color: var(--bg-base);
  border-radius: 6px;
  display: flex;
  align-items: center;
  justify-content: center;
  font-weight: 800;
  font-size: 14px;
}

.info-content h2 {
  font-size: 2.5rem;
  line-height: 1.2;
  margin-bottom: 16px;
  letter-spacing: -0.04em;
}

.info-content p {
  color: var(--text-soft);
  font-size: 1.05rem;
  line-height: 1.7;
  margin-bottom: 40px;
}

.feature-list {
  display: grid;
  gap: 24px;
}

.feature-item strong {
  display: block;
  font-size: 1rem;
  margin-bottom: 4px;
}

.feature-item span {
  color: var(--text-soft);
  font-size: 0.9375rem;
}

.auth-form-wrap {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 32px;
}

.auth-card {
  width: 100%;
  max-width: 400px;
}

.auth-header {
  margin-bottom: 32px;
}

.auth-header h1 {
  font-size: 1.75rem;
  margin-bottom: 8px;
}

.auth-header p {
  color: var(--text-soft);
  font-size: 0.9375rem;
}

.auth-form {
  display: flex;
  flex-direction: column;
}

.auth-error {
  margin-bottom: 16px;
}

.auth-footer {
  margin-top: 24px;
  text-align: center;
  font-size: 0.875rem;
}

.auth-footer a {
  color: var(--text-soft);
  text-decoration: none;
}

.auth-footer a:hover {
  color: var(--text-main);
}

.back-home {
  margin-top: 16px;
}

.text-muted {
  color: var(--text-muted);
}

@media (max-width: 960px) {
  .auth-split {
    flex-direction: column;
  }

  .auth-info {
    flex: none;
    padding: 40px 24px;
    border-right: none;
    border-bottom: 1px solid var(--border);
  }

  .info-content h2 {
    font-size: 2rem;
  }
}
</style>
