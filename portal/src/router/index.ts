import { createRouter, createWebHistory } from 'vue-router'
import { useUserStore } from '../stores/user'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      name: 'landing',
      component: () => import('../views/Landing.vue'),
      meta: { titleKey: 'route.landing' },
    },
    {
      path: '/auth',
      name: 'auth',
      component: () => import('../views/Auth.vue'),
      meta: { titleKey: 'route.auth' },
    },
    {
      path: '/app',
      component: () => import('../layouts/DashboardLayout.vue'),
      redirect: '/app/overview',
      meta: { requiresAuth: true, titleKey: 'route.overview' },
      children: [
        {
          path: 'overview',
          name: 'overview',
          component: () => import('../views/dashboard/Overview.vue'),
          meta: { requiresAuth: true, titleKey: 'route.overview' },
        },
        {
          path: 'sponsor',
          name: 'sponsor',
          component: () => import('../views/dashboard/Sponsor.vue'),
          meta: { requiresAuth: true, titleKey: 'route.billing' },
        },
        {
          path: 'orders',
          name: 'orders',
          component: () => import('../views/dashboard/Orders.vue'),
          meta: { requiresAuth: true, titleKey: 'route.orders' },
        },
        {
          path: 'usage',
          name: 'usage',
          component: () => import('../views/dashboard/Usage.vue'),
          meta: { requiresAuth: true, titleKey: 'route.usage' },
        },
      ],
    },
    {
      path: '/:pathMatch(.*)*',
      redirect: '/',
    },
  ],
})

router.beforeEach(async (to) => {
  const userStore = useUserStore()

  if (!userStore.initialized) {
    await userStore.bootstrap()
  }

  if (to.meta.requiresAuth && !userStore.isAuthenticated) {
    return {
      path: '/auth',
      query: { redirect: to.fullPath },
    }
  }

  if (to.path === '/auth' && userStore.isAuthenticated) {
    return '/app/overview'
  }

  return true
})

export default router
