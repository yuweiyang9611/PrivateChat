<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { MoreFilled } from '@element-plus/icons-vue'
import type { NodeDto } from '@/stores/drive'
import { blobUrl } from '@/upload'
import { formatBytes, formatDate, formatDuration, hasThumb, KIND_EMOJI, kindOfMime } from './fileKind'

/**
 * 文件列表里的一项（网格卡片或列表行）。单独成组件：缩略图坏了、刷新按钮转圈等只重渲染这一项，
 * 不会让几千项一起重渲染；菜单 persistent=false，打开时才渲染（否则每项都常驻一套弹层）。
 */
const props = defineProps<{ node: NodeDto; view: 'grid' | 'list'; canWrite: boolean; canManage: boolean }>()
const emit = defineEmits<{ open: []; command: [cmd: string] }>()
const { t } = useI18n()

const broken = ref(false)
// 视频缩略图在后台生成完之前是 404：每次目录刷新（含 DriveChanged 推送）都换成新对象，借此重新试一次
watch(() => props.node, () => { broken.value = false })

const thumb = computed(() => {
  const n = props.node
  if (n.isFolder || !n.blobId || broken.value) return null
  return hasThumb(kindOfMime(n.mime)) ? blobUrl(n.blobId, { v: 'thumb-320' }) : null
})
const icon = computed(() => KIND_EMOJI[kindOfMime(props.node.mime, props.node.isFolder)])
const subtitle = computed(() => {
  const n = props.node
  if (n.isFolder) return formatDate(n.updatedAt)
  const parts = [formatBytes(n.size)]
  if (n.durationSeconds) parts.push(formatDuration(n.durationSeconds))
  return parts.join(' · ')
})
</script>

<template>
  <li v-if="view === 'grid'" class="group relative" :data-node-name="node.name">
    <button type="button" class="flex w-full flex-col items-center gap-1 rounded-lg p-2 text-center hover:bg-[var(--pc-primary-soft)]" @click="emit('open')">
      <div class="flex aspect-square w-full items-center justify-center overflow-hidden rounded-lg" style="background: var(--pc-border)">
        <img v-if="thumb" :src="thumb" alt="" loading="lazy" class="h-full w-full object-cover" @error="broken = true" />
        <span v-else class="text-5xl">{{ icon }}</span>
      </div>
      <span class="line-clamp-2 w-full break-all text-sm leading-tight">{{ node.name }}</span>
      <span class="text-xs" style="color: var(--pc-text-muted)">{{ subtitle }}</span>
    </button>
    <!-- 菜单按钮：能悬停的设备（鼠标）上悬停才显示；触屏（含 iPad、横屏手机）一直显示 -->
    <el-dropdown class="!absolute right-1 top-1" trigger="click" :persistent="false" @command="(c: string) => emit('command', c)">
      <el-button
        size="small"
        circle
        :icon="MoreFilled"
        class="opacity-80 [@media(hover:hover)]:opacity-0 group-hover:opacity-100 focus-visible:opacity-100"
        :aria-label="t('drive.more')"
        data-testid="node-menu"
        @click.stop
      />
      <template #dropdown>
        <el-dropdown-menu>
          <el-dropdown-item command="open">{{ t('drive.open') }}</el-dropdown-item>
          <el-dropdown-item v-if="!node.isFolder" command="download">{{ t('drive.download') }}</el-dropdown-item>
          <el-dropdown-item v-if="!node.isFolder" command="send">{{ t('drive.sendToChat') }}</el-dropdown-item>
          <el-dropdown-item v-if="canManage" command="share">{{ t('drive.share') }}</el-dropdown-item>
          <el-dropdown-item v-if="canWrite" command="rename" divided>{{ t('drive.rename') }}</el-dropdown-item>
          <el-dropdown-item v-if="canManage" command="move">{{ t('drive.move') }}</el-dropdown-item>
          <el-dropdown-item v-if="canWrite" command="delete">{{ t('drive.delete') }}</el-dropdown-item>
        </el-dropdown-menu>
      </template>
    </el-dropdown>
  </li>

  <tr v-else class="cursor-pointer hover:bg-[var(--pc-primary-soft)]" :data-node-name="node.name" @click="emit('open')">
    <td class="w-10 py-1.5 pl-1">
      <img v-if="thumb" :src="thumb" alt="" loading="lazy" class="h-8 w-8 rounded object-cover" @error="broken = true" />
      <span v-else class="text-2xl">{{ icon }}</span>
    </td>
    <td class="max-w-0 truncate px-2">{{ node.name }}<span v-if="node.hasShares" class="ml-1 text-xs" :title="t('drive.isShared')">🔗</span></td>
    <td class="hidden whitespace-nowrap px-2 text-right sm:table-cell" style="color: var(--pc-text-muted)">{{ node.isFolder ? '' : formatBytes(node.size) }}</td>
    <td class="hidden whitespace-nowrap px-2 text-right md:table-cell" style="color: var(--pc-text-muted)">{{ formatDate(node.updatedAt) }}</td>
    <td class="w-10 text-right" @click.stop>
      <el-dropdown trigger="click" :persistent="false" @command="(c: string) => emit('command', c)">
        <el-button size="small" text :icon="MoreFilled" :aria-label="t('drive.more')" />
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="open">{{ t('drive.open') }}</el-dropdown-item>
            <el-dropdown-item v-if="!node.isFolder" command="download">{{ t('drive.download') }}</el-dropdown-item>
            <el-dropdown-item v-if="!node.isFolder" command="send">{{ t('drive.sendToChat') }}</el-dropdown-item>
            <el-dropdown-item v-if="canManage" command="share">{{ t('drive.share') }}</el-dropdown-item>
            <el-dropdown-item v-if="canWrite" command="rename" divided>{{ t('drive.rename') }}</el-dropdown-item>
            <el-dropdown-item v-if="canManage" command="move">{{ t('drive.move') }}</el-dropdown-item>
            <el-dropdown-item v-if="canWrite" command="delete">{{ t('drive.delete') }}</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </td>
  </tr>
</template>
