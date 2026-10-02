/* Field sync — repair one column from another database.
   Two halves: pick what to compare, then read the comparison and tick what to write.
   No third-party libraries; the grid renders itself with innerHTML and delegates its events. */
(function () {
    'use strict';

    var CFG = window.TP_SYNC || { maxRows: 5000, canEdit: false, currentModule: '', sources: [] };

    /* Keyed by the name the server sends (SyncRowState, serialised by name).
       "pick" = ticked by default: only the plain "target is empty" case, never an overwrite. */
    var STATES = {
        Fill: { text: 'Sẽ điền', cls: 'ok', pick: true, writable: true },
        Overwrite: { text: 'Sẽ ghi đè', cls: 'warn', pick: false, writable: true },
        Blocked: { text: 'Không ghi được', cls: 'danger' },
        Ambiguous: { text: 'Nguồn trùng khóa', cls: 'warn' },
        NotFound: { text: 'Không thấy ở nguồn', cls: 'mut' },
        SourceEmpty: { text: 'Nguồn cũng trống', cls: 'mut' },
        Same: { text: 'Giống nhau', cls: 'mut' },
        Updated: { text: 'Đã ghi', cls: 'ok' },
        Stale: { text: 'Đã đổi, bỏ qua', cls: 'warn' }
    };

    /* Grid order: what needs attention first, what was left alone last. */
    var ORDER = ['Fill', 'Overwrite', 'Blocked', 'Ambiguous', 'NotFound', 'SourceEmpty', 'Same',
        'Updated', 'Stale'];

    var OPS = [
        { v: '=', t: '=' }, { v: '<>', t: '≠' },
        { v: 'like', t: 'chứa' }, { v: 'startswith', t: 'bắt đầu bằng' },
        { v: '>', t: '>' }, { v: '>=', t: '≥' }, { v: '<', t: '<' }, { v: '<=', t: '≤' },
        { v: 'in', t: 'trong danh sách (a,b,c)' },
        { v: 'null', t: 'đang trống (NULL)' }, { v: 'notnull', t: 'có giá trị' }
    ];

    var S = {
        dstTables: [], dstColumns: [],
        srcTables: [], srcColumns: [],
        filters: [],
        plan: null,
        rows: [],
        picked: new Set(),     // index into S.rows
        hiddenStates: new Set(),
        frozen: false,         // showing the outcome of a write, not a preview
        modalOk: null
    };

    /** The searchable pickers, by field id. Built once in initPickers(). */
    var P = {};

    function $(id) { return document.getElementById(id); }
    var esc = function (s) { return TP.escapeHtml(s); };

    // ------------------------------------------------------------------ helpers

    function display(v) {
        if (v === null || v === undefined) return 'NULL';
        if (v === '') return '(rỗng)';
        return String(v);
    }

    function cellClass(v) { return (v === null || v === undefined || v === '') ? ' class="null"' : ''; }

    function typeHint(c) {
        var len = c.MaxLength === -1 ? '(max)' : (c.IsText && c.MaxLength > 0 ? '(' + c.MaxLength + ')' : '');
        return c.DataType + len + (c.IsPrimaryKey ? ' · PK' : '') + (c.IsReadOnly ? ' · chỉ đọc' : '');
    }

    function fill(sel, items, value, placeholder) {
        var html = placeholder ? '<option value="">' + esc(placeholder) + '</option>' : '';
        html += items.map(function (it) {
            return '<option value="' + esc(it.v) + '"' + (it.v === value ? ' selected' : '') + '>'
                + esc(it.t) + '</option>';
        }).join('');
        sel.innerHTML = html;
        return sel.value;
    }

    function emptyHtml(title, text, icon) {
        return '<div class="empty"><svg width="44" height="44"><use href="#' + (icon || 'i-inbox') + '"/></svg>'
            + '<h3>' + esc(title) + '</h3>' + (text ? '<p>' + esc(text) + '</p>' : '') + '</div>';
    }

    function setLoading(on) {
        var wrap = $('gridWrap');
        var ov = wrap.querySelector('.overlay');
        if (on) {
            if (!ov) {
                ov = document.createElement('div');
                ov.className = 'overlay';
                ov.innerHTML = '<div class="spinner" role="status" aria-label="Đang chạy"></div>';
                wrap.appendChild(ov);
            }
        } else if (ov) {
            ov.remove();
        }
    }

    function note(text, kind) {
        var el = $('note');
        if (!text) { el.hidden = true; return; }
        el.hidden = false;
        el.style.color = kind === 'err' ? 'var(--danger)' : '';
        el.innerHTML = '<svg width="15" height="15"><use href="#i-alert"/></svg><span></span>';
        el.querySelector('span').textContent = text;
    }

    // ------------------------------------------------------------------ schema

    function initModules() {
        var sel = $('srcModule');
        var list = (CFG.sources || []).map(function (m) {
            return { v: m.Key, t: m.Name + (m.Current ? ' (đang mở)' : '') };
        });

        if (!list.length) {
            fill(sel, [], '', 'Không có database nào bạn được xem');
            sel.disabled = true;
            return;
        }

        // Default to a database OTHER than the one being repaired — that is the whole point of the
        // page. Same-module is still selectable, for copying between two tables of one database.
        var other = (CFG.sources.filter(function (m) { return !m.Current; })[0] || CFG.sources[0]).Key;
        fill(sel, list, other);
    }

    /**
     * Turns a text input into a type-to-search picker over whatever items() returns. A plain
     * <select> is unusable against a database with several hundred tables, and this reuses the
     * popup the SQL console and the blueprint designer already use rather than inventing a widget.
     *
     * onPick fires only once the field holds a name that really exists, so a half-typed value never
     * drags the rest of the form along with it.
     */
    function picker(el, items, onPick) {
        var showAll = false;
        var last = null;

        var api = TP.attachAutocomplete(el, {
            getItems: function (value) {
                var all = items();
                if (!all.length) return null;

                // A click on a field that already holds a value should offer the whole list, not
                // just the one item that value happens to match.
                var q = showAll ? '' : value.trim().toLowerCase();
                showAll = false;

                var hit = q
                    ? all.filter(function (it) { return it.label.toLowerCase().indexOf(q) >= 0; })
                    : all;

                return { items: hit, range: [0, value.length] };
            }
        });

        el.addEventListener('focus', function () {
            showAll = true;
            el.select();
            api.refresh();
        });

        el.addEventListener('input', settle);
        // Later than the popup's own 150ms blur delay, or clicking a suggestion would settle on the
        // half-typed text that was in the field a moment before the pick landed.
        el.addEventListener('blur', function () { setTimeout(settle, 170); });

        function settle() {
            var v = el.value.trim();
            var ok = !v || items().some(function (it) { return it.label === v; });

            el.classList.toggle('is-bad', !ok);
            if (!ok || v === last) return;

            last = v;
            onPick(v);
        }

        return {
            /** Sets the field without firing onPick — used when one side mirrors the other. */
            set: function (v) {
                el.value = v || '';
                last = el.value;
                el.classList.remove('is-bad');
            }
        };
    }

    function tableItems(list) {
        return list.map(function (t) { return { label: t.Full, hint: t.IsView ? 'view' : '' }; });
    }

    function columnItems(list, writableOnly) {
        return list
            .filter(function (c) { return !writableOnly || (!c.IsPrimaryKey && !c.IsReadOnly); })
            .map(function (c) { return { label: c.Name, hint: typeHint(c) }; });
    }

    function initPickers() {
        P.dstTable = picker($('dstTable'), function () { return tableItems(S.dstTables); }, function () {
            // The conditions name columns of the previous table, so they cannot survive the change.
            S.filters = [];
            renderFilters();
            loadColumns('dst').then(mirrorSourceTable);
        });

        P.srcTable = picker($('srcTable'), function () { return tableItems(S.srcTables); }, function () {
            loadColumns('src');
        });

        // Picking on the target moves the source to the same name when it has one — the usual case
        // is two copies of one schema, and re-picking every column by hand would be pure friction.
        P.dstMatch = picker($('dstMatch'), function () { return columnItems(S.dstColumns); }, function (v) {
            mirror(v, P.srcMatch, S.srcColumns);
        });
        P.dstColumn = picker($('dstColumn'), function () { return columnItems(S.dstColumns, true); }, function (v) {
            mirror(v, P.srcColumn, S.srcColumns);
        });

        P.srcMatch = picker($('srcMatch'), function () { return columnItems(S.srcColumns); }, noop);
        P.srcColumn = picker($('srcColumn'), function () { return columnItems(S.srcColumns); }, noop);

        P.dstLabel = picker($('dstLabel'), function () { return columnItems(S.dstColumns); }, noop);
    }

    function noop() { }

    function mirror(name, target, cols) {
        if (name && cols.some(function (c) { return c.Name === name; })) target.set(name);
    }

    async function loadTables(side) {
        var isSrc = side === 'src';
        var el = $(isSrc ? 'srcTable' : 'dstTable');
        var moduleKey = isSrc ? $('srcModule').value : '';

        if (isSrc) S.srcTables = []; else S.dstTables = [];

        if (isSrc && !moduleKey) {
            el.placeholder = 'Chọn database trước…';
            return;
        }

        el.placeholder = 'Đang tải danh sách bảng…';
        try {
            var data = await TP.get('/sync/tables' + (moduleKey ? '?source=' + encodeURIComponent(moduleKey) : ''));

            // A view can supply values but can never be written to, so it is offered as a source only.
            var tables = data.filter(function (t) { return isSrc || !t.IsView; });
            if (isSrc) S.srcTables = tables; else S.dstTables = tables;

            el.placeholder = 'Gõ để tìm trong ' + tables.length + ' bảng…';

            if (isSrc) await mirrorSourceTable();
            else await loadColumns('dst');
        } catch (e) {
            el.placeholder = 'Không tải được danh sách bảng';
            TP.toast(e.message, 'err');
        }
    }

    async function loadColumns(side) {
        var isSrc = side === 'src';
        var table = $(isSrc ? 'srcTable' : 'dstTable').value.trim();
        var matchEl = $(isSrc ? 'srcMatch' : 'dstMatch');
        var colEl = $(isSrc ? 'srcColumn' : 'dstColumn');
        var matchP = isSrc ? P.srcMatch : P.dstMatch;
        var colP = isSrc ? P.srcColumn : P.dstColumn;

        if (isSrc) S.srcColumns = []; else S.dstColumns = [];

        if (!table) {
            matchP.set('');
            colP.set('');
            if (!isSrc) P.dstLabel.set('');
            matchEl.placeholder = colEl.placeholder = 'Chọn bảng trước…';
            return;
        }

        var moduleKey = isSrc ? $('srcModule').value : '';
        matchEl.placeholder = colEl.placeholder = 'Đang tải danh sách cột…';

        try {
            var data = await TP.get('/sync/columns?table=' + encodeURIComponent(table)
                + (moduleKey ? '&source=' + encodeURIComponent(moduleKey) : ''));

            var cols = data.Columns.filter(function (c) { return !c.IsSkipped; });
            if (isSrc) S.srcColumns = cols; else S.dstColumns = cols;

            matchEl.placeholder = colEl.placeholder = 'Gõ để tìm trong ' + cols.length + ' cột…';

            if (isSrc) {
                // Keep what is already there if this table still has it; otherwise follow the target.
                keepOrMirror(matchP, matchEl, $('dstMatch').value.trim(), cols);
                keepOrMirror(colP, colEl, $('dstColumn').value.trim(), cols);
            } else {
                var pk = defaultMatch(cols);
                matchP.set(pk);
                colP.set('');
                // Left blank on purpose: the server guesses, and the guess comes back in the result.
                P.dstLabel.set('');
                $('dstLabel').placeholder = 'Để trống = tự chọn';
                mirror(pk, P.srcMatch, S.srcColumns);
            }
        } catch (e) {
            matchEl.placeholder = colEl.placeholder = 'Không tải được danh sách cột';
            TP.toast(e.message, 'err');
        }
    }

    function keepOrMirror(target, el, want, cols) {
        var current = el.value.trim();
        if (current && cols.some(function (c) { return c.Name === current; })) return;

        target.set(cols.some(function (c) { return c.Name === want; }) ? want : '');
    }

    /** Points the source at the same table name as the target, when it has one. */
    async function mirrorSourceTable() {
        var want = $('dstTable').value.trim();
        if (!want || $('srcTable').value.trim() === want) return;
        if (!S.srcTables.some(function (t) { return t.Full === want; })) return;

        P.srcTable.set(want);
        await loadColumns('src');
    }

    /** A single-column primary key is the obvious way to pair rows; otherwise let the user say. */
    function defaultMatch(cols) {
        var pks = cols.filter(function (c) { return c.IsPrimaryKey; });
        return pks.length === 1 ? pks[0].Name : '';
    }

    // ----------------------------------------------------------------- filters

    function renderFilters() {
        var box = $('filters');
        box.innerHTML = S.filters.map(function (f, i) {
            var cols = S.dstColumns.map(function (c) {
                return '<option value="' + esc(c.Name) + '"' + (c.Name === f.Column ? ' selected' : '') + '>'
                    + esc(c.Name) + '</option>';
            }).join('');
            var ops = OPS.map(function (o) {
                return '<option value="' + o.v + '"' + (o.v === f.Op ? ' selected' : '') + '>' + esc(o.t) + '</option>';
            }).join('');
            var noValue = f.Op === 'null' || f.Op === 'notnull';

            return '<div class="filter-row" data-i="' + i + '">'
                + '<select class="select" data-k="Column">' + cols + '</select>'
                + '<select class="select" data-k="Op">' + ops + '</select>'
                + '<input class="input" data-k="Value" value="' + esc(f.Value || '') + '"'
                + (noValue ? ' disabled placeholder="không cần giá trị"' : '') + ' />'
                + '<button type="button" class="icon-btn" data-del aria-label="Xoá điều kiện">'
                + '<svg width="14" height="14"><use href="#i-x"/></svg></button>'
                + '</div>';
        }).join('');
    }

    function initFilters() {
        $('btnAddFilter').addEventListener('click', function () {
            if (!S.dstColumns.length) { TP.toast('Chọn bảng đích trước', 'err'); return; }
            S.filters.push({ Column: S.dstColumns[0].Name, Op: '=', Value: '' });
            renderFilters();
        });

        $('filters').addEventListener('change', function (e) {
            var row = e.target.closest('.filter-row');
            var key = e.target.dataset.k;
            if (!row || !key) return;

            S.filters[+row.dataset.i][key] = e.target.value;
            if (key === 'Op') renderFilters();
        });

        $('filters').addEventListener('click', function (e) {
            var btn = e.target.closest('[data-del]');
            if (!btn) return;
            S.filters.splice(+btn.closest('.filter-row').dataset.i, 1);
            renderFilters();
        });
    }

    // -------------------------------------------------------------------- plan

    function config() {
        return {
            SourceModule: $('srcModule').value,
            SourceTable: $('srcTable').value.trim(),
            SourceMatchColumn: $('srcMatch').value.trim(),
            SourceColumn: $('srcColumn').value.trim(),
            TargetTable: $('dstTable').value.trim(),
            MatchColumn: $('dstMatch').value.trim(),
            Column: $('dstColumn').value.trim(),
            LabelColumn: $('dstLabel').value.trim(),
            Mode: +document.querySelector('input[name=mode]:checked').value,
            Filters: S.filters.filter(function (f) {
                return f.Column && (f.Value || f.Op === 'null' || f.Op === 'notnull');
            })
        };
    }

    /* The fields are free text now, so "nothing picked" and "typed a name that does not exist" are
       different mistakes and deserve different messages. */
    function missing(cfg) {
        if (!cfg.SourceModule) return 'Chưa chọn database nguồn';

        return check(cfg.TargetTable, S.dstTables, 'Full', 'bảng đích')
            || check(cfg.SourceTable, S.srcTables, 'Full', 'bảng nguồn')
            || check(cfg.MatchColumn, S.dstColumns, 'Name', 'khóa ghép ở đích')
            || check(cfg.SourceMatchColumn, S.srcColumns, 'Name', 'khóa ghép ở nguồn')
            || check(cfg.Column, S.dstColumns, 'Name', 'cột cần vá')
            || check(cfg.SourceColumn, S.srcColumns, 'Name', 'cột lấy giá trị ở nguồn')
            || (cfg.LabelColumn ? check(cfg.LabelColumn, S.dstColumns, 'Name', 'cột nhận dạng') : null);
    }

    function check(value, list, key, label) {
        if (!value) return 'Chưa chọn ' + label;

        return list.some(function (x) { return x[key] === value; })
            ? null
            : 'Không có ' + label + ' tên "' + value + '" — chọn từ danh sách gợi ý';
    }

    async function plan() {
        var cfg = config();
        var bad = missing(cfg);
        if (bad) { TP.toast(bad, 'err'); return; }

        setLoading(true);
        note('');
        try {
            var data = await TP.post('/sync/plan', cfg);

            S.plan = data;
            S.rows = data.Rows;
            S.frozen = false;

            // The server may have picked the label column itself; show which one it went with.
            if (data.LabelColumn) P.dstLabel.set(data.LabelColumn);
            S.hiddenStates = new Set();

            // Only the safe case starts ticked. An overwrite is always a deliberate click.
            S.picked = new Set();
            S.rows.forEach(function (r, i) {
                if ((STATES[r.State] || {}).pick) S.picked.add(i);
            });

            render();

            var messages = (data.Warnings || []).slice();
            if (!data.CanApply && cfg.Mode !== 2) {
                messages.push('Tài khoản hoặc cấu hình không cho ghi vào database này — chỉ xem được.');
            }
            note(messages.join(' · '));
        } catch (e) {
            S.plan = null;
            S.rows = [];
            S.picked = new Set();
            render();
            note(e.message, 'err');
            TP.toast(e.message, 'err');
        } finally {
            setLoading(false);
        }
    }

    // ------------------------------------------------------------------ render

    function visible(row) { return !S.hiddenStates.has(row.State); }

    function pkText(row) {
        return Object.keys(row.Key).map(function (k) { return k + '=' + display(row.Key[k]); }).join(', ');
    }

    /** True when the match column IS the whole primary key, in which case a PK column adds nothing. */
    function pkIsMatch() {
        if (!S.plan) return false;
        var keys = Object.keys(S.rows.length ? S.rows[0].Key : {});
        return keys.length === 1 && keys[0].toLowerCase() === (S.plan.MatchColumn || '').toLowerCase();
    }

    function render() {
        renderChips();
        renderGrid();
        renderFoot();
    }

    function renderChips() {
        var box = $('chips');
        if (!S.plan || !S.rows.length) { box.hidden = true; return; }

        var counts = {};
        S.rows.forEach(function (r) { counts[r.State] = (counts[r.State] || 0) + 1; });

        box.hidden = false;
        box.innerHTML = ORDER.filter(function (k) { return counts[k]; }).map(function (k) {
            var st = STATES[k];
            var off = S.hiddenStates.has(k);
            return '<button type="button" class="st st--' + st.cls + (off ? ' is-off' : '')
                + '" data-state="' + k + '" title="Bấm để ẩn/hiện nhóm này">'
                + esc(st.text) + ' <b>' + TP.num(counts[k]) + '</b></button>';
        }).join('')
            + (S.frozen || !canPickAny() ? ''
                : '<button type="button" class="chip" id="btnPickFill">Chọn hết nhóm ghi được</button>');
    }

    function canPickAny() {
        return S.plan && S.plan.CanApply
            && S.rows.some(function (r) { return (STATES[r.State] || {}).writable; });
    }

    function renderGrid() {
        var wrap = $('gridWrap');

        if (!S.plan) {
            wrap.innerHTML = emptyHtml('Chưa có gì để so',
                'Chọn database nguồn, bảng, khóa ghép và cột cần vá rồi bấm "Xem trước".', 'i-refresh');
            return;
        }

        var shown = S.rows.filter(visible).length;
        if (!shown) {
            wrap.innerHTML = emptyHtml('Không có dòng nào',
                S.rows.length ? 'Mọi nhóm đang bị ẩn — bấm lại vào nhãn phía trên để hiện.'
                    : 'Không dòng nào ở bảng đích khớp phạm vi đang chọn.');
            return;
        }

        var showPk = !pkIsMatch();
        var pick = !S.frozen && S.plan.CanApply;

        var head = '<tr>'
            + (pick ? '<th class="rowno"><input type="checkbox" id="ckAll" aria-label="Chọn tất cả đang hiện" /></th>'
                : '<th class="rowno">#</th>')
            + (showPk ? '<th>Khóa chính</th>' : '')
            + (S.plan.LabelColumn ? '<th>' + esc(S.plan.LabelColumn) + '</th>' : '')
            + '<th>' + esc(S.plan.MatchColumn) + '</th>'
            + '<th>Đang có ở đích</th>'
            + '<th>Sẽ ghi vào</th>'
            + '<th>Trạng thái</th>'
            + '</tr>';

        // Mapped over the whole list rather than a filtered copy, so the row index stays the identity
        // used by the checkboxes without an indexOf lookup per row.
        var body = S.rows.map(function (r, i) {
            if (!visible(r)) return '';

            var st = STATES[r.State] || { text: r.State, cls: 'mut' };
            var canPick = pick && st.writable;

            return '<tr data-i="' + i + '">'
                + '<td class="rowno">'
                + (canPick ? '<input type="checkbox" data-pick' + (S.picked.has(i) ? ' checked' : '') + ' />'
                    : (i + 1))
                + '</td>'
                + (showPk ? '<td title="' + esc(pkText(r)) + '">' + esc(pkText(r)) + '</td>' : '')
                + (S.plan.LabelColumn
                    ? '<td' + cellClass(r.Label) + '>' + esc(display(r.Label)) + '</td>' : '')
                + '<td' + cellClass(r.Match) + '>' + esc(display(r.Match)) + '</td>'
                + '<td' + cellClass(r.Current) + '>' + esc(display(r.Current)) + '</td>'
                + '<td' + cellClass(r.Incoming) + '>' + esc(display(r.Incoming)) + '</td>'
                + '<td><span class="st st--' + st.cls + '">' + esc(st.text) + '</span>'
                + (r.Note ? ' <span class="st-note">' + esc(r.Note) + '</span>' : '')
                + '</td>'
                + '</tr>';
        }).join('');

        wrap.innerHTML = '<table class="grid"><thead>' + head + '</thead><tbody>' + body + '</tbody></table>';
    }

    function renderFoot() {
        $('statScanned').textContent = S.plan ? TP.num(S.plan.Scanned != null ? S.plan.Scanned : S.rows.length) : '0';
        $('statPicked').textContent = TP.num(S.picked.size);
        $('statTime').textContent = S.plan && S.plan.ElapsedMs != null ? S.plan.ElapsedMs + ' ms' : '';

        var bar = $('applyBar');
        var show = !S.frozen && S.plan && S.plan.CanApply && S.picked.size > 0;
        bar.hidden = !show;

        if (show) {
            var over = 0;
            S.picked.forEach(function (i) { if (S.rows[i].State === 'Overwrite') over++; });
            $('applyText').textContent = 'Đã chọn ' + S.picked.size + ' dòng'
                + (over ? ' — trong đó ' + over + ' dòng sẽ GHI ĐÈ dữ liệu đang có' : '');
        }
    }

    // ------------------------------------------------------------------- apply

    function openModal(title, bodyHtml, okText, hint, onOk) {
        $('modalTitle').textContent = title;
        $('modalBody').innerHTML = bodyHtml;
        $('modalOk').textContent = okText;
        $('modalOk').hidden = !onOk;
        $('modalHint').textContent = hint || '';
        S.modalOk = onOk;
        $('modal').dataset.open = 'true';
    }

    function closeModal() {
        $('modal').dataset.open = 'false';
        S.modalOk = null;
    }

    function confirmApply() {
        if (!S.picked.size) return;

        var picked = [];
        S.picked.forEach(function (i) { picked.push(S.rows[i]); });

        var over = picked.filter(function (r) { return r.State === 'Overwrite'; });
        var sample = picked.slice(0, 15);

        var html = '<div class="kvgrid">'
            + kvi('Bảng', S.plan.TargetTable + ' · cột ' + S.plan.Column)
            + kvi('Lấy từ', S.plan.SourceModuleName + ' · ' + S.plan.SourceTable + ' · cột ' + S.plan.SourceColumn)
            + kvi('Ghép theo', S.plan.MatchColumn + ' ↔ ' + S.plan.SourceMatchColumn)
            + kvi('Số dòng', picked.length + ' dòng'
                + (over.length ? ' (' + over.length + ' dòng ghi đè)' : ''))
            + '</div>';

        if (over.length) {
            html += '<p class="err" style="margin:12px 0 0">'
                + over.length + ' dòng đang có dữ liệu và sẽ bị thay. Giá trị cũ vẫn được ghi vào nhật ký.</p>';
        }

        html += '<div class="section-title">Xem thử ' + sample.length + ' dòng đầu</div>'
            + '<table class="kv"><tbody>'
            + sample.map(function (r) {
                return '<tr><th>' + esc(display(r.Label != null ? r.Label : r.Match)) + '</th><td>'
                    + '<span class="null">' + esc(display(r.Current)) + '</span> → <b>'
                    + esc(display(r.Incoming)) + '</b></td></tr>';
            }).join('')
            + '</tbody></table>';

        openModal('Xác nhận ghi ' + picked.length + ' dòng', html, 'Ghi ' + picked.length + ' dòng',
            'Mỗi dòng một câu UPDATE theo khóa chính, và chỉ ghi khi giá trị cũ vẫn đúng như đang thấy.',
            apply);
    }

    function kvi(k, v) {
        return '<div class="kvi"><span class="kvk">' + esc(k) + '</span>'
            + '<span class="kvv">' + esc(v) + '</span></div>';
    }

    async function apply() {
        var keys = [];
        S.picked.forEach(function (i) { keys.push(S.rows[i].Key); });

        closeModal();
        setLoading(true);
        try {
            var body = config();
            body.Keys = keys;

            var data = await TP.post('/sync/apply', body);

            // Show what actually happened, per row, instead of the plan that is now out of date.
            S.rows = data.Rows;
            S.picked = new Set();
            S.frozen = true;
            S.hiddenStates = new Set();
            S.plan = Object.assign({}, S.plan, {
                Scanned: data.Rows.length,
                ElapsedMs: data.ElapsedMs,
                ApplySql: data.Sql
            });

            render();
            note('Đã ghi ' + data.Updated + ' dòng, bỏ qua ' + data.Skipped
                + '. Bấm "Xem trước" để chạy lại và kiểm tra phần còn lại.'
                + (data.Updated
                    ? ' Cần hoàn tác thì vào Tra cứu → Lịch sử, mở dòng sync này và bấm "Copy hoàn tác cả lần chạy".'
                    : ''));
            TP.toast('Đã ghi ' + data.Updated + ' dòng', data.Updated ? 'ok' : 'err');
        } catch (e) {
            note(e.message, 'err');
            TP.toast(e.message, 'err');
        } finally {
            setLoading(false);
        }
    }

    // ------------------------------------------------------------------- wiring

    function initGrid() {
        $('gridWrap').addEventListener('change', function (e) {
            if (e.target.id === 'ckAll') {
                var on = e.target.checked;
                S.rows.forEach(function (r, i) {
                    if (!visible(r) || !(STATES[r.State] || {}).writable) return;
                    if (on) S.picked.add(i); else S.picked.delete(i);
                });
                renderGrid();
                renderFoot();
                return;
            }

            if (e.target.dataset.pick !== undefined) {
                var i = +e.target.closest('tr').dataset.i;
                if (e.target.checked) S.picked.add(i); else S.picked.delete(i);
                renderFoot();
            }
        });

        $('chips').addEventListener('click', function (e) {
            if (e.target.closest('#btnPickFill')) {
                S.rows.forEach(function (r, i) {
                    if (visible(r) && (STATES[r.State] || {}).writable) S.picked.add(i);
                });
                render();
                return;
            }

            var chip = e.target.closest('[data-state]');
            if (!chip) return;

            var key = chip.dataset.state;
            if (S.hiddenStates.has(key)) S.hiddenStates.delete(key); else S.hiddenStates.add(key);
            render();
        });
    }

    function initModal() {
        $('modal').addEventListener('click', function (e) {
            if (e.target.closest('[data-close]') || e.target.id === 'modal') closeModal();
        });
        $('modalOk').addEventListener('click', function () {
            if (S.modalOk) S.modalOk();
        });
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') closeModal();
        });
    }

    function showSql() {
        if (!S.plan) { TP.toast('Chưa chạy lần nào', 'err'); return; }

        var parts = [
            ['Đọc ở bảng đích', S.plan.TargetSql],
            ['Đọc ở bảng nguồn', S.plan.SourceSql],
            ['Câu ghi (mẫu một dòng)', S.plan.ApplySql]
        ].filter(function (p) { return p[1]; });

        openModal('SQL đã chạy', parts.map(function (p) {
            return '<div class="section-title">' + esc(p[0]) + '</div>'
                + '<pre class="sql-box">' + esc(p[1]) + '</pre>';
        }).join(''), '', 'Đọc luôn ở READ UNCOMMITTED nên không khoá bảng của ai.', null);
    }

    function exportCsv() {
        if (!S.rows.length) { TP.toast('Chưa có dữ liệu', 'err'); return; }

        var cols = ['KhoaChinh'];
        if (S.plan.LabelColumn) cols.push(S.plan.LabelColumn);
        cols = cols.concat([S.plan.MatchColumn, 'DangCoODich', 'SeGhiVao', 'TrangThai', 'GhiChu']);

        var rows = S.rows.filter(visible).map(function (r) {
            var o = {};
            o.KhoaChinh = pkText(r);
            if (S.plan.LabelColumn) o[S.plan.LabelColumn] = r.Label;
            o[S.plan.MatchColumn] = r.Match;
            o.DangCoODich = r.Current;
            o.SeGhiVao = r.Incoming;
            o.TrangThai = (STATES[r.State] || {}).text || r.State;
            o.GhiChu = r.Note || '';
            return o;
        });

        TP.downloadCsv('dongbo-' + S.plan.Column + '.csv', cols, rows);
    }

    function init() {
        initModules();
        initPickers();
        initFilters();
        initGrid();
        initModal();

        $('srcModule').addEventListener('change', function () { loadTables('src'); });

        $('btnPlan').addEventListener('click', plan);
        $('btnApply').addEventListener('click', confirmApply);
        $('btnNone').addEventListener('click', function () { S.picked = new Set(); render(); });
        $('btnSql').addEventListener('click', showSql);
        $('btnCsv').addEventListener('click', exportCsv);

        if (!CFG.canEdit) {
            note('Database này đang ở chế độ chỉ đọc — vẫn xem được chỗ lệch nhưng không ghi được.');
        }

        render();

        // Target first: the source list preselects whatever table the target landed on.
        loadTables('dst').then(function () { return loadTables('src'); });
    }

    document.addEventListener('DOMContentLoaded', init);
})();
