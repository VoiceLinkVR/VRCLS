<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'

type NoticeKind = 'info' | 'success' | 'error'

const ADMIN_API_PREFIX = '/v2/adminapi'

type AnnouncementItem = {
  id: string
  title: string
  content: string
  display_mode: 'banner' | 'modal'
  show_in_overview: boolean
  sort_order: number
  is_published: boolean
  published_at: string | null
  created_at: string | null
  updated_at: string | null
  updated_by_username: string | null
}

type AnnouncementListPayload = {
  items: AnnouncementItem[]
}

type FaqItem = {
  id: string
  title: string
  content: string
  sort_order: number
  is_published: boolean
  published_at: string | null
  created_at: string | null
  updated_at: string | null
  updated_by_username: string | null
}

type FaqListPayload = {
  items: FaqItem[]
}

class ApiError extends Error {
  status: number
  payload: unknown

  constructor(message: string, status: number, payload: unknown) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.payload = payload
  }
}

function emptyAnnouncementForm() {
  return {
    title: '',
    content: '',
    display_mode: 'banner' as 'banner' | 'modal',
    show_in_overview: false,
    sort_order: 100,
    is_published: false,
  }
}

function emptyFaqForm() {
  return {
    title: '',
    content: '',
    sort_order: 100,
    is_published: false,
  }
}

async function requestJson<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers ?? {})
  if (init.body && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json')
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'include',
  })

  const rawText = await response.text()
  let payload: unknown = null

  if (rawText) {
    try {
      payload = JSON.parse(rawText)
    } catch {
      payload = rawText
    }
  }

  if (!response.ok) {
    let message = `Request failed: ${response.status}`
    if (typeof payload === 'string' && payload.trim()) {
      message = payload
    } else if (payload && typeof payload === 'object') {
      const detail = (payload as { detail?: unknown }).detail
      if (typeof detail === 'string' && detail.trim()) {
        message = detail
      }
    }
    throw new ApiError(message, response.status, payload)
  }

  return payload as T
}

function formatDate(value: string | null | undefined) {
  if (!value) {
    return '-'
  }
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) {
    return value
  }
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')} ${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`
}

const busy = ref(false)
const announcementPayload = ref<AnnouncementListPayload>({ items: [] })
const faqPayload = ref<FaqListPayload>({ items: [] })
const selectedAnnouncementId = ref('')
const selectedFaqId = ref('')

const notice = reactive({
  kind: 'info' as NoticeKind,
  message: '',
})

const announcementForm = reactive(emptyAnnouncementForm())
const faqForm = reactive(emptyFaqForm())

const selectedAnnouncement = computed(() =>
  announcementPayload.value.items.find((item) => item.id === selectedAnnouncementId.value) ?? null,
)
const selectedFaq = computed(() =>
  faqPayload.value.items.find((item) => item.id === selectedFaqId.value) ?? null,
)

const publishedAnnouncementCount = computed(() =>
  announcementPayload.value.items.filter((item) => item.is_published).length,
)
const publishedFaqCount = computed(() =>
  faqPayload.value.items.filter((item) => item.is_published).length,
)

function setNotice(kind: NoticeKind, message: string) {
  notice.kind = kind
  notice.message = message
}

function clearNotice() {
  notice.message = ''
}

function applyAnnouncementToForm(item: AnnouncementItem) {
  announcementForm.title = item.title
  announcementForm.content = item.content
  announcementForm.display_mode = item.display_mode
  announcementForm.show_in_overview = item.show_in_overview
  announcementForm.sort_order = item.sort_order
  announcementForm.is_published = item.is_published
}

function applyFaqToForm(item: FaqItem) {
  faqForm.title = item.title
  faqForm.content = item.content
  faqForm.sort_order = item.sort_order
  faqForm.is_published = item.is_published
}

function resetAnnouncementForm() {
  selectedAnnouncementId.value = ''
  Object.assign(announcementForm, emptyAnnouncementForm())
}

function resetFaqForm() {
  selectedFaqId.value = ''
  Object.assign(faqForm, emptyFaqForm())
}

function selectAnnouncement(item: AnnouncementItem) {
  selectedAnnouncementId.value = item.id
  applyAnnouncementToForm(item)
}

function selectFaq(item: FaqItem) {
  selectedFaqId.value = item.id
  applyFaqToForm(item)
}

async function loadAnnouncements() {
  announcementPayload.value = await requestJson<AnnouncementListPayload>(`${ADMIN_API_PREFIX}/content/announcements`)
}

async function loadFaqs() {
  faqPayload.value = await requestJson<FaqListPayload>(`${ADMIN_API_PREFIX}/content/faqs`)
}

async function refreshAll() {
  await Promise.all([loadAnnouncements(), loadFaqs()])
}

