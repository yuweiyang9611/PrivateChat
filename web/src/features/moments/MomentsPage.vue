<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { Camera } from '@element-plus/icons-vue'
import PreviewHost from '@/features/drive/PreviewHost.vue'
import PostComposer from './PostComposer.vue'

/** 时光外壳：动态 / 相册两个分区 + "发动态"；内容由子路由渲染 */
const { t } = useI18n()
const route = useRoute()
const composing = ref(false)
const section = computed(() => (route.meta.momentsSection as string | undefined) ?? 'feed')

const tabs = computed(() => [
  { key: 'feed', to: '/moments', label: t('moments.feed') },
  { key: 'albums', to: '/moments/albums', label: t('moments.albums') },
])
</script>

<template>
  <div class="flex h-full min-h-0 flex-col">
    <header class="flex shrink-0 items-center gap-2 px-3 py-2" style="border-bottom: 1px solid var(--pc-border)">
      <nav class="flex gap-1">
        <router-link
          v-for="tab in tabs"
          :key="tab.key"
          :to="tab.to"
          class="rounded-full px-3 py-1 text-sm"
          :style="section === tab.key ? 'background: var(--pc-primary); color: #fff' : 'background: var(--pc-border)'"
          :data-testid="`moments-tab-${tab.key}`"
        >{{ tab.label }}</router-link>
      </nav>
      <el-button class="ml-auto" type="primary" size="small" :icon="Camera" data-testid="new-post" @click="composing = true">{{ t('moments.newPost') }}</el-button>
    </header>
    <div class="min-h-0 flex-1">
      <router-view />
    </div>
    <PostComposer :open="composing" @close="composing = false" />
    <PreviewHost />
  </div>
</template>
