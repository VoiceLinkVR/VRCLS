import { createRouter, createWebHistory } from 'vue-router'
import { useUserStore } from '../stores/user'

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
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
      path: '/authorize',
      name: 'authorize',
      component: () => import('../views/Authorize.vue'),
      meta: { requiresAuth: true, title: '控制台授权登录', allowMigration: true },
    },
    {
      path: '/reset-password',
      name: 'reset-password',
      component: () => import('../views/ResetPassword.vue'),
      meta: { title: '重置密码' },
    },
    {
      path: '/verify-email',
      name: 'verify-email',
      component: () => import('../views/VerifyEmail.vue'),
      meta: { title: '验证邮箱', allowMigration: true },
    },
    {
      path: '/',
      component: () => import('../layouts/DashboardLayout.vue'),
      children: [
        {
          path: 'center',
          name: 'center',
          component: () => import('../views/dashboard/UserCenter.vue'),
          meta: { requiresAuth: true, title: '用户中心', allowMigration: true },
        },
        {
          path: 'overview',
          name: 'overview',
          component: () => import('../views/dashboard/Overview.vue'),
          meta: { requiresAuth: true, titleKey: 'route.overview', allowMigration: true },
        },
        {
          path: 'billing',
          name: 'billing',
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
        {
          path: 'security',
          redirect: '/center',
        },
        {
          path: 'email',
          redirect: '/center',
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
    const redirect = userStore.isMigrationPending
      ? '/center'
      : typeof to.query.redirect === 'string'
        ? to.query.redirect
        : '/overview'
    return redirect
  }

  if (userStore.isAuthenticated && userStore.isMigrationPending && !to.meta.allowMigration) {
    return '/center'
  }

  return true
})

export default router
