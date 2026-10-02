/* Tab workspace: several ToolPlus pages open at once in one window. Every tab is an iframe of an
   ordinary page, which hides its own topbar and menu (data-embed, see _Layout) — so every page works
   in a tab unchanged, and keeps its state (results, filters, scroll) while another tab is in front. */
(function () {
    'use strict';

    // Opened inside one of its own tabs: hand the URL to the real workspace rather than nest another.
    if (window.self !== window.top) { window.top.location.href = location.href; return; }

    var KEY = 'tp-tabs-state';
    // Each tab is a whole page kept alive in memory; past this the browser starts to struggle.
    var MAX_TABS = 20;

    var root = document.documentElement;
    var $ = function (id) { return document.getElementById(id); };
    var esc = TP.escapeHtml;

    var ws = $('ws'), bar = $('wsTabs'), frames = $('wsFrames'), sel = $('moduleSwitch');

    var S = { tabs: [], active: null, seq: 0, drag: null };

    // Modules this user can open: the switch lists them; with only one there is no switch at all.
    var modules = sel
        ? Array.prototype.map.call(sel.options, function (o) { return { key: o.value, name: o.textContent.trim() }; })
        : [{ key: root.dataset.module, name: ((document.querySelector('.module-tag') || {}).textContent || root.dataset.module).trim() }];

    function findModule(key) {
        key = (key || '').toLowerCase();
        for (var i = 0; i < modules.length; i++) if (modules[i].key.toLowerCase() === key) return modules[i];
        return null;
    }

    /** '/hrm/explorer/index?table=x' -> { module: 'hrm', parts: ['explorer', 'index'], tail: '?table=x' } */
    function splitPath(url) {
        var a = new URL(url, location.origin);
        var parts = a.pathname.split('/').filter(Boolean);
        var m = parts.length ? findModule(parts[0]) : null;
        if (m) parts.shift();
        return { module: m ? m.key : null, parts: parts, tail: a.search + a.hash };
    }

    /** A module-less URL is served by the default module, which is what the workspace was opened on. */
    function moduleOf(t) { return splitPath(t.url).module || root.dataset.module; }

    function moduleName(key) { var m = findModule(key); return m ? m.name : key; }

    /** Only same-origin page paths become tabs — never the login pages, never the workspace itself. */
    function clean(url) {
        var a;
        try { a = new URL(url, location.origin); } catch (e) { return null; }
        if (a.origin !== location.origin) return null;
        var p = splitPath(a.pathname).parts.join('/').toLowerCase();
        if (p === 'home/tabs' || p.indexOf('login') === 0) return null;
        return a.pathname + a.search + a.hash;
    }

    function isHome(url) {
        var p = splitPath(url).parts.join('/').toLowerCase();
        return p === '' || p === 'home' || p === 'home/index';
    }

    function homeUrl() {
        return S.active ? '/' + moduleOf(S.active) + '/home/index' : ws.dataset.home;
    }

    function byId(id) {
        for (var i = 0; i < S.tabs.length; i++) if (S.tabs[i].id === +id) return S.tabs[i];
        return null;
    }

    // ---------------------------------------------------------------- frames

    function newTab(url, title) {
        return { id: ++S.seq, url: url, title: title || '', frame: null, loading: false };
    }

    /* Frames are created on first view, not on restore: a dozen restored tabs loading at once would
       each fire their first queries against a production database for pages nobody is looking at. */
    function ensureFrame(t) {
        if (t.frame) return;
        var f = document.createElement('iframe');
        f.className = 'ws-frame';
        f.title = t.title || 'Tab';
        f.src = t.url;
        f.addEventListener('load', function () { onFrameLoad(t); });
        frames.appendChild(f);
        t.frame = f;
        t.loading = true;
    }

    /** Copies the URL and title the frame is really on — it may have been navigated from inside. */
    function readFrame(t) {
        if (!t.frame) return;
        try {
            var w = t.frame.contentWindow;
            if (w.location.href === 'about:blank') return;
            t.url = w.location.pathname + w.location.search + w.location.hash;
            var title = w.document.title.replace(/\s*·\s*ToolPlus\s*$/, '').trim();
            if (title) t.title = title;
            t.frame.title = t.title;
        } catch (e) { /* not reachable mid-navigation — keep what we had */ }
    }

    function onFrameLoad(t) {
        t.loading = false;
        readFrame(t);

        try {
            var w = t.frame.contentWindow;
            // A page may retitle itself after load (Explorer names the table it shows) and update its
            // URL with replaceState at the same moment; the tab follows both.
            new MutationObserver(function () { readFrame(t); refresh(t); })
                .observe(w.document.head, { subtree: true, childList: true, characterData: true });
            // Navigation started from inside the tab: show it as loading until the next load event.
            w.addEventListener('pagehide', function () { t.loading = true; render(); });
        } catch (e) { }

        refresh(t);
    }

    function refresh(t) {
        if (t === S.active) syncChrome();
        render();
        save();
    }

    /** Closing a tab drops its page without the browser asking — so ask the page the same way it does. */
    function hasUnsaved(t) {
        if (!t.frame) return false;
        try {
            var w = t.frame.contentWindow;
            var ev = new w.Event('beforeunload', { cancelable: true });
            w.dispatchEvent(ev);
            return ev.defaultPrevented;
        } catch (e) { return false; }
    }

    // ------------------------------------------------------------------ tabs

    function activate(t) {
        S.active = t;
        ensureFrame(t);
        // visibility rather than display:none: a tab opened in the background still lays out at
        // full size, so pages that measure themselves on load (blueprint fit-to-view) come out right.
        S.tabs.forEach(function (x) { if (x.frame) x.frame.classList.toggle('is-active', x === t); });
        readFrame(t);
        syncChrome();
        render();
        save();
    }

    function open(url, background) {
        url = clean(url);
        if (!url) return null;
        if (S.tabs.length >= MAX_TABS) {
            TP.toast('Đã mở tối đa ' + MAX_TABS + ' tab — đóng bớt tab không dùng trước đã', 'err');
            return null;
        }

        var t = newTab(url, '');
        S.tabs.push(t);
        if (background && S.active) {
            ensureFrame(t);
            render();
            save();
        } else {
            activate(t);
        }
        return t;
    }

    function navigate(t, url) {
        url = clean(url);
        if (!url) return;
        if (!t.frame) { t.url = url; ensureFrame(t); render(); return; }
        // Through location rather than src, so the page's own "unsaved changes" prompt still runs.
        try { t.frame.contentWindow.location.href = url; } catch (e) { t.frame.src = url; }
    }

    function close(t) {
        if (hasUnsaved(t) && !window.confirm('Tab "' + t.title + '" còn thay đổi chưa lưu. Đóng và bỏ các thay đổi đó?')) return;

        var i = S.tabs.indexOf(t);
        S.tabs.splice(i, 1);
        if (t.frame) t.frame.remove();

        if (S.active !== t) { render(); save(); return; }

        S.active = null;
        var next = S.tabs[i] || S.tabs[i - 1];
        if (next) activate(next);
        else open(ws.dataset.home);   // never leave the workspace with nothing in it
    }

    // --------------------------------------------------------------- chrome

    /* The topbar must always name the database of the tab in front — this tool edits real data,
       and two tabs can be on two different databases. */
    function syncChrome() {
        var t = S.active;
        document.title = (t && t.title ? t.title + ' · ' : '') + 'ToolPlus';
        if (sel && t) sel.value = moduleOf(t);
        highlightNav();
    }

    /** The menu was rendered for /home/tabs; mark the item of the page shown in the active tab instead. */
    function highlightNav() {
        function rel(url) {
            var parts = splitPath(url).parts;
            if (parts.length === 0) parts = ['home'];
            if (parts.length === 1) parts.push('index');
            return '/' + parts.join('/').toLowerCase();
        }

        var cur = S.active ? rel(S.active.url) : '';
        var best = null, len = -1;
        document.querySelectorAll('#nav a.nav-item').forEach(function (a) {
            a.removeAttribute('aria-current');
            var r = rel(a.pathname);
            if ((cur === r || cur.indexOf(r + '/') === 0) && r.length > len) { best = a; len = r.length; }
        });
        if (best) best.setAttribute('aria-current', 'page');
    }

    function render() {
        // Name the database on every tab once they no longer all point at the same one.
        var mods = {};
        S.tabs.forEach(function (t) { mods[moduleOf(t)] = true; });
        var multi = Object.keys(mods).length > 1;

        bar.innerHTML = S.tabs.map(function (t) {
            var on = t === S.active, mod = moduleName(moduleOf(t)), title = t.title || 'Tab mới';
            return '<div class="ws-tab" role="tab" draggable="true" data-id="' + t.id + '"'
                + ' aria-selected="' + on + '" tabindex="' + (on ? 0 : -1) + '"'
                + ' title="' + esc(title + ' — ' + mod) + '">'
                + (t.loading ? '<span class="ws-spin" aria-hidden="true"></span>' : '')
                + (multi ? '<span class="ws-mod">' + esc(mod) + '</span>' : '')
                + '<span class="ws-title">' + esc(title) + '</span>'
                + '<button type="button" class="ws-close" data-close tabindex="-1" title="Đóng tab" aria-label="Đóng tab">'
                + '<svg width="12" height="12"><use href="#i-x"/></svg></button>'
                + '</div>';
        }).join('');

        var a = bar.querySelector('[aria-selected="true"]');
        if (a) a.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    }

    function save() {
        S.tabs.forEach(readFrame);
        try {
            localStorage.setItem(KEY, JSON.stringify({
                tabs: S.tabs.map(function (t) { return { url: t.url, title: t.title }; }),
                active: S.tabs.indexOf(S.active)
            }));
        } catch (e) { }
    }

    // ---------------------------------------------------------------- events

    function tabOf(e) {
        var el = e.target.closest('.ws-tab');
        return el ? byId(el.dataset.id) : null;
    }

    bar.addEventListener('click', function (e) {
        var t = tabOf(e);
        if (!t) return;
        if (e.target.closest('[data-close]')) close(t);
        else if (t !== S.active) activate(t);
    });

    // Middle-click closes, as in a browser. mousedown is cancelled too, or it starts autoscroll.
    bar.addEventListener('mousedown', function (e) { if (e.button === 1) e.preventDefault(); });
    bar.addEventListener('auxclick', function (e) {
        var t = tabOf(e);
        if (e.button !== 1 || !t) return;
        e.preventDefault();
        close(t);
    });

    // Double-click on the empty part of the strip opens a new tab, as in a browser.
    ws.querySelector('.ws-bar').addEventListener('dblclick', function (e) {
        if (!tabOf(e) && !e.target.closest('button')) open(homeUrl());
    });

    bar.addEventListener('keydown', function (e) {
        var t = tabOf(e);
        if (!t) return;
        var i = S.tabs.indexOf(t);
        var to = e.key === 'ArrowRight' ? S.tabs[i + 1] : e.key === 'ArrowLeft' ? S.tabs[i - 1] : null;
        if (to) { e.preventDefault(); activate(to); bar.querySelector('[aria-selected="true"]').focus(); }
        else if (e.key === 'Delete') { e.preventDefault(); close(t); }
    });

    // A long row of tabs scrolls sideways with the ordinary mouse wheel.
    bar.addEventListener('wheel', function (e) {
        if (e.deltaY && !e.deltaX) { bar.scrollLeft += e.deltaY; e.preventDefault(); }
    }, { passive: false });

    // Drag to reorder. The DOM node is moved rather than re-rendered: replacing the element being
    // dragged cancels the drag in some browsers. Iframes are never moved — that would reload them.
    bar.addEventListener('dragstart', function (e) {
        S.drag = e.target.closest('.ws-tab');
        if (!S.drag) return;
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', '');
        S.drag.classList.add('is-dragging');
    });
    bar.addEventListener('dragover', function (e) {
        var over = e.target.closest('.ws-tab');
        if (!S.drag || !over) return;
        e.preventDefault();
        if (over === S.drag) return;
        var r = over.getBoundingClientRect();
        bar.insertBefore(S.drag, e.clientX > r.left + r.width / 2 ? over.nextSibling : over);
    });
    bar.addEventListener('dragend', function () {
        if (!S.drag) return;
        S.drag.classList.remove('is-dragging');
        S.drag = null;
        var order = Array.prototype.map.call(bar.children, function (el) { return byId(el.dataset.id); });
        S.tabs = order.filter(Boolean);
        save();
    });

    $('wsNew').addEventListener('click', function () { open(homeUrl()); });

    /* Menu links (and the brand) open in the active tab; Ctrl/⌘ or middle-click opens a new one.
       They were rendered for the workspace's module, so they are re-pointed at the active tab's —
       the menu acts on the database the topbar shows. A link carrying an id (a pinned blueprint)
       keeps its module: that id exists only in the module that rendered it. */
    function retarget(path) {
        var p = splitPath(path);
        if (!S.active || !p.module || p.parts.length > 2) return path;
        return '/' + [moduleOf(S.active)].concat(p.parts).join('/');
    }

    function onChromeLink(e) {
        if (e.defaultPrevented || (e.button !== 0 && e.button !== 1)) return;
        var a = e.target.closest('#nav a.nav-item, .topbar a.brand');
        if (!a) return;
        e.preventDefault();

        var url = retarget(a.pathname) + a.search;
        if (e.ctrlKey || e.metaKey || e.button === 1) open(url, true);
        else if (S.active) navigate(S.active, url);
        else open(url);
    }
    document.addEventListener('click', onChromeLink);
    document.addEventListener('auxclick', onChromeLink);

    // Switching module moves the active tab to the same page of the other database; the other tabs
    // stay where they are. The ids after controller/action belong to the old module and are dropped.
    if (sel) {
        sel.addEventListener('change', function () {
            try { localStorage.setItem('tp-last-module', sel.value); } catch (e) { }
            var t = S.active;
            if (!t) return;
            var p = splitPath(t.url);
            navigate(t, '/' + [sel.value].concat(p.parts.slice(0, 2)).join('/'));
            // Until the new page has loaded (or if its unsaved-changes prompt is refused) the tab is
            // still on the old database, and the switch must keep saying so.
            syncChrome();
        });
    }

    // Ctrl/middle-click on a link inside a tab (site.js initEmbedLinks).
    window.addEventListener('message', function (e) {
        if (e.origin !== location.origin || !e.data || e.data.tp !== 'open') return;
        var fromTab = S.tabs.some(function (t) { return t.frame && t.frame.contentWindow === e.source; });
        if (fromTab) open(e.data.url, true);
    });

    var exit = $('tabsExit');
    if (exit) {
        exit.addEventListener('click', function () {
            save();
            try { localStorage.setItem('tp-tabs', 'off'); } catch (e) { }
            location.href = S.active ? S.active.url : ws.dataset.home;
        });
    }

    window.addEventListener('pagehide', save);

    // ------------------------------------------------------------------ init

    // Being here turns tab mode on: from now on a page opened on its own lands here as a tab.
    try { localStorage.setItem('tp-tabs', 'on'); } catch (e) { }

    var saved = null;
    try { saved = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch (e) { }
    if (saved && Array.isArray(saved.tabs)) {
        saved.tabs.slice(0, MAX_TABS).forEach(function (x) {
            var url = x && clean(x.url);
            if (url) S.tabs.push(newTab(url, x.title));
        });
    }

    var want = clean(new URLSearchParams(location.search).get('open') || '');
    history.replaceState(null, '', location.pathname);

    var start = null;
    // A plain visit to the home page just means "open ToolPlus": carry on where it was left,
    // rather than piling up one more home tab on every visit.
    if (want && !(isHome(want) && S.tabs.length)) {
        start = S.tabs.filter(function (t) { return t.url.toLowerCase() === want.toLowerCase(); })[0] || null;
        if (!start) {
            if (S.tabs.length >= MAX_TABS) S.tabs.shift();
            start = newTab(want, '');
            S.tabs.push(start);
        }
    }
    start = start || (saved && S.tabs[saved.active]) || S.tabs[0];
    if (!start) { start = newTab(ws.dataset.home, ''); S.tabs.push(start); }

    activate(start);
})();
