<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { formatDateTime } from '../../i18n'
import { useUserStore } from '../../stores/user'

const userStore = useUserStore()
const loading = ref(false)
const errorText = ref('')
const openIds = ref<string[]>([])

const faqItems = computed(() => userStore.faqItems)

function toggleItem(id: string) {
  if (openIds.value.includes(id)) {
    openIds.value = openIds.value.filter((value) => value !== id)
    return
  }
  openIds.value = [...openIds.value, id]
}

function isOpen(id: string) {
  return openIds.value.includes(id)
}

async function loadFaqs(force = false) {
  if (!force && faqItems.value.length > 0) {
    return
  }
  loading.value = true
  errorText.value = ''
  try {
    await userStore.loadFaqItems()
    openIds.value = userStore.faqItems.length > 0 ? [userStore.faqItems[0].id] : []
  } catch (error) {
    errorText.value = error instanceof Error ? error.message : '加载常见问题失败。'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void loadFaqs()
})
</script>

<template>
  <div class="faq-page">
    <section class="panel faq-hero">
      <p class="faq-eyebrow">帮助中心</p>
      <h2>常见问题解答</h2>
      <p class="faq-copy">这里汇总了当前控制台、计费、旧账号迁移和客户端接入过程中最常见的问题说明。</p>
    </section>

    <section class="panel faq-toolbar">
      <div>
        <strong>FAQ 列表</strong>
        <p class="faq-toolbar-copy">内容由管理员后台发布，修改后会自动同步到这里。</p>
      </div>
      <button class="btn btn-outline" :disabled="loading" @click="loadFaqs(true)">
        {{ loading ? '刷新中...' : '刷新' }}
      </button>
    </section>

    <section v-if="errorText" class="panel faq-state faq-error">
      {{ errorText }}
    </section>

    <section v-else-if="faqItems.length === 0 && !loading" class="panel faq-state">
      当前还没有发布 FAQ 内容。
    </section>

    <section v-else class="faq-list">
      <article v-for="item in faqItems" :key="item.id" class="panel faq-item">
        <button class="faq-trigger" type="button" @click="toggleItem(item.id)">
          <div>
            <strong>{{ item.title }}</strong>
            <p>{{ item.publishedAt ? `发布时间：${formatDateTime(item.publishedAt)}` : '未记录发布时间' }}</p>
          </div>
          <span class="faq-arrow">{{ isOpen(item.id) ? '−' : '+' }}</span>
        </button>

        <div v-if="isOpen(item.id)" class="faq-answer">
          <p>{{ item.content }}</p>
        </div>
      </article>
    </section>
  </div>
</template>

<style scoped>
.faq-page {
  display: flex;
  flex-direction: column;
  gap: 24px;
}

.faq-hero,
.faq-toolbar,
.faq-state,
.faq-item {
  padding: 24px;
}

.faq-eyebrow {
  margin: 0 0 10px;
  font-size: 12px;
  letter-spacing: 0.16em;
  text-transform: uppercase;
  color: var(--text-soft);
}

.faq-hero h2,
.faq-toolbar strong {
  margin: 0;
}

.faq-copy,
.faq-toolbar-copy {
  margin: 10px 0 0;
  line-height: 1.7;
  color: var(--text-soft);
}

.faq-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 16px;
}

.faq-error {
  color: #ffb4b4;
}

.faq-list {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.faq-trigger {
  width: 100%;
  background: none;
  border: none;
  color: inherit;
  padding: 0;
  display: flex;
  justify-content: space-between;
  gap: 16px;
  text-align: left;
  cursor: pointer;
}

.faq-trigger p {
  margin: 8px 0 0;
  color: var(--text-soft);
  font-size: 13px;
}

.faq-arrow {
  font-size: 28px;
  line-height: 1;
  color: var(--text-soft);
}

.faq-answer {
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid var(--border);
}

.faq-answer p {
  margin: 0;
  white-space: pre-line;
  line-height: 1.8;
  color: var(--text-main);
}

@media (max-width: 768px) {
  .faq-toolbar {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
