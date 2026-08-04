/* Shared helpers: theme, toasts, API calls. No third-party dependencies. */
(function () {
    'use strict';

    var root = document.documentElement;

    // ------------------------------------------------------------------ theme

    function currentTheme() {
        if (root.dataset.theme) return root.dataset.theme;
        return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
    }

    function paintThemeIcon() {
        var dark = currentTheme() === 'dark';
        document.querySelectorAll('[data-theme-icon] use').forEach(function (u) {
            u.setAttribute('href', dark ? '#i-sun' : '#i-moon');
        });
    }

    function toggleTheme() {
        var next = currentTheme() === 'dark' ? 'light' : 'dark';
        root.dataset.theme = next;
        try { localStorage.setItem('tp-theme', next); } catch (e) { }
        paintThemeIcon();
    }

    // ------------------------------------------------------------------ toast

    function toast(message, kind) {
        var box = document.getElementById('toasts');
        if (!box) return;

        var el = document.createElement('div');
        el.className = 'toast toast--' + (kind === 'err' ? 'err' : 'ok');
        el.setAttribute('role', kind === 'err' ? 'alert' : 'status');
        el.innerHTML =
            '<svg width="15" height="15" style="margin-top:2px;flex:0 0 15px"><use href="' +
            (kind === 'err' ? '#i-alert' : '#i-check') + '"/></svg><span></span>' +
            '<button type="button" aria-label="Close"><svg width="13" height="13"><use href="#i-x"/></svg></button>';
        el.querySelector('span').textContent = message;

        function close() { el.remove(); }
        el.querySelector('button').addEventListener('click', close);
        box.appendChild(el);

        // Keep at most 4 toasts; errors linger longer so they can be read.
        while (box.children.length > 4) box.firstElementChild.remove();
        setTimeout(close, kind === 'err' ? 9000 : 4000);
    }

    // -------------------------------------------------------------------- api

    /* Prefixes a path with the current module: '/explorer/tables' -> '/billing/explorer/tables'.
       Applied inside request() rather than trusting each call site to remember: forget it once and
       that request silently runs against the default module while the page shows another database —
       editing the wrong database while the UI still displays the right name.
       The login page belongs to no module, so /login/* is passed through. */
    function u(path) {
        var mod = document.documentElement.dataset.module;
        if (!mod || typeof path !== 'string' || path.charAt(0) !== '/') return path;
        if (path.indexOf('/login/') === 0) return path;
        if (path.indexOf('/' + mod + '/') === 0) return path;
        return '/' + mod + path;
    }

    async function request(url, options) {
        var res = await fetch(u(url), options);
        var json;
        try {
            json = await res.json();
        } catch (e) {
            throw new Error('Server did not return JSON (HTTP ' + res.status + ')');
        }

        if (json.Status === 'UsernotFound') {
            location.href = '/login/index?returnUrl=' + encodeURIComponent(location.pathname + location.search);
            throw new Error('Session expired');
        }
        if (json.Status !== 'OK') {
            throw new Error(json.Message || 'Unknown error');
        }
        return json.Data;
    }

    function get(url) {
        return request(url, { headers: { 'X-Requested-With': 'fetch' } });
    }

    function post(url, body) {
        return request(url, {
            method: 'POST',
            headers: { 'X-Requested-With': 'fetch', 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
    }

    function postForm(url, data) {
        var form = new URLSearchParams();
        Object.keys(data).forEach(function (k) { form.append(k, data[k] == null ? '' : data[k]); });
        return request(url, {
            method: 'POST',
            headers: { 'X-Requested-With': 'fetch', 'Content-Type': 'application/x-www-form-urlencoded' },
            body: form.toString()
        });
    }

    // ------------------------------------------------------------------- misc

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function num(n) {
        return (n == null || n < 0) ? '?' : n.toLocaleString('vi-VN');
    }

    /** CSV export with a BOM so Excel reads the encoding correctly; ";" separator to match the locale. */
    function downloadCsv(filename, columns, rows) {
        var quote = function (v) {
            if (v == null) return '';
            var s = String(v);
            return /[",\n;]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
        };
        var lines = [columns.map(quote).join(';')];
        rows.forEach(function (r) {
            lines.push(columns.map(function (c) { return quote(r[c]); }).join(';'));
        });

        var blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        a.click();
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    }

    window.TP = {
        u: u,
        toast: toast,
        get: get,
        post: post,
        postForm: postForm,
        escapeHtml: escapeHtml,
        num: num,
        downloadCsv: downloadCsv,
        toggleTheme: toggleTheme
    };

    // ---------------------------------------------------------------- sidebar

    function initNav() {
        var nav = document.getElementById('nav');
        var scrim = document.getElementById('navScrim');

        // Collapse to icons only (wide screens) — remembered in localStorage.
        var collapse = document.getElementById('navToggle');
        if (collapse) {
            collapse.addEventListener('click', function () {
                var collapsed = root.dataset.nav === 'collapsed';
                if (collapsed) delete root.dataset.nav; else root.dataset.nav = 'collapsed';
                try { localStorage.setItem('tp-nav', collapsed ? 'expanded' : 'collapsed'); } catch (e) { }
            });
        }

        // Drawer mode (narrow screens).
        function setDrawer(open) {
            if (nav) nav.dataset.open = String(open);
            if (scrim) scrim.dataset.open = String(open);
        }

        var drawer = document.getElementById('navDrawerToggle');
        if (drawer && nav) {
            drawer.addEventListener('click', function () { setDrawer(nav.dataset.open !== 'true'); });
        }
        if (scrim) scrim.addEventListener('click', function () { setDrawer(false); });
        if (nav) {
            nav.addEventListener('click', function (e) {
                if (e.target.closest('a.nav-item')) setDrawer(false);
            });
        }
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') setDrawer(false);
        });
    }

    /* Switching module = rewriting the first URL segment, staying on the same page. Trailing parts
       (a blueprint id…) are dropped: that id belongs to the old module and would not resolve here. */
    function initModuleSwitch() {
        var sel = document.getElementById('moduleSwitch');
        if (!sel) return;

        sel.addEventListener('change', function () {
            var parts = location.pathname.split('/').filter(Boolean);
            if (parts[0] === document.documentElement.dataset.module) parts.shift();

            location.href = '/' + [sel.value].concat(parts.slice(0, 2)).join('/');
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        paintThemeIcon();
        var t = document.getElementById('themeToggle');
        if (t) t.addEventListener('click', toggleTheme);
        initNav();
        initModuleSwitch();
    });
})();
