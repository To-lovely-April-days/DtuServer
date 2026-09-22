/* ============================================================
   MaxChemical 控制台 · 应用外壳（唯一真相源）

   用法：
     <body>
       <div id="shell-mount"></div>
       <template id="page-content"> …页面内容… </template>
       <script src="/shell.js"></script>
       <script>Shell.mount('models');</script>
     </body>

   导航条目只在下面的 NAV 里定义一次，加页面改这一处即可。
   设备总览页内部的视图（拓扑/站点/3D）用 view 标记：
   在总览页上由它自己接管切换，在其它页面上则跳回 / 并带 hash。
   ============================================================ */
(function (global) {

  const ICON = {
    dashboard: '<path d="M3 3v18h18" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/><path d="M7 14l3-4 3 2 4-6" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>',
    devices:   '<path d="M21 16V8a2 2 0 00-1-1.73l-7-4a2 2 0 00-2 0l-7 4A2 2 0 003 8v8a2 2 0 001 1.73l7 4a2 2 0 002 0l7-4A2 2 0 0021 16z" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/><path d="M3.3 7L12 12l8.7-5M12 22V12" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>',
    manage:    '<rect x="3" y="4" width="18" height="16" rx="2" stroke="currentColor" stroke-width="1.7"/><path d="M7 9h10M7 13h6" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>',
    gateways:  '<rect x="3" y="8" width="18" height="9" rx="2" stroke="currentColor" stroke-width="1.7"/><path d="M7 12.5h.01M11 12.5h.01" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/><path d="M12 8V5M9 3.5h6" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>',
    models:    '<path d="M4 6h16M4 12h10M4 18h7" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/><circle cx="18" cy="15" r="3" stroke="currentColor" stroke-width="1.7"/>',
    topology:  '<circle cx="6" cy="6" r="2.4" stroke="currentColor" stroke-width="1.7"/><circle cx="6" cy="18" r="2.4" stroke="currentColor" stroke-width="1.7"/><circle cx="18" cy="12" r="2.4" stroke="currentColor" stroke-width="1.7"/><path d="M8 7l8 4M8 17l8-4" stroke="currentColor" stroke-width="1.7"/>',
    tickets:   '<rect x="3" y="7" width="18" height="13" rx="2" stroke="currentColor" stroke-width="1.7"/><path d="M8 7V5a2 2 0 012-2h4a2 2 0 012 2v2" stroke="currentColor" stroke-width="1.7"/>',
    sites:     '<path d="M20 10c0 6-8 11-8 11s-8-5-8-11a8 8 0 0116 0z" stroke="currentColor" stroke-width="1.7"/><circle cx="12" cy="10" r="2.6" stroke="currentColor" stroke-width="1.7"/>',
    lab3d:     '<path d="M12 2l8 4.5v9L12 20l-8-4.5v-9L12 2z" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/><path d="M4 6.5l8 4.5 8-4.5M12 11v9" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/>',
    analytics: '<path d="M12 3l1.8 4.6L18 9l-4.2 1.4L12 15l-1.8-4.6L6 9l4.2-1.4L12 3z" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round"/>',
    devcenter: '<path d="M8 9l-3 3 3 3M16 9l3 3-3 3M13 5l-2 14" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>',
    settings:  '<path d="M12 15a3 3 0 100-6 3 3 0 000 6z" stroke="currentColor" stroke-width="1.7"/><path d="M19.4 15a1.65 1.65 0 00.33 1.82 2 2 0 11-2.83 2.83 1.65 1.65 0 00-1.82-.33 1.65 1.65 0 00-1 1.51 2 2 0 01-4 0 1.65 1.65 0 00-1-1.51 1.65 1.65 0 00-1.82.33 2 2 0 11-2.83-2.83A1.65 1.65 0 004.6 15a1.65 1.65 0 00-1.51-1 2 2 0 010-4 1.65 1.65 0 001.51-1 1.65 1.65 0 00-.33-1.82 2 2 0 112.83-2.83A1.65 1.65 0 009 4.6a1.65 1.65 0 001-1.51 2 2 0 014 0 1.65 1.65 0 001 1.51 1.65 1.65 0 001.82-.33 2 2 0 112.83 2.83A1.65 1.65 0 0019.4 9c.61.2 1.03.78 1 1.51a2 2 0 010 4z" stroke="currentColor" stroke-width="1.3"/>',
  };

  // key 用来标记当前页；href = 独立页面；view = 设备总览页内部视图
  const NAV = [
    { key: 'dashboard', label: '数据看板',  todo: true },
    { key: 'devices',   label: '设备总览',  href: '/' },
    { key: 'manage',    label: '设备管理',  href: '/devices.html' },
    { key: 'gateways',  label: '网关设备',  href: '/gateways.html' },
    { key: 'models',    label: '物模型',    href: '/models.html' },
    { key: 'topology',  label: '设备拓扑',  view: 'topology' },
    { key: 'tickets',   label: '工单管理',  todo: true },
    { key: 'sites',     label: '现场站点',  view: 'sites' },
    { key: 'lab3d',     label: '3D 实验室', view: 'lab3d' },
    { key: 'analytics', label: '智能分析',  todo: true },
    { key: 'devcenter', label: '开发者中心', href: '/swagger', blank: true },
  ];
  const NAV_FOOT = [
    { key: 'settings', label: '系统设置', href: '/account.html' },
    { key: 'logout',   label: '退出登录', act: 'logout' },
  ];
  ICON.logout = '<path d="M9 21H5a2 2 0 01-2-2V5a2 2 0 012-2h4" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/><path d="M16 17l5-5-5-5M21 12H9" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>';

  function navItem(n, active) {
    const cls = 'side-item' + (n.key === active ? ' active' : '');
    const body = `<span class="side-ico-box"><svg width="20" height="20" viewBox="0 0 24 24" fill="none">${ICON[n.key] || ''}</svg></span>
      <span class="side-label">${n.label}</span>`;
    if (n.href)
      return `<a class="${cls}" data-nav="${n.key}" href="${n.href}"${n.blank ? ' target="_blank"' : ''} title="${n.label}">${body}</a>`;
    if (n.act)
      return `<div class="${cls}" data-nav="${n.key}" data-act="${n.act}" title="${n.label}">${body}</div>`;
    // todo: 尚未实现的条目，保持占位、点击无响应（与重构前一致，别抢走当前页高亮）
    if (n.todo)
      return `<div class="${cls}" data-nav="${n.key}" title="${n.label}" style="opacity:.55;cursor:default">${body}</div>`;
    return `<div class="${cls}" data-nav="${n.key}" data-view="${n.view}" title="${n.label}">${body}</div>`;
  }

  const Shell = {
    /**
     * 渲染顶栏 + 侧栏 + 内容区。
     * @param {string} active 当前页的 nav key
     * @param {object} [opt]  { footer: '底栏 HTML' }
     */
    mount(active, opt) {
      opt = opt || {};
      const host = document.getElementById('shell-mount');
      if (!host) return;

      host.outerHTML = `
<header class="topbar">
  <a class="brand" href="/">
    <span class="logo"><img src="/logo.svg" alt="MaxChemical" /></span>
    <span class="brand-name">MaxChemical</span>
    <span class="brand-badge mono">IoT&nbsp;CLOUD</span>
  </a>
  <div class="top-right">
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none"><path d="M18 8a6 6 0 10-12 0c0 7-3 9-3 9h18s-3-2-3-9" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round"/><path d="M13.7 21a2 2 0 01-3.4 0" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/></svg>
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" id="shGear"><circle cx="12" cy="12" r="3" stroke="currentColor" stroke-width="1.7"/><path d="M19.4 15a1.65 1.65 0 00.33 1.82 2 2 0 11-2.83 2.83 1.65 1.65 0 00-2.82 1.18 2 2 0 01-4 0 1.65 1.65 0 00-2.82-1.18 2 2 0 11-2.83-2.83A1.65 1.65 0 004.6 15a2 2 0 010-4 1.65 1.65 0 00.33-1.82 2 2 0 112.83-2.83A1.65 1.65 0 0110.6 5.2a2 2 0 014 0 1.65 1.65 0 002.82 1.15 2 2 0 112.83 2.83A1.65 1.65 0 0019.4 11a2 2 0 010 4z" stroke="currentColor" stroke-width="1.3"/></svg>
    <span class="avatar" id="shAvatar">
      <svg width="14" height="14" viewBox="0 0 24 24" fill="none"><circle cx="12" cy="8" r="3.4" stroke="currentColor" stroke-width="1.8"/><path d="M5 20a7 7 0 0114 0" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>
      <span class="dot"></span></span>
  </div>
</header>
<div class="shell">
  <nav class="sidebar" id="shSidebar">
    <div class="nav-eyebrow">导航</div>
    ${NAV.map(n => navItem(n, active)).join('')}
    <div class="side-spacer"></div>
    ${NAV_FOOT.map(n => navItem(n, active)).join('')}
    <div class="side-divider"></div>
    <div class="side-item side-toggle" id="shToggle" title="收起/展开">
      <span class="side-ico-box"><svg class="tg" width="20" height="20" viewBox="0 0 24 24" fill="none"><path d="M15 18l-6-6 6-6" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round"/></svg></span>
      <span class="side-label">收起菜单</span>
    </div>
  </nav>
  <main class="content" id="shContent">
    <div class="content-scroll" id="shMain"></div>
  </main>
</div>`;

      // 页面内容写在 <template id="page-content"> 里，这里搬进内容区
      const tpl = document.getElementById('page-content');
      if (tpl) document.getElementById('shMain').innerHTML = tpl.innerHTML;

      // 常驻底栏（分页等）放 <template id="page-footer">，挂在滚动区外面
      const foot = document.getElementById('page-footer');
      if (foot) document.getElementById('shContent').insertAdjacentHTML('beforeend', foot.innerHTML);
      else if (opt.footer) document.getElementById('shContent').insertAdjacentHTML('beforeend', opt.footer);

      document.getElementById('shAvatar').onclick = () => location.href = '/account.html';
      document.getElementById('shGear').onclick = () => location.href = '/account.html';

      // 侧栏折叠状态跨页面记忆
      const sb = document.getElementById('shSidebar');
      if (localStorage.getItem('shell.sidebar') === 'collapsed') sb.classList.add('collapsed');
      document.getElementById('shToggle').onclick = () => {
        sb.classList.toggle('collapsed');
        localStorage.setItem('shell.sidebar', sb.classList.contains('collapsed') ? 'collapsed' : '');
      };

      // 内部视图：总览页自己接管（设置 Shell.onView），其它页面跳回总览并带 hash
      document.querySelectorAll('[data-view]').forEach(el => {
        el.onclick = () => {
          const v = el.dataset.view;
          if (typeof Shell.onView === 'function') Shell.onView(v, el);
          else location.href = '/#' + v;
        };
      });

      // 已经在本页时点自己的导航项：不整页跳转，交给本页处理（总览页借此从 iframe 视图切回列表）
      document.querySelectorAll('a.side-item[data-nav]').forEach(el => {
        const path = new URL(el.getAttribute('href'), location.origin).pathname;
        const here = location.pathname === '/index.html' ? '/' : location.pathname;
        if (path !== here) return;
        el.addEventListener('click', ev => {
          if (typeof Shell.onView !== 'function') return;
          ev.preventDefault();
          Shell.onView(el.dataset.nav, el);
        });
      });

      document.querySelectorAll('[data-act="logout"]').forEach(el => {
        el.onclick = async () => {
          try { await fetch('/auth/logout', { method: 'POST' }); } catch (e) { /* 网络挂了也照样回登录页 */ }
          location.href = '/login.html';
        };
      });
    },

    /** 设备总览页把自己的视图切换函数挂到这里 */
    onView: null,

    /** 高亮某个导航项（总览页切内部视图时用） */
    setActive(key) {
      document.querySelectorAll('.side-item[data-nav]').forEach(el =>
        el.classList.toggle('active', el.dataset.nav === key));
    },

    // ---------- 通用小工具 ----------
    async guard(r) {
      if (r.status === 401) { location.href = '/login.html?next=' + encodeURIComponent(location.pathname + location.search); throw 0; }
      if (r.status === 403) { alert('需要管理员权限'); throw 0; }
      return r;
    },
    async errText(r) {
      try { const d = await r.json(); return d.error || JSON.stringify(d); }
      catch { return '请求失败 (' + r.status + ')'; }
    },
    toast(msg) {
      const d = document.createElement('div');
      d.className = 'toast'; d.textContent = msg;
      document.body.appendChild(d); setTimeout(() => d.remove(), 1800);
    },
    esc: s => (s == null ? '' : String(s)).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c])),
    ago(iso) {
      if (!iso) return '—';
      const s = Math.floor((Date.now() - new Date(iso).getTime()) / 1000);
      if (s < 60) return s + ' 秒前';
      if (s < 3600) return Math.floor(s / 60) + ' 分钟前';
      if (s < 86400) return Math.floor(s / 3600) + ' 小时前';
      return Math.floor(s / 86400) + ' 天前';
    },
  };

  global.Shell = Shell;
  if (!global.$) global.$ = id => document.getElementById(id);
})(window);
