/* Explorer — search and quick-fix data on any table.
   No third-party libraries: the grid renders itself with innerHTML, and events are delegated on a
   stable container, so re-rendering costs nothing. */
(function () {
    'use strict';

    var CFG = window.TP_CONFIG || { maxRows: 500, allowEdit: false };
    var LAST_TABLE = 'tp-last-table';

    var OPS = [
        { v: '=', t: '=' }, { v: '<>', t: '≠' },
        { v: 'like', t: 'contains' }, { v: 'startswith', t: 'starts with' },
        { v: '>', t: '>' }, { v: '>=', t: '≥' }, { v: '<', t: '<' }, { v: '<=', t: '≤' },
        { v: 'in', t: 'in list (a,b,c)' },
        { v: 'null', t: 'empty (NULL)' }, { v: 'notnull', t: 'has value' }
    ];

    var S = {
        tables: [],
        table: null,
        isView: false,
        columns: [],
        hidden: [],
        rows: [],
        dirty: new Map(),          // row index -> { key: {...}, changes: {column: value} }
        page: 1,
        pageSize: 50,
        sortColumn: null,
        sortDesc: false,
        total: -1,
        sql: '',
        query: { Columns: [], Rows: [], Sql: '' },
        log: [],                   // last loaded audit page, indexed by the row's data-i
        modalOk: null
    };

    function $(id) { return document.getElementById(id); }
    var esc = function (s) { return TP.escapeHtml(s); };

    // -------------------------------------------------------- value formatting

    function display(v) {
        if (v === null || v === undefined) return 'NULL';
        if (v === true) return 'true';
        if (v === false) return 'false';
        return String(v);
    }

    function shortType(c) {
        if (!c.DataType) return '';
        var len = c.MaxLength === -1 ? '(max)' : (c.IsText && c.MaxLength > 0 ? '(' + c.MaxLength + ')' : '');
        return c.DataType + len;
    }

    function colTitle(c) {
        var bits = [c.Name + ' · ' + shortType(c)];
        if (c.IsPrimaryKey) bits.push('primary key');
        if (c.IsReadOnly) bits.push('identity/computed — read-only');
        if (!c.IsNullable) bits.push('NOT NULL');
        bits.push('click to sort');
        return bits.join(' · ');
    }

    function canEdit(c) {
        return CFG.allowEdit && !S.isView && pks().length > 0
            && !c.IsPrimaryKey && !c.IsReadOnly && !c.IsSkipped;
    }

    function pks() { return S.columns.filter(function (c) { return c.IsPrimaryKey; }); }

    function colByName(name) {
        return S.columns.filter(function (c) { return c.Name === name; })[0];
    }

    function emptyHtml(title, text, icon) {
        return '<div class="empty"><svg width="44" height="44"><use href="#' + (icon || 'i-inbox') + '"/></svg>'
            + '<h3>' + esc(title) + '</h3>' + (text ? '<p>' + esc(text) + '</p>' : '') + '</div>';
    }

    function setLoading(container, on) {
        var ov = container.querySelector('.overlay');
        if (on) {
            if (!ov) {
                ov = document.createElement('div');
                ov.className = 'overlay';
                ov.innerHTML = '<div class="spinner" role="status" aria-label="Đang tải"></div>';
                container.appendChild(ov);
            }
        } else if (ov) {
            ov.remove();
        }
    }

    // ------------------------------------------------------------------- tab

    function initTabs() {
        $('tabs').addEventListener('click', function (e) {
            var tab = e.target.closest('.tab');
            if (!tab) return;

            document.querySelectorAll('#tabs .tab').forEach(function (t) {
                t.setAttribute('aria-selected', String(t === tab));
            });
            document.querySelectorAll('.pane').forEach(function (p) {
                p.dataset.active = String(p.dataset.pane === tab.dataset.pane);
            });

            if (tab.dataset.pane === 'log') loadLog();
            if (tab.dataset.pane === 'query') { setConsoleCollapsed(false); $('sqlText').focus(); }
        });
    }

    // ---------------------------------------------------------- query console

    function setConsoleCollapsed(collapsed) {
        $('queryConsole').hidden = collapsed;
        $('queryConsoleToggle').querySelector('.console-caret').style.transform = collapsed ? 'rotate(-90deg)' : '';
        try { localStorage.setItem('tp-query-console', collapsed ? '1' : '0'); } catch (e) { }
    }

    function initQueryConsole() {
        setConsoleCollapsed(localStorage.getItem('tp-query-console') === '1');
        $('queryConsoleToggle').addEventListener('click', function () {
            setConsoleCollapsed(!$('queryConsole').hidden);
        });
    }

    // ------------------------------------------------------------- table list

    async function loadTables(force) {
        try {
            if (force) await TP.get('/explorer/clearcache');
            S.tables = await TP.get('/explorer/tables');
            renderTableList();
            if (force) TP.toast('Database structure refreshed', 'ok');

            var last = localStorage.getItem(LAST_TABLE);
            if (!S.table && last && S.tables.some(function (t) { return t.Full === last; })) {
                selectTable(last);
            }
        } catch (e) {
            $('tableList').innerHTML = emptyHtml('Could not load table list', e.message, 'i-alert');
            $('tableCount').textContent = 'error';
        }
    }

    function renderTableList() {
        var kw = $('tableFilter').value.trim().toLowerCase();
        var list = kw
            ? S.tables.filter(function (t) { return t.Full.toLowerCase().indexOf(kw) >= 0; })
            : S.tables;

        $('tableCount').textContent = list.length + '/' + S.tables.length + ' bảng & view';
        $('tableList').innerHTML = list.length === 0
            ? emptyHtml('No tables match', '')
            : list.map(function (t) {
                return '<button type="button" class="table-item" data-full="' + esc(t.Full) + '"'
                    + (t.Full === S.table ? ' aria-current="true"' : '')
                    + ' title="' + esc(t.Full) + '"><span>' + esc(t.Name) + '</span>'
                    + (t.IsView ? '<span class="badge badge--view">view</span>' : '') + '</button>';
            }).join('');
    }

    async function selectTable(full) {
        if (!confirmDiscard()) return;

        S.table = full;
        S.page = 1;
        S.sortColumn = null;
        S.sortDesc = false;
        S.dirty.clear();
        S.rows = [];
        localStorage.setItem(LAST_TABLE, full);
        renderTableList();
        renderSaveBar();
        $('filters').innerHTML = '';
        $('gridWrap').innerHTML = '';
        setLoading($('gridWrap'), true);

        try {
            var meta = await TP.get('/explorer/columns?table=' + encodeURIComponent(full));
            S.columns = meta.Columns;
            S.isView = meta.IsView;
            await search();
        } catch (e) {
            TP.toast(e.message, 'err');
            $('gridWrap').innerHTML = emptyHtml('Could not load table structure', e.message, 'i-alert');
        } finally {
            setLoading($('gridWrap'), false);
        }
    }

    // ----------------------------------------------------------------- filters

    function addFilterRow() {
        if (!S.columns.length) { TP.toast('Select a table first', 'err'); return; }

        var row = document.createElement('div');
        row.className = 'filter-row';
        row.innerHTML =
            '<select class="select col-select" aria-label="Column">'
            + S.columns.map(function (c) { return '<option value="' + esc(c.Name) + '">' + esc(c.Name) + '</option>'; }).join('')
            + '</select>'
            + '<select class="select op-select" aria-label="Comparison">'
            + OPS.map(function (o) { return '<option value="' + o.v + '">' + o.t + '</option>'; }).join('')
            + '</select>'
            + '<input class="input val-input" placeholder="value" spellcheck="false" />'
            + '<button type="button" class="icon-btn" title="Remove condition" aria-label="Remove condition">'
            + '<svg width="15" height="15"><use href="#i-x"/></svg></button>';

        var op = row.querySelector('.op-select');
        var val = row.querySelector('.val-input');
        op.addEventListener('change', function () {
            val.style.visibility = (op.value === 'null' || op.value === 'notnull') ? 'hidden' : 'visible';
        });
        val.addEventListener('keydown', function (e) { if (e.key === 'Enter') search(true); });
        row.querySelector('.icon-btn').addEventListener('click', function () { row.remove(); search(true); });

        $('filters').appendChild(row);
        row.querySelector('.col-select').focus();
    }

    function readFilters() {
        return Array.prototype.map.call($('filters').querySelectorAll('.filter-row'), function (r) {
            return {
                Column: r.querySelector('.col-select').value,
                Op: r.querySelector('.op-select').value,
                Value: r.querySelector('.val-input').value
            };
        });
    }

    // ------------------------------------------------------------------ search

    async function search(resetPage) {
        if (!S.table) return;
        if (!confirmDiscard()) return;
        if (resetPage) S.page = 1;

        S.dirty.clear();
        renderSaveBar();
        setLoading($('gridWrap'), true);

        try {
            var res = await TP.post('/explorer/search', {
                Table: S.table,
                Keyword: $('kw').value,
                BoDau: $('boDau').checked,
                IncludeLongText: $('longText').checked,
                CountTotal: $('countTotal').checked,
                Filters: readFilters(),
                SortColumn: S.sortColumn,
                SortDesc: S.sortDesc,
                Page: S.page,
                PageSize: S.pageSize
            });

            S.columns = res.Columns;
            S.rows = res.Rows;
            S.total = res.Total;
            S.sql = res.Sql;
            S.hidden = res.HiddenColumns || [];

            renderGrid();
            renderFoot(res);
        } catch (e) {
            TP.toast(e.message, 'err');
            $('gridWrap').innerHTML = emptyHtml('Query error', e.message, 'i-alert');
        } finally {
            setLoading($('gridWrap'), false);
        }
    }

    function renderFoot(res) {
        $('statRows').textContent = TP.num(S.rows.length);
        $('statTotal').textContent = S.total < 0 ? '–' : TP.num(S.total);
        $('statTime').textContent = res.ElapsedMs + ' ms';

        var pages = S.total < 0 ? '?' : Math.max(1, Math.ceil(S.total / S.pageSize));
        $('pageOf').textContent = '/ ' + pages;
        $('pageNo').value = S.page;
        $('btnPrev').disabled = S.page <= 1;
        $('btnNext').disabled = S.rows.length < S.pageSize;

        var notes = [];
        if (S.isView) notes.push('This is a view — read-only.');
        if (!S.isView && pks().length === 0) notes.push('Table has no primary key — editing is disabled.');
        if (S.hidden.length) notes.push('Hidden ' + S.hidden.length + ' long/binary column(s): ' + S.hidden.slice(0, 6).join(', ')
            + (S.hidden.length > 6 ? '…' : '') + ' (enable "Long cols" to show).');

        var note = $('gridNote');
        note.hidden = notes.length === 0;
        note.innerHTML = notes.length
            ? '<svg width="14" height="14" style="flex:0 0 14px"><use href="#i-alert"/></svg><span>' + esc(notes.join(' ')) + '</span>'
            : '';
    }

    function renderGrid() {
        var wrap = $('gridWrap');

        if (!S.table) {
            wrap.innerHTML = emptyHtml('Select a table on the left', 'Then enter a keyword or add a filter to query data.', 'i-table');
            return;
        }
        if (!S.rows.length) {
            wrap.innerHTML = emptyHtml('No rows match', 'Try clearing the keyword or relaxing the filter conditions.');
            return;
        }

        wrap.innerHTML = gridHtml(S.columns, S.rows, {
            startIndex: (S.page - 1) * S.pageSize,
            sortable: true,
            editable: true
        });
    }

    /** Shared renderer for both the search grid and the query-result grid. */
    function gridHtml(columns, rows, o) {
        var h = ['<table class="grid"><thead><tr><th class="rowno">#</th>'];

        columns.forEach(function (c) {
            var sorted = o.sortable && S.sortColumn === c.Name;
            h.push('<th' + (o.sortable ? ' data-col="' + esc(c.Name) + '"' : ' style="cursor:default"')
                + (sorted ? ' data-sort="1"' : '') + ' title="' + esc(o.sortable ? colTitle(c) : c.Name) + '"><div class="th">'
                + (c.IsPrimaryKey ? '<svg width="11" height="11" style="color:var(--accent);flex:0 0 11px"><use href="#i-key"/></svg>' : '')
                + '<span>' + esc(c.Name) + '</span>'
                + (c.DataType ? '<span class="type">' + esc(shortType(c)) + '</span>' : '')
                + (o.sortable ? '<svg class="caret" width="11" height="11" style="transform:rotate(' + (sorted && !S.sortDesc ? 180 : 0) + 'deg)"><use href="#i-caret"/></svg>' : '')
                + '</div></th>');
        });
        h.push('</tr></thead><tbody>');

        rows.forEach(function (row, i) {
            h.push('<tr><td class="rowno">' + (o.startIndex + i + 1) + '</td>');
            var d = o.editable ? S.dirty.get(i) : null;

            columns.forEach(function (c) {
                var isDirty = d && Object.prototype.hasOwnProperty.call(d.changes, c.Name);
                var v = isDirty ? d.changes[c.Name] : row[c.Name];
                var text = display(v);
                var cls = [];

                if (v === null || v === undefined) cls.push('null');
                if (isDirty) cls.push('dirty');
                if (o.editable) cls.push(canEdit(c) ? 'editable' : (c.IsPrimaryKey ? '' : 'ro'));

                h.push('<td data-r="' + i + '" data-c="' + esc(c.Name) + '"'
                    + (cls.length ? ' class="' + cls.join(' ') + '"' : '')
                    + ' title="' + esc(text.length > 400 ? text.slice(0, 400) + '…' : text) + '">'
                    + esc(text) + '</td>');
            });
            h.push('</tr>');
        });

        return h.join('') + '</tbody></table>';
    }

    // -------------------------------------------------------------- cell edit

    function startEdit(td) {
        if (!td.classList.contains('editable') || td.classList.contains('editing')) return;

        var r = +td.dataset.r;
        var col = colByName(td.dataset.c);
        if (!col) return;

        var d = S.dirty.get(r);
        var cur = d && Object.prototype.hasOwnProperty.call(d.changes, col.Name) ? d.changes[col.Name] : S.rows[r][col.Name];

        td.classList.add('editing');
        td.textContent = '';

        var input = document.createElement('input');
        input.value = (cur === null || cur === undefined) ? '' : String(cur);
        input.placeholder = col.IsNullable ? 'NULL' : '';
        input.setAttribute('aria-label', col.Name);
        td.appendChild(input);
        input.focus();
        input.select();

        var done = false;
        function finish(commit, move) {
            if (done) return;
            done = true;
            var text = input.value;
            td.classList.remove('editing');
            if (commit) applyEdit(r, col, text);
            paintCell(td, r, col);
            if (move) moveEdit(td, move);
        }

        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); finish(true, 'down'); }
            else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
            else if (e.key === 'Tab') { e.preventDefault(); finish(true, e.shiftKey ? 'left' : 'right'); }
        });
        input.addEventListener('blur', function () { finish(true); });
    }

    /** {null} = set NULL, {empty} = empty string, blank = NULL when the column allows it. */
    function parseInput(col, text) {
        if (text === '{null}') return null;
        if (text === '{empty}') return '';
        if (text === '') return col.IsNullable ? null : '';

        if (col.DataType === 'bit') {
            var t = text.trim().toLowerCase();
            if (t === '1' || t === 'true') return 'true';
            if (t === '0' || t === 'false') return 'false';
        }
        return text;
    }

    function same(a, b) {
        var an = (a === null || a === undefined), bn = (b === null || b === undefined);
        if (an || bn) return an && bn;
        return String(a) === String(b);
    }

    function applyEdit(r, col, text) {
        var parsed = parseInput(col, text);
        var d = S.dirty.get(r);

        if (same(parsed, S.rows[r][col.Name])) {
            if (d) {
                delete d.changes[col.Name];
                if (!Object.keys(d.changes).length) S.dirty.delete(r);
            }
        } else {
            if (!d) { d = { key: rowKey(r), changes: {} }; S.dirty.set(r, d); }
            d.changes[col.Name] = parsed;
        }
        renderSaveBar();
    }

    function rowKey(r) {
        var key = {};
        pks().forEach(function (c) {
            var v = S.rows[r][c.Name];
            key[c.Name] = (v === null || v === undefined) ? null : String(v);
        });
        return key;
    }

    function paintCell(td, r, col) {
        var d = S.dirty.get(r);
        var isDirty = d && Object.prototype.hasOwnProperty.call(d.changes, col.Name);
        var v = isDirty ? d.changes[col.Name] : S.rows[r][col.Name];
        var text = display(v);

        td.className = 'editable' + (isDirty ? ' dirty' : '') + ((v === null || v === undefined) ? ' null' : '');
        td.textContent = text;
        td.title = text.length > 400 ? text.slice(0, 400) + '…' : text;
    }

    function moveEdit(td, dir) {
        var target = null;
        if (dir === 'down') {
            var tr = td.parentElement.nextElementSibling;
            if (tr) target = tr.querySelector('td[data-c="' + CSS.escape(td.dataset.c) + '"]');
        } else {
            target = dir === 'right' ? td.nextElementSibling : td.previousElementSibling;
            while (target && !target.classList.contains('editable')) {
                target = dir === 'right' ? target.nextElementSibling : target.previousElementSibling;
            }
        }
        if (target) startEdit(target);
    }

    // -------------------------------------------------------------------- save

    function renderSaveBar() {
        var n = S.dirty.size;
        var cells = 0;
        S.dirty.forEach(function (d) { cells += Object.keys(d.changes).length; });

        $('saveBar').hidden = n === 0;
        $('dirtyText').textContent = n === 0 ? '' : 'Editing ' + cells + ' cell(s) across ' + n + ' row(s) — unsaved';
    }

    function openSaveModal() {
        if (!S.dirty.size) return;

        var body = [];
        S.dirty.forEach(function (d, r) {
            body.push('<div style="margin-bottom:var(--s4)">'
                + '<div style="display:flex;gap:6px;align-items:center;flex-wrap:wrap;margin-bottom:6px;font-size:12px;color:var(--fg-muted)">'
                + 'Row ' + ((S.page - 1) * S.pageSize + r + 1) + ' · '
                + Object.keys(d.key).map(function (k) {
                    return '<span class="keychip">' + esc(k) + ' = ' + esc(display(d.key[k])) + '</span>';
                }).join(' ')
                + '</div>'
                + '<table class="diff"><thead><tr><th>Column</th><th>Before</th><th>After</th></tr></thead><tbody>'
                + Object.keys(d.changes).map(function (c) {
                    return '<tr><td class="col">' + esc(c) + '</td>'
                        + '<td class="from">' + esc(display(S.rows[r][c])) + '</td>'
                        + '<td class="to">' + esc(display(d.changes[c])) + '</td></tr>';
                }).join('')
                + '</tbody></table></div>');
        });

        showModal('Confirm update ' + S.dirty.size + ' row(s)', body.join(''),
            'Each row is one UPDATE by primary key, only runs when exactly 1 row is affected. Audited.', doSave);
    }

    async function doSave() {
        closeModal();

        var entries = [];
        S.dirty.forEach(function (d, r) { entries.push([r, d]); });

        var ok = 0;
        var errors = [];
        for (var i = 0; i < entries.length; i++) {
            var r = entries[i][0], d = entries[i][1];
            try {
                await TP.post('/explorer/update', { Table: S.table, Key: d.key, Changes: d.changes });
                Object.keys(d.changes).forEach(function (c) { S.rows[r][c] = d.changes[c]; });
                S.dirty.delete(r);
                ok++;
            } catch (e) {
                errors.push('Row ' + ((S.page - 1) * S.pageSize + r + 1) + ': ' + e.message);
            }
        }

        renderGrid();
        renderSaveBar();
        if (ok) TP.toast('Updated ' + ok + ' row(s)', 'ok');
        errors.forEach(function (m) { TP.toast(m, 'err'); });
    }

    function confirmDiscard() {
        if (!S.dirty.size) return true;
        return window.confirm('There are unsaved changes that will be lost. Continue?');
    }

    // -------------------------------------------------------- console SELECT

    async function runQuery() {
        var sql = $('sqlText').value.trim();
        if (!sql) { TP.toast('No statement entered', 'err'); return; }

        var wrap = $('queryGrid');
        setLoading(wrap, true);
        try {
            var res = await TP.post('/explorer/query', { Sql: sql });
            S.query = res;

            wrap.innerHTML = res.Rows.length
                ? gridHtml(res.Columns, res.Rows, { startIndex: 0, sortable: false, editable: false })
                : emptyHtml('Query ran but returned no rows', '');

            $('qRows').textContent = TP.num(res.Rows.length);
            $('qTime').textContent = res.ElapsedMs + ' ms'
                + (res.Truncated ? ' · truncated at ' + CFG.maxRows + ' rows' : '');
        } catch (e) {
            TP.toast(e.message, 'err');
            wrap.innerHTML = emptyHtml('Query failed', e.message, 'i-alert');
            $('qRows').textContent = '0';
            $('qTime').textContent = '';
        } finally {
            setLoading(wrap, false);
        }
    }

    // ----------------------------------------------------------- SQL suggestions

    var SQL_KEYWORDS = [
        'SELECT', 'DISTINCT', 'TOP', 'FROM', 'WHERE', 'AND', 'OR', 'NOT', 'IN', 'LIKE', 'IS', 'NULL',
        'ORDER BY', 'GROUP BY', 'HAVING', 'JOIN', 'INNER JOIN', 'LEFT JOIN', 'RIGHT JOIN', 'ON', 'AS',
        'WITH', 'UNION', 'UNION ALL', 'BETWEEN', 'DESC', 'ASC', 'COUNT', 'SUM', 'AVG', 'MIN', 'MAX',
        'CAST', 'CONVERT', 'CASE', 'WHEN', 'THEN', 'ELSE', 'END', 'EXISTS'
    ];

    var queryColCache = {};     // table Full -> Columns[], loaded lazily once a table is mentioned
    var queryColLoading = {};   // table Full -> true while that fetch is in flight
    var sqlAc = null;

    /** Table names following FROM/JOIN so far, used to scope which columns get suggested. */
    function mentionedTables(sql) {
        var names = [], seen = {}, re = /\b(?:FROM|JOIN)\s+([A-Za-z0-9_.[\]]+)/gi, m;
        while ((m = re.exec(sql))) {
            var name = m[1].replace(/[[\]]/g, '').replace(/^dbo\./i, '');
            if (!seen[name]) { seen[name] = true; names.push(name); }
        }
        return names;
    }

    function sqlSuggestItems(value, caret) {
        var m = /[A-Za-z0-9_]+$/.exec(value.slice(0, caret));
        if (!m) return null;

        var kw = m[0].toLowerCase();
        var items = [];

        SQL_KEYWORDS.forEach(function (k) {
            if (k.toLowerCase().indexOf(kw) === 0) items.push({ label: k, hint: 'từ khoá' });
        });

        S.tables.forEach(function (t) {
            if (t.Name.toLowerCase().indexOf(kw) === 0) items.push({ label: t.Name, hint: t.IsView ? 'view' : 'bảng' });
        });

        mentionedTables(value).forEach(function (name) {
            var t = S.tables.filter(function (x) { return x.Name.toLowerCase() === name.toLowerCase(); })[0];
            if (!t) return;

            var cols = queryColCache[t.Full];
            if (!cols) {
                if (!queryColLoading[t.Full]) {
                    queryColLoading[t.Full] = true;
                    TP.get('/explorer/columns?table=' + encodeURIComponent(t.Full)).then(function (res) {
                        queryColCache[t.Full] = res.Columns || [];
                        if (sqlAc) sqlAc.refresh();
                    }, function () { queryColLoading[t.Full] = false; });
                }
                return;
            }
            cols.forEach(function (c) {
                if (c.Name.toLowerCase().indexOf(kw) === 0) items.push({ label: c.Name, hint: t.Name });
            });
        });

        if (!items.length) return null;
        return { items: items, range: [caret - m[0].length, caret] };
    }

    // ----------------------------------------------------------------- history

    async function loadLog() {
        var box = $('logList');
        setLoading(box, true);
        try {
            var items = await TP.get('/explorer/log?take=300');
            S.log = items;
            box.innerHTML = items.length ? items.map(logItemHtml).join('')
                : emptyHtml('No audit log yet', 'Entries are written when data is edited, someone signs in, or a SELECT query is run.');
        } catch (e) {
            box.innerHTML = emptyHtml('Could not load audit log', e.message, 'i-alert');
        } finally {
            setLoading(box, false);
        }
    }

    /** `nested` = already being shown inside its own run, so it does not offer to open it again. */
    function logItemHtml(x, i, nested) {
        var what;
        if (x.Type === 'update') {
            var changed = [];
            try { changed = JSON.parse(x.ChangesJson || '[]').map(function (c) { return c.Column; }); } catch (e) { }

            // The label is the whole point of the line: the key is a GUID in most of these tables,
            // so without it every entry reads the same.
            what = (x.TableName || '')
                + (x.Label ? ' [' + x.Label + ']' : '')
                + (changed.length ? ' → ' + changed.join(', ') : '');
        } else if (x.Type === 'query') {
            what = (x.Sql || '').replace(/\s+/g, ' ').slice(0, 200);
        } else {
            what = x.Note || x.TableName || '';
        }

        return '<div class="log-item" data-i="' + i + '"><div class="log-head">'
            + '<span class="badge' + (x.Type === 'update' ? ' badge--pk' : '') + '">' + esc(x.Type) + '</span>'
            + '<time>' + esc(logTime(x.Time)) + '</time>'
            + '<span class="who">' + esc(x.User || '?') + '</span>'
            + '<span class="what">' + esc(what) + '</span>'
            + '<svg width="13" height="13"><use href="#i-caret"/></svg></div>'
            + '<div class="log-body" hidden>' + logActions(x, nested)
            + '<div class="log-run" hidden></div>'
            + '<pre>' + esc(logDetail(x)) + '</pre></div></div>';
    }

    function logTime(iso) {
        if (!iso) return '';
        var d = new Date(iso);
        if (isNaN(d)) return String(iso);
        var p = function (n) { return String(n).padStart(2, '0'); };
        return p(d.getDate()) + '/' + p(d.getMonth() + 1) + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    }

    /** Unwraps nested JSON so it is readable instead of an escaped string. */
    function logDetail(x) {
        var o = {};
        Object.keys(x).forEach(function (k) {
            var v = x[k];
            if (v === null || v === '') return;
            if (k === 'KeyJson' || k === 'ChangesJson') {
                try { o[k.replace('Json', '')] = JSON.parse(v); return; } catch (e) { }
            }
            o[k] = v;
        });
        return JSON.stringify(o, null, 2);
    }

    // ------------------------------------------- history: statements you can actually run

    /* A logged statement is parameterised — `SET [c] = @v WHERE [k] = @k0` — because values never
       reach the SQL text; that is the injection guard. Pasting one into SSMS therefore does nothing
       but complain about undeclared variables. The values are recorded separately, in Key and
       Changes, so a runnable statement is rebuilt here out of those three pieces. */

    function logActions(x, nested) {
        var out = [];

        if (x.Type === 'update' && x.KeyJson && x.ChangesJson) {
            out.push(copyBtn('sel', 'Copy SELECT', 'Câu SELECT xem lại dòng này trong SSMS'));
            out.push(copyBtn('upd', 'Copy UPDATE', 'Đúng thay đổi đã ghi, tham số thay bằng giá trị thật'));
            out.push(copyBtn('undo', 'Copy hoàn tác', 'Câu UPDATE trả dòng này về giá trị trước đó'));
        } else if (x.Type === 'query' && x.Sql) {
            out.push(copyBtn('sql', 'Copy SQL', 'Câu này chạy được nguyên văn'));
        }

        // A summary row says how many rows a sync wrote but not which, and they are scattered
        // through the list — so it has to be able to show them itself.
        if (x.RunId && !nested) {
            out.push(btnHtml('data-runview', 'i-table', 'Xem các dòng đã sửa',
                'Mở từng dòng mà lần chạy này đã ghi'));
            out.push(copyBtn('run', 'Copy hoàn tác cả lần chạy',
                'Script trả TOÀN BỘ số dòng lần chạy này đã sửa về giá trị cũ'));
        }

        return out.length ? '<div class="log-actions">' + out.join('') + '</div>' : '';
    }

    function copyBtn(kind, text, title) {
        return btnHtml('data-copy="' + kind + '"', 'i-file', text, title);
    }

    function btnHtml(attr, icon, text, title) {
        return '<button type="button" class="btn btn--ghost" ' + attr + ' title="' + esc(title)
            + '"><svg width="13" height="13"><use href="#' + icon + '"/></svg> ' + esc(text) + '</button>';
    }

    /**
     * Renders the rows one run wrote, inside the entry that summarises it.
     *
     * They are appended to the same list the copy buttons index into, so each nested row gets the
     * ordinary per-row buttons with no special case — only the run-level ones are suppressed, or
     * every row would offer to open the run it is already inside.
     */
    async function showRunRows(index, button) {
        var x = S.log[index];
        var box = button.closest('.log-body').querySelector('.log-run');
        if (!x || !box) return;

        if (box.dataset.loaded === '1') {
            box.hidden = !box.hidden;
            return;
        }

        button.disabled = true;
        try {
            var rows = await TP.get('/explorer/logrun?id=' + encodeURIComponent(x.RunId));
            var writes = rows.filter(function (r) { return r.Type === 'update'; });

            box.innerHTML = writes.length
                ? writes.map(function (r) {
                    S.log.push(r);
                    return logItemHtml(r, S.log.length - 1, true);
                }).join('')
                : '<div class="res-msg">Lần chạy này không ghi dòng nào.</div>';

            box.dataset.loaded = '1';
            box.hidden = false;
        } catch (e) {
            TP.toast(e.message, 'err');
        } finally {
            button.disabled = false;
        }
    }

    async function copyLogScript(kind, index) {
        var x = S.log[index];
        if (!x) return;

        try {
            var script = kind === 'run' ? await runUndoScript(x) : entryScript(kind, x);
            if (!script) {
                TP.toast('Bản ghi này không đủ dữ liệu để dựng câu lệnh', 'err');
                return;
            }

            if (await TP.copy(script)) TP.toast('Đã copy — dán thẳng vào SSMS', 'ok');
            else showModal('Trình duyệt không cho copy — chọn và copy tay',
                '<pre class="sql-box">' + esc(script) + '</pre>', '', null);
        } catch (e) {
            TP.toast(e.message, 'err');
        }
    }

    function entryScript(kind, x) {
        if (kind === 'sql') return x.Sql || null;

        if (kind === 'sel') {
            var select = selectStatement(x);
            return select ? scriptHead('xem lại thao tác #' + x.Id, x) + '\n\n' + select + '\n' : null;
        }

        var back = kind === 'undo';
        var update = updateStatement(x, back);
        if (!update) return null;

        return guarded(scriptHead((back ? 'hoàn tác' : 'chạy lại') + ' thao tác #' + x.Id, x), [update]);
    }

    /** Undo a whole run: every row it wrote, reversed, inside one transaction. */
    async function runUndoScript(x) {
        var rows = await TP.get('/explorer/logrun?id=' + encodeURIComponent(x.RunId));

        var statements = rows
            .filter(function (r) { return r.Type === 'update' && r.KeyJson && r.ChangesJson; })
            .map(function (r) { return updateStatement(r, true); })
            .filter(Boolean);

        if (!statements.length) throw new Error('Lần chạy này không ghi dòng nào');

        return guarded(scriptHead('hoàn tác cả lần chạy · ' + statements.length + ' dòng', x)
            + '\n   run ' + x.RunId, statements);
    }

    function selectStatement(x) {
        var key = parseLogJson(x.KeyJson);
        if (!key || !x.TableName) return null;

        return 'SELECT *\n  FROM ' + qualify(x.TableName) + '\n WHERE ' + whereKey(key) + ';';
    }

    /**
     * One UPDATE, in either direction — `back` swaps From and To, turning "what ToolPlus did" into
     * "undo what ToolPlus did".
     *
     * The WHERE repeats the value the log says the column held afterwards, so a row somebody has
     * changed since simply will not match: the same optimistic rule the sync page writes under.
     */
    function updateStatement(x, back) {
        var key = parseLogJson(x.KeyJson);
        var changes = parseLogJson(x.ChangesJson);
        if (!key || !changes || !changes.length || !x.TableName) return null;

        var sets = changes.map(function (c) {
            return quote(c.Column) + ' = ' + literal(back ? c.From : c.To);
        });
        var guards = changes.map(function (c) {
            return same(c.Column, back ? c.To : c.From);
        });

        return 'UPDATE ' + qualify(x.TableName)
            + '\n   SET ' + sets.join(',\n       ')
            + '\n WHERE ' + whereKey(key)
            + '\n   AND ' + guards.join('\n   AND ') + ';';
    }

    function whereKey(key) {
        return Object.keys(key).map(function (k) { return same(k, key[k]); }).join('\n   AND ');
    }

    /** NULL never compares equal to anything, so it needs IS rather than =. */
    function same(column, value) {
        return value === null || value === undefined
            ? quote(column) + ' IS NULL'
            : quote(column) + ' = ' + literal(value);
    }

    /**
     * Value -> SQL literal. Column types are not recorded, so everything goes out as a string and
     * SQL Server converts: N'123' into an int column is fine. The exception is bit, which is logged
     * as "True"/"False" and which N'True' will NOT convert to — hence the explicit mapping.
     */
    function literal(v) {
        if (v === null || v === undefined) return 'NULL';
        if (v === 'True') return '1';
        if (v === 'False') return '0';
        return "N'" + String(v).replace(/'/g, "''") + "'";
    }

    function quote(name) { return '[' + String(name == null ? '' : name).replace(/]/g, ']]') + ']'; }

    /** "dbo.sys_User" -> "[dbo].[sys_User]". */
    function qualify(table) { return String(table || '').split('.').map(quote).join('.'); }

    function parseLogJson(raw) {
        try { return JSON.parse(raw || ''); } catch (e) { return null; }
    }

    function scriptHead(title, x) {
        return '/* ToolPlus · ' + title
            + '\n   ' + (x.User || '?') + ' · ' + logStamp(x.Time)
            + ' · module ' + (x.Module || '?') + ' */';
    }

    function logStamp(iso) {
        var d = new Date(iso);
        if (isNaN(d)) return String(iso || '');
        var p = function (n) { return String(n).padStart(2, '0'); };
        return p(d.getDate()) + '/' + p(d.getMonth() + 1) + '/' + d.getFullYear()
            + ' ' + p(d.getHours()) + ':' + p(d.getMinutes()) + ':' + p(d.getSeconds());
    }

    /** Anything that writes goes out ready to be run, counted, and only then kept. */
    function guarded(head, statements) {
        return head + '\n\nBEGIN TRAN;\n\n' + statements.join('\n\n')
            + '\n\n-- Xem số dòng bị ảnh hưởng. Đúng thì đổi ROLLBACK thành COMMIT rồi chạy lại.'
            + '\nROLLBACK;\n';
    }

    // ------------------------------------------------------------------ modal

    function showModal(title, bodyHtml, hint, onOk) {
        $('modalTitle').textContent = title;
        $('modalBody').innerHTML = bodyHtml;
        $('modalHint').textContent = hint || '';
        $('modalOk').hidden = !onOk;
        S.modalOk = onOk || null;
        $('modal').dataset.open = 'true';
        (onOk ? $('modalOk') : $('modal').querySelector('[data-close]')).focus();
    }

    function closeModal() {
        $('modal').dataset.open = 'false';
        S.modalOk = null;
    }

    // ------------------------------------------------------------------- init

    function initEvents() {
        // sidebar
        $('tableFilter').addEventListener('input', renderTableList);
        $('btnReloadMeta').addEventListener('click', function () { loadTables(true); });
        $('tableList').addEventListener('click', function (e) {
            var item = e.target.closest('.table-item');
            if (!item) return;
            selectTable(item.dataset.full);
            if (window.innerWidth <= 1280) $('sidebar').dataset.open = 'false';
        });
        $('tableToggle').addEventListener('click', function () {
            var sb = $('sidebar');
            sb.dataset.open = sb.dataset.open === 'true' ? 'false' : 'true';
        });

        // toolbar
        $('btnSearch').addEventListener('click', function () { search(true); });
        $('kw').addEventListener('keydown', function (e) { if (e.key === 'Enter') search(true); });
        ['boDau', 'longText', 'countTotal'].forEach(function (id) {
            $(id).addEventListener('change', function () { search(true); });
        });
        $('btnAddFilter').addEventListener('click', addFilterRow);
        $('btnReload').addEventListener('click', function () { search(false); });
        $('btnCsv').addEventListener('click', function () {
            if (!S.rows.length) { TP.toast('No data to export', 'err'); return; }
            TP.downloadCsv(S.table + '.csv', S.columns.map(function (c) { return c.Name; }), S.rows);
        });
        $('btnSql').addEventListener('click', function () {
            if (!S.sql) { TP.toast('No query has been run', 'err'); return; }
            showModal('Last SQL query run',
                '<pre style="margin:0;font:400 12.5px/1.6 var(--mono);white-space:pre-wrap">' + esc(S.sql) + '</pre>',
                'Values were passed as parameters so they do not appear in the statement.', null);
        });

        // paging
        $('pageSize').addEventListener('change', function () {
            S.pageSize = +this.value;
            search(true);
        });
        $('btnPrev').addEventListener('click', function () { if (S.page > 1) { S.page--; search(false); } });
        $('btnNext').addEventListener('click', function () { S.page++; search(false); });
        $('pageNo').addEventListener('keydown', function (e) {
            if (e.key !== 'Enter') return;
            var n = parseInt(this.value, 10);
            if (n > 0) { S.page = n; search(false); }
        });

        // grid: sorting + cell editing
        $('gridWrap').addEventListener('click', function (e) {
            var th = e.target.closest('th[data-col]');
            if (!th) return;
            if (S.sortColumn === th.dataset.col) S.sortDesc = !S.sortDesc;
            else { S.sortColumn = th.dataset.col; S.sortDesc = false; }
            search(true);
        });
        $('gridWrap').addEventListener('dblclick', function (e) {
            var td = e.target.closest('td');
            if (td) startEdit(td);
        });

        // save
        $('btnSave').addEventListener('click', openSaveModal);
        $('btnRevert').addEventListener('click', function () {
            S.dirty.clear();
            renderGrid();
            renderSaveBar();
        });

        // console SELECT
        initQueryConsole();
        $('btnRun').addEventListener('click', runQuery);
        $('sqlText').addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); runQuery(); }
        });
        sqlAc = TP.attachAutocomplete($('sqlText'), { getItems: sqlSuggestItems });
        $('btnQueryCsv').addEventListener('click', function () {
            if (!S.query.Rows || !S.query.Rows.length) { TP.toast('No results to export', 'err'); return; }
            TP.downloadCsv('query.csv', S.query.Columns.map(function (c) { return c.Name; }), S.query.Rows);
        });

        // history
        $('btnReloadLog').addEventListener('click', loadLog);
        $('logList').addEventListener('click', function (e) {
            var copy = e.target.closest('[data-copy]');
            if (copy) {
                copyLogScript(copy.dataset.copy, +copy.closest('.log-item').dataset.i);
                return;
            }

            var runView = e.target.closest('[data-runview]');
            if (runView) {
                showRunRows(+runView.closest('.log-item').dataset.i, runView);
                return;
            }

            var head = e.target.closest('.log-head');
            if (!head) return;
            var body = head.nextElementSibling;
            body.hidden = !body.hidden;
            head.querySelector('svg').style.transform = body.hidden ? '' : 'rotate(180deg)';
        });

        // modal
        $('modal').addEventListener('click', function (e) {
            if (e.target === this || e.target.closest('[data-close]')) closeModal();
        });
        $('modalOk').addEventListener('click', function () { if (S.modalOk) S.modalOk(); });

        // keyboard shortcuts & unsaved-changes guard
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && $('modal').dataset.open === 'true') { closeModal(); return; }

            var tag = (e.target.tagName || '').toLowerCase();
            if (tag === 'input' || tag === 'textarea' || tag === 'select') return;
            if (e.key === '/') { e.preventDefault(); $('tableFilter').focus(); }
            if (e.key === 'f' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); $('kw').focus(); }
        });
        window.addEventListener('beforeunload', function (e) {
            if (S.dirty.size) { e.preventDefault(); e.returnValue = ''; }
        });
    }

    document.addEventListener('DOMContentLoaded', function () {
        initTabs();
        initEvents();
        renderGrid();
        loadTables(false);
    });
})();
