<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft } from '@element-plus/icons-vue'
import { MomentsError, useMomentsStore } from '@/stores/moments'
import { useFamilyStore } from '@/stores/family'
import PostCard from './PostCard.vue'

/** 单条动态（推送通知、那年今天里点进来） */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const moments = useMomentsStore()
const family = useFamilyStore()

const id = computed(() => String(route.params.id ?? ''))
const post = computed(() => moments.cache[id.value] ?? null)
const state = ref<'loading' | 'ok' | 'gone' | 'error'>('loading')
/** 从服务器拉到最新的一份之后加一：缓存里那份可能还没有要高亮的评论 */
const fetched = ref(0)

watch(id, async (pid) => {
  if (!pid) return
  void family.load()
  state.value = moments.cache[pid] ? 'ok' : 'loading'
  try {
    await moments.fetchPost(pid)
    state.value = 'ok'
    fetched.value++
  } catch (e) {
    state.value = e instanceof MomentsError && (e.status === 404 || e.status === 403) ? 'gone' : moments.cache[pid] ? 'ok' : 'error'
  }
}, { immediate: true })

// 搜索结果跳过来：?comment={id} —— 滚到那条评论闪一下
let flashed = ''
watch([state, () => route.query.comment, fetched], async ([s, comment]) => {
  if (s !== 'ok' || typeof comment !== 'string' || !comment || flashed === comment) return
  await nextTick()
  const el = document.querySelector<HTMLElement>(`[data-comment="${CSS.escape(comment)}"]`)
  if (!el) return
  flashed = comment
  el.scrollIntoView({ block: 'center' })
  el.classList.remove('pc-flash')
  void el.offsetWidth // 重新触发动画
  el.classList.add('pc-flash')
}, { immediate: true })

// 别人把它删了（实时事件）：缓存里没了
watch(post, (p, old) => {
  if (!p && old) state.value = 'gone'
})

function back(): void {
  if (window.history.state?.back) router.back()
  else void router.push('/moments')
}
</script>

<template>
  <div class="h-full overflow-y-auto">
    <div class="mx-auto flex max-w-2xl flex-col gap-3 p-3">
      <div>
        <el-button text :icon="ArrowLeft" @click="back">{{ t('moments.backToFeed') }}</el-button>
      </div>
      <PostCard v-if="post && state === 'ok'" :post="post" @deleted="back" />
      <el-skeleton v-else-if="state === 'loading'" animated :rows="4" />
      <el-empty v-else-if="state === 'gone'" :description="t('moments.postGone')" data-testid="post-gone" />
      <el-empty v-else :description="t('moments.loadFailed')" />
    </div>
  </div>
</template>
