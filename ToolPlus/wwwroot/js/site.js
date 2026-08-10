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

    // ------------------------------------------------------------ autocomplete

    var suggestBox = null;
    var suggestState = null;   // { el, items, active, range, pick } for whichever field is open

    function ensureSuggestBox() {
        if (suggestBox) return suggestBox;
        suggestBox = document.createElement('div');
        suggestBox.className = 'tp-suggest';
        suggestBox.hidden = true;
        document.body.appendChild(suggestBox);

        // mousedown (not click) fires before the field's blur handler hides the popup.
        suggestBox.addEventListener('mousedown', function (e) {
            if (!suggestState) return;
            var item = e.target.closest('.tp-suggest-item');
            if (!item) return;
            e.preventDefault();
            suggestState.pick(+item.dataset.i);
        });
        return suggestBox;
    }

    function hideSuggest() {
        if (suggestBox) suggestBox.hidden = true;
        suggestState = null;
    }

    /** Opens upward (bottom edge pinned just above the field) — fields like the SQL console sit at
     *  the bottom of the page, where a popup dropping below the field has no room to be seen at all. */
    function paintSuggest(state) {
        var b = ensureSuggestBox();
        b.innerHTML = state.items.map(function (it, i) {
            return '<div class="tp-suggest-item' + (i === state.active ? ' is-active' : '') + '" data-i="' + i + '">'
                + '<span class="tp-suggest-label">' + escapeHtml(it.label) + '</span>'
                + (it.hint ? '<span class="tp-suggest-hint">' + escapeHtml(it.hint) + '</span>' : '')
                + '</div>';
        }).join('');

        var r = state.el.getBoundingClientRect();
        b.style.left = Math.round(r.left + window.scrollX) + 'px';
        b.style.minWidth = Math.round(r.width) + 'px';
        b.hidden = false;   // must be visible to measure its rendered height below

        b.style.top = Math.round(r.top + window.scrollY - 4 - b.offsetHeight) + 'px';
    }

    /**
     * Inline suggestion popup for a text input/textarea — e.g. {parameter} tokens in a blueprint
     * condition value, or table/column/keyword names in a raw SQL box. Every attached field shares
     * one popup element (only one field can be focused at a time), so fields that get re-rendered
     * often — blueprint condition rows are rebuilt on every edit — never leak floating DOM nodes.
     *
     * opts.getItems(value, caret) returns null/undefined to hide the popup, or
     * { items: [{label, insertText, hint}], range: [start, end] } to show it — [start, end) is the
     * slice of `value` replaced by insertText when an item is picked.
     */
    function attachAutocomplete(el, opts) {
        function pick(i) {
            var it = suggestState && suggestState.items[i];
            if (!it) return;
            var v = el.value, range = suggestState.range;
            var insert = it.insertText != null ? it.insertText : it.label;

            el.value = v.slice(0, range[0]) + insert + v.slice(range[1]);
            var pos = range[0] + insert.length;
            el.focus();
            el.setSelectionRange(pos, pos);
            el.dispatchEvent(new Event('input', { bubbles: true }));
            hideSuggest();
        }

        function update() {
            var res;
            try { res = opts.getItems(el.value, el.selectionStart); } catch (e) { res = null; }
            if (!res || !res.items || !res.items.length) { hideSuggest(); return; }

            suggestState = { el: el, items: res.items.slice(0, 10), range: res.range, active: 0, pick: pick };
            paintSuggest(suggestState);
        }

        el.addEventListener('input', update);
        el.addEventListener('keydown', function (e) {
            if (!suggestState || suggestState.el !== el || ensureSuggestBox().hidden) return;
            var n = suggestState.items.length;

            if (e.key === 'ArrowDown') { e.preventDefault(); suggestState.active = (suggestState.active + 1) % n; paintSuggest(suggestState); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); suggestState.active = (suggestState.active - 1 + n) % n; paintSuggest(suggestState); }
            else if (e.key === 'Tab' || (e.key === 'Enter' && !e.ctrlKey && !e.metaKey)) { e.preventDefault(); pick(suggestState.active); }
            else if (e.key === 'Escape') { hideSuggest(); }
        });
        // A mousedown on the popup runs first and cancels itself out; this only fires for a genuine blur.
        el.addEventListener('blur', function () {
            setTimeout(function () { if (suggestState && suggestState.el === el) hideSuggest(); }, 150);
        });

        return { refresh: function () { if (document.activeElement === el) update(); } };
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
        toggleTheme: toggleTheme,
        attachAutocomplete: attachAutocomplete
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

        var curModule = document.documentElement.dataset.module;

        sel.addEventListener('change', function () {
            try { localStorage.setItem('tp-last-module', sel.value); } catch (e) { }

            var parts = location.pathname.split('/').filter(Boolean);
            if (parts[0] === curModule) parts.shift();

            location.href = '/' + [sel.value].concat(parts.slice(0, 2)).join('/');
        });

        // Landed on the module-less fallback route (bare "/", "/home/index"…) rather than an
        // explicit "/<module>/…" URL — controller names can never collide with module keys, so
        // parts[0] === curModule only when the URL named that module on purpose. Jump to whichever
        // module was last picked instead of always the server's static default.
        var parts = location.pathname.split('/').filter(Boolean);
        if (parts[0] !== curModule) {
            var last;
            try { last = localStorage.getItem('tp-last-module'); } catch (e) { last = null; }

            var known = last && Array.prototype.some.call(sel.options, function (o) { return o.value === last; });
            if (known && last !== curModule) {
                location.href = '/' + [last].concat(parts.slice(0, 2)).join('/');
            }
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        paintThemeIcon();
        var t = document.getElementById('themeToggle');
        if (t) t.addEventListener('click', toggleTheme);
        initNav();
        initModuleSwitch();
    });
})();