async function handleSaveAnnouncement() {
  busy.value = true
  clearNotice()
  try {
    const payload = {
      title: announcementForm.title,
      content: announcementForm.content,
      display_mode: announcementForm.display_mode,
      show_in_overview: announcementForm.show_in_overview,
      sort_order: announcementForm.sort_order,
      is_published: announcementForm.is_published,
    }
    const saved = selectedAnnouncementId.value
      ? await requestJson<AnnouncementItem>(`${ADMIN_API_PREFIX}/content/announcements/${selectedAnnouncementId.value}`, {
          method: 'PATCH',
          body: JSON.stringify(payload),
        })
      : await requestJson<AnnouncementItem>(`${ADMIN_API_PREFIX}/content/announcements`, {
          method: 'POST',
          body: JSON.stringify(payload),
        })

    await loadAnnouncements()
    const current = announcementPayload.value.items.find((item) => item.id === saved.id)
    if (current) {
      selectAnnouncement(current)
    }
    setNotice('success', '公告已保存。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '保存公告失败')
  } finally {
    busy.value = false
  }
}

async function handleDeleteAnnouncement() {
  if (!selectedAnnouncementId.value || !window.confirm('确定删除当前公告吗？')) {
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/content/announcements/${selectedAnnouncementId.value}`, {
      method: 'DELETE',
    })
    await loadAnnouncements()
    resetAnnouncementForm()
    setNotice('success', '公告已删除。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '删除公告失败')
  } finally {
    busy.value = false
  }
}

async function handleSaveFaq() {
  busy.value = true
  clearNotice()
  try {
    const payload = {
      title: faqForm.title,
      content: faqForm.content,
      sort_order: faqForm.sort_order,
      is_published: faqForm.is_published,
    }
    const saved = selectedFaqId.value
      ? await requestJson<FaqItem>(`${ADMIN_API_PREFIX}/content/faqs/${selectedFaqId.value}`, {
          method: 'PATCH',
          body: JSON.stringify(payload),
        })
      : await requestJson<FaqItem>(`${ADMIN_API_PREFIX}/content/faqs`, {
          method: 'POST',
          body: JSON.stringify(payload),
        })

    await loadFaqs()
    const current = faqPayload.value.items.find((item) => item.id === saved.id)
    if (current) {
      selectFaq(current)
    }
    setNotice('success', 'FAQ 已保存。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '保存 FAQ 失败')
  } finally {
    busy.value = false
  }
}

async function handleDeleteFaq() {
  if (!selectedFaqId.value || !window.confirm('确定删除当前 FAQ 吗？')) {
    return
  }
  busy.value = true
  clearNotice()
  try {
    await requestJson(`${ADMIN_API_PREFIX}/content/faqs/${selectedFaqId.value}`, {
      method: 'DELETE',
    })
    await loadFaqs()
    resetFaqForm()
    setNotice('success', 'FAQ 已删除。')
  } catch (error) {
    setNotice('error', error instanceof Error ? error.message : '删除 FAQ 失败')
  } finally {
    busy.value = false
  }
}

onMounted(() => {
  void refreshAll()
})
</script>

<template>
  <div class="page-grid">
    <div v-if="notice.message" class="notice" :data-kind="notice.kind">{{ notice.message }}</div>

    <div class="metrics-grid">
      <article class="metric-card glass-panel"><span>公告总数</span><strong>{{ announcementPayload.items.length }}</strong></article>
      <article class="metric-card glass-panel"><span>已发布公告</span><strong>{{ publishedAnnouncementCount }}</strong></article>
      <article class="metric-card glass-panel"><span>FAQ 总数</span><strong>{{ faqPayload.items.length }}</strong></article>
      <article class="metric-card glass-panel"><span>已发布 FAQ</span><strong>{{ publishedFaqCount }}</strong></article>
    </div>

    <section class="glass-panel panel-section">
      <div class="section-head">
        <h2>公告管理</h2>
        <div class="lookup-row compact">
          <button class="btn btn-ghost" :disabled="busy" @click="loadAnnouncements">刷新</button>
          <button class="btn btn-outline" :disabled="busy" @click="resetAnnouncementForm">新建公告</button>
        </div>
      </div>

      <div class="split-grid content-split">
        <section class="glass-panel panel-section content-list-panel">
          <div class="section-head"><h3>公告列表</h3></div>
          <table class="data-table">
            <thead><tr><th>标题</th><th>类型</th><th>总览显示</th><th>状态</th><th>发布时间</th><th>操作</th></tr></thead>
            <tbody>
              <tr v-for="item in announcementPayload.items" :key="item.id" :class="{ selected: selectedAnnouncementId === item.id }">
                <td><strong>{{ item.title }}</strong><span>{{ item.updated_by_username || '-' }}</span></td>
                <td>{{ item.display_mode === 'modal' ? '弹窗' : '普通消息' }}</td>
                <td>{{ item.show_in_overview ? '显示' : '隐藏' }}</td>
                <td>{{ item.is_published ? '已发布' : '未发布' }}</td>
                <td>{{ formatDate(item.published_at) }}</td>
                <td class="actions-cell">
                  <button class="btn btn-ghost" :disabled="busy" @click="selectAnnouncement(item)">编辑</button>
                </td>
              </tr>
            </tbody>
          </table>
        </section>

        <section class="glass-panel panel-section form-panel">
          <div class="section-head">
            <h3>{{ selectedAnnouncement ? '编辑公告' : '新建公告' }}</h3>
            <button class="btn btn-ghost" :disabled="busy" @click="resetAnnouncementForm">清空</button>
          </div>
          <div class="form-grid">
            <label><span>标题</span><input v-model.trim="announcementForm.title" class="input-field" /></label>
            <label>
              <span>展示类型</span>
              <select v-model="announcementForm.display_mode" class="input-field">
                <option value="banner">普通消息</option>
                <option value="modal">弹窗公告</option>
              </select>
            </label>
            <label><span>排序值</span><input v-model.number="announcementForm.sort_order" class="input-field" type="number" min="0" /></label>
            <label class="check-row"><input v-model="announcementForm.show_in_overview" type="checkbox" /> 显示在总览顶部</label>
            <label class="check-row"><input v-model="announcementForm.is_published" type="checkbox" /> 立即发布</label>
            <label class="content-editor-wide">
              <span>正文内容</span>
              <textarea v-model="announcementForm.content" class="input-field content-textarea"></textarea>
            </label>
          </div>
          <div class="lookup-row">
            <button class="btn btn-primary" :disabled="busy" @click="handleSaveAnnouncement">保存公告</button>
            <button class="btn btn-danger" :disabled="busy || !selectedAnnouncement" @click="handleDeleteAnnouncement">删除公告</button>
          </div>
          <div v-if="selectedAnnouncement" class="mini-card">
            <strong>最近修改</strong>
            <span>{{ selectedAnnouncement.updated_by_username || '-' }} / {{ formatDate(selectedAnnouncement.updated_at) }}</span>
          </div>
        </section>
      </div>
    </section>

    <section class="glass-panel panel-section">
      <div class="section-head">
        <h2>FAQ 管理</h2>
        <div class="lookup-row compact">
          <button class="btn btn-ghost" :disabled="busy" @click="loadFaqs">刷新</button>
          <button class="btn btn-outline" :disabled="busy" @click="resetFaqForm">新建 FAQ</button>
        </div>
      </div>

      <div class="split-grid content-split">
        <section class="glass-panel panel-section content-list-panel">
          <div class="section-head"><h3>FAQ 列表</h3></div>
          <table class="data-table">
            <thead><tr><th>标题</th><th>状态</th><th>发布时间</th><th>操作</th></tr></thead>
            <tbody>
              <tr v-for="item in faqPayload.items" :key="item.id" :class="{ selected: selectedFaqId === item.id }">
                <td><strong>{{ item.title }}</strong><span>{{ item.updated_by_username || '-' }}</span></td>
                <td>{{ item.is_published ? '已发布' : '未发布' }}</td>
                <td>{{ formatDate(item.published_at) }}</td>
                <td class="actions-cell">
                  <button class="btn btn-ghost" :disabled="busy" @click="selectFaq(item)">编辑</button>
                </td>
              </tr>
            </tbody>
          </table>
        </section>

        <section class="glass-panel panel-section form-panel">
          <div class="section-head">
            <h3>{{ selectedFaq ? '编辑 FAQ' : '新建 FAQ' }}</h3>
            <button class="btn btn-ghost" :disabled="busy" @click="resetFaqForm">清空</button>
          </div>
          <div class="form-grid">
            <label><span>标题</span><input v-model.trim="faqForm.title" class="input-field" /></label>
            <label><span>排序值</span><input v-model.number="faqForm.sort_order" class="input-field" type="number" min="0" /></label>
            <label class="check-row"><input v-model="faqForm.is_published" type="checkbox" /> 立即发布</label>
            <label class="content-editor-wide">
              <span>正文内容</span>
              <textarea v-model="faqForm.content" class="input-field content-textarea"></textarea>
            </label>
          </div>
          <div class="lookup-row">
            <button class="btn btn-primary" :disabled="busy" @click="handleSaveFaq">保存 FAQ</button>
            <button class="btn btn-danger" :disabled="busy || !selectedFaq" @click="handleDeleteFaq">删除 FAQ</button>
          </div>
          <div v-if="selectedFaq" class="mini-card">
            <strong>最近修改</strong>
            <span>{{ selectedFaq.updated_by_username || '-' }} / {{ formatDate(selectedFaq.updated_at) }}</span>
          </div>
        </section>
      </div>
    </section>
  </div>
</template>

<style scoped>
.content-split {
  align-items: start;
}

.content-list-panel {
  overflow: hidden;
}

.content-editor-wide {
  grid-column: 1 / -1;
}

.content-textarea {
  min-height: 220px;
  resize: vertical;
  white-space: pre-wrap;
}

.notice {
  padding: 14px 18px;
  border-radius: 18px;
}

.notice[data-kind='success'] {
  background: rgba(0, 230, 118, 0.12);
}

.notice[data-kind='error'] {
  background: rgba(255, 79, 79, 0.12);
}
</style>
