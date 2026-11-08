import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'

declare module 'vue-router' {
  interface RouteMeta {
    /** 桌面三栏布局中栏显示哪种列表；为空则内容区占满 */
    panel?: 'conversations'
    /** 导航高亮用的分区 */
    section: 'messages' | 'calls' | 'drive' | 'moments' | 'devices' | 'settings' | 'family'
    titleKey: string
    /** 网盘内的分区（导航高亮） */
    driveSection?: 'my' | 'family' | 'gallery' | 'shared' | 'trash'
  }
}

const routes: RouteRecordRaw[] = [
  { path: '/', redirect: '/messages' },
  {
    path: '/messages',
    component: () => import('@/features/chat/MessagesHome.vue'),
    meta: { panel: 'conversations', section: 'messages', titleKey: 'nav.messages' },
  },
  {
    path: '/messages/:id',
    component: () => import('@/features/chat/ChatPage.vue'),
    props: true,
    meta: { panel: 'conversations', section: 'messages', titleKey: 'nav.messages' },
  },
  { path: '/calls', component: () => import('@/features/calls/CallsPage.vue'), meta: { section: 'calls', titleKey: 'nav.calls' } },
  {
    path: '/drive',
    component: () => import('@/features/drive/DrivePage.vue'),
    meta: { section: 'drive', titleKey: 'nav.drive' },
    children: [
      { path: '', redirect: '/drive/my' },
      {
        path: 'my/:folderId?',
        component: () => import('@/features/drive/FileBrowser.vue'),
        props: (r) => ({ kind: 'my', folderId: r.params.folderId || undefined }),
        meta: { section: 'drive', titleKey: 'nav.drive', driveSection: 'my' },
      },
      {
        path: 'family/:folderId?',
        component: () => import('@/features/drive/FileBrowser.vue'),
        props: (r) => ({ kind: 'family', folderId: r.params.folderId || undefined }),
        meta: { section: 'drive', titleKey: 'nav.drive', driveSection: 'family' },
      },
      {
        path: 'shared',
        component: () => import('@/features/drive/SharedView.vue'),
        meta: { section: 'drive', titleKey: 'nav.drive', driveSection: 'shared' },
      },
      {
        path: 'shared/:spaceId/:folderId',
        component: () => import('@/features/drive/FileBrowser.vue'),
        props: (r) => ({ kind: 'shared', spaceId: r.params.spaceId, folderId: r.params.folderId }),
        meta: { section: 'drive', titleKey: 'nav.drive', driveSection: 'shared' },
      },
      {
        path: 'gallery',
        component: () => import('@/features/drive/GalleryView.vue'),
        meta: { section: 'drive', titleKey: 'nav.drive', driveSection: 'gallery' },
      },
      {
        path: 'trash',
        component: () => import('@/features/drive/TrashView.vue'),
        meta: { section: 'drive', titleKey: 'nav.drive', driveSection: 'trash' },
      },
    ],
  },
  { path: '/moments', component: () => import('@/features/moments/MomentsPage.vue'), meta: { section: 'moments', titleKey: 'nav.moments' } },
  { path: '/devices', component: () => import('@/features/remote/DevicesPage.vue'), meta: { section: 'devices', titleKey: 'nav.devices' } },
  { path: '/settings', component: () => import('@/features/settings/SettingsPage.vue'), meta: { section: 'settings', titleKey: 'nav.settings' } },
  { path: '/family', component: () => import('@/features/family/FamilyPage.vue'), meta: { section: 'family', titleKey: 'nav.family' } },
  { path: '/:pathMatch(.*)*', redirect: '/messages' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})
