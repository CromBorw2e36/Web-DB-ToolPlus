/* Table copy — compare whole tables with another database, then pull over what the user chose.
   Three parts: pick tables, read the per-table summary and the rows behind it, then write.
   Every server call is a short step that hands back a cursor, so a big table shows progress and
   no single request lives long enough to be cut by a proxy. No third-party libraries. */
(function () {
    'use strict';

    var CFG = window.TP_COPY || { canApply: false, currentModule: '', currentName: '', maxListed: 500, sources: [] };
    var EMPTY_RUN = '00000000-0000-0000-0000-000000000000';

    /* Keyed by the name the server sends (CopyKind, serialised by name). */
    var KINDS = {
        Insert: { text: 'Thiếu ở đích', verb: 'thêm', sign: '+', cls: 'ok', opt: 'Thêm' },
        Update: { text: 'Khác nhau', verb: 'cập nhật', sign: '~', cls: 'warn', opt: 'Cập nhật' },
        Delete: { text: 'Thừa ở đích', verb: 'xoá', sign: '−', cls: 'danger', opt: 'Xoá' }
    };
    var KIND_ORDER = ['Insert', 'Update', 'Delete'];
    var COUNT = { Insert: 'ins', Update: 'upd', Delete: 'del' };

    var S = {
        tables: [],
        chosen: new Set(),
        results: {},       // table -> R
        order: [],         // tables in the order they were compared
        current: null,     // table shown in the detail grid
        hiddenKinds: new Set(),
        busy: false,
        stop: false,
        modalOk: null
    };

    function $(id) { return document.getElementById(id); }
    var esc = function (s) { return TP.escapeHtml(s); };

    // ------------------------------------------------------------------ helpers

    function display(v) {
        if (v === null || v === undefined) return 'NULL';
        if (v === '') return '(rỗng)';
        return String(v);
    }

    function rowsText(n) { return n < 0 ? '?' : '~' + TP.num(n); }

    function emptyHtml(title, text, icon) {
        return '<div class="empty"><svg width="44" height="44"><use href="#' + (icon || 'i-inbox') + '"/></svg>'
            + '<h3>' + esc(title) + '</h3>' + (text ? '<p>' + esc(text) + '</p>' : '') + '</div>';
    }

    function note(text, kind) {
        var el = $('note');
        if (!text) { el.hidden = true; return; }
        el.hidden = false;
        el.style.color = kind === 'err' ? 'var(--danger)' : '';
        el.innerHTML = '<svg width="15" height="15"><use href="#i-alert"/></svg><span></span>';
        el.querySelector('span').textContent = text;
    }

    function setBusy(on) {
        S.busy = on;
        // Cleared only when a run starts: the end of a run still has to read it to report a stop.
        if (on) S.stop = false;
        $('btnStop').hidden = !on;
        $('btnCompare').disabled = on;
        $('btnApply').disabled = on;
        $('srcModule').disabled = on || !(CFG.sources || []).length;
    }

    function keyText(key) {
        return Object.keys(key).map(function (k) { return k + '=' + display(key[k]); }).join(', ');
    }

    // ------------------------------------------------------------ table list

    function initModules() {
        var sel = $('srcModule');
        var list = CFG.sources || [];
        if (!list.length) {
            sel.innerHTML = '<option value="">Không có database nào khác bạn được xem</option>';
            sel.disabled = true;
            return;
        }
        sel.innerHTML = list.map(function (m) {
            return '<option value="' + esc(m.Key) + '">' + esc(m.Name) + '</option>';
        }).join('');
    }

    async function loadTables() {
        var mod = $('srcModule').value;
        S.tables = [];
        S.chosen = new Set();
        resetResults();
        if (!mod) { renderList(); return; }

        $('tblList').innerHTML = '<div class="hint" style="padding:8px">Đang tải danh sách bảng…</div>';
        try {
            S.tables = await TP.get('/copy/tables?source=' + encodeURIComponent(mod));
        } catch (e) {
            TP.toast(e.message, 'err');
            note(e.message, 'err');
        }
        renderList();
    }

    function usable(t) { return t.InSource && t.HasPrimaryKey; }

    function shownTables() {
        var q = $('tblFilter').value.trim().toLowerCase();
        var onlyDiff = $('onlyDiff').checked;
        return S.tables.filter(function (t) {
            if (q && t.Full.toLowerCase().indexOf(q) < 0) return false;
            if (onlyDiff && t.SourceRows === t.TargetRows) return false;
            return true;
        });
    }

    function renderList() {
        var box = $('tblList');
        var shown = shownTables();

        if (!S.tables.length) {
            box.innerHTML = '<div class="hint" style="padding:8px">Chưa có bảng nào — chọn database nguồn.</div>';
        } else if (!shown.length) {
            box.innerHTML = '<div class="hint" style="padding:8px">Không bảng nào khớp bộ lọc.</div>';
        } else {
            box.innerHTML = shown.map(function (t) {
                var ok = usable(t);
                var why = !t.InSource ? 'không có ở nguồn' : (!t.HasPrimaryKey ? 'không có khóa chính' : '');
                var diff = t.InSource && t.SourceRows !== t.TargetRows;
                return '<label class="copy-item' + (ok ? '' : ' is-off') + '" title="' + esc(why || t.Full) + '">'
                    + '<input type="checkbox" data-t="' + esc(t.Full) + '"' + (S.chosen.has(t.Full) ? ' checked' : '')
                    + (ok ? '' : ' disabled') + ' />'
                    + '<span class="name">' + esc(t.Full) + '</span>'
                    + (why ? '<span class="badge badge--ro">' + esc(why) + '</span>' : '')
                    + '<span class="rows' + (diff ? ' is-diff' : '') + '">nguồn ' + rowsText(t.SourceRows)
                    + ' · đích ' + rowsText(t.TargetRows) + '</span>'
                    + '</label>';
            }).join('');
        }

        $('pickCount').textContent = S.chosen.size ? 'Đã chọn ' + S.chosen.size + ' bảng' : '';
    }

    function initList() {
        $('tblFilter').addEventListener('input', renderList);
        $('onlyDiff').addEventListener('change', renderList);

        $('tblList').addEventListener('change', function (e) {
            var t = e.target.dataset.t;
            if (!t) return;
            if (e.target.checked) S.chosen.add(t); else S.chosen.delete(t);
            $('pickCount').textContent = S.chosen.size ? 'Đã chọn ' + S.chosen.size + ' bảng' : '';
        });

        $('btnPickShown').addEventListener('click', function () {
            shownTables().forEach(function (t) { if (usable(t)) S.chosen.add(t.Full); });
            renderList();
        });
        $('btnPickNone').addEventListener('click', function () {
            S.chosen = new Set();
            renderList();
        });
    }

    // ----------------------------------------------------------------- compare

    function resetResults() {
        S.results = {};
        S.order = [];
        S.current = null;
        render();
    }

    function newResult(name) {
        return {
            name: name, status: 'queued', error: null,
            keys: [], columns: [], label: '', canApply: false,
            srcApprox: -1, tgtApprox: -1, srcScanned: 0, tgtScanned: 0,
            ins: 0, upd: 0, del: 0, same: 0,
            rows: [], listed: { Insert: 0, Update: 0, Delete: 0 },
            warnings: [],
            /* Kinds pulled over when the table is written. Deleting is never on by default: it is
               the one choice that removes data from the target for good. */
            opts: { Insert: true, Update: true, Delete: false },
            /* null = "every row of the ticked kinds". A Set = only these rows (indexes into rows). */
            picked: null,
            applied: null
        };
    }

    async function compareAll(names) {
        if (!names.length) { TP.toast('Chọn ít nhất một bảng', 'err'); return; }

        S.results = {};
        S.order = names.slice();
        names.forEach(function (n) { S.results[n] = newResult(n); });
        S.current = names[0];
        note('');
        setBusy(true);
        render();

        try {
            for (var i = 0; i < names.length && !S.stop; i++) {
                await compareOne(S.results[names[i]]);
            }
        } finally {
            setBusy(false);
            render();
        }

        var bad = S.order.filter(function (n) { return S.results[n].status === 'error'; }).length;
        var warns = [];
        S.order.forEach(function (n) {
            S.results[n].warnings.forEach(function (w) { warns.push(n + ': ' + w); });
        });
        if (S.stop) warns.unshift('Đã dừng giữa chừng — các bảng chưa so xong không ghi được.');
        if (bad) warns.unshift(bad + ' bảng lỗi — xem cột trạng thái.');
        if (!CFG.canApply) warns.push('Database này không cho ghi (chưa bật CopyTarget hoặc tài khoản chỉ có quyền xem) — chỉ so sánh.');
        note(warns.join(' · '));
    }

    async function compareOne(R) {
        R.status = 'running';
        renderSummary();

        var cursor = null;
        var first = true;
        try {
            do {
                var data = await TP.post('/copy/compare', {
                    SourceModule: $('srcModule').value, Table: R.name, Cursor: cursor
                });

                if (first) {
                    R.keys = data.Keys;
                    R.columns = data.Columns;
                    R.label = data.LabelColumn;
                    R.canApply = data.CanApply && CFG.canApply;
                    R.srcApprox = data.SourceApprox;
                    R.tgtApprox = data.TargetApprox;
                    R.warnings = data.Warnings || [];
                    first = false;
                }

                R.srcScanned += data.SourceScanned;
                R.tgtScanned += data.TargetScanned;
                R.ins += data.Insert;
                R.upd += data.Update;
                R.del += data.Delete;
                R.same += data.Same;

                data.Rows.forEach(function (row) {
                    if (R.listed[row.Kind] >= CFG.maxListed) return;
                    R.listed[row.Kind]++;
                    R.rows.push(row);
                });

                cursor = data.Cursor;
                renderSummary();
                if (S.current === R.name) renderDetail();
            } while (cursor && !S.stop);

            R.status = cursor ? 'stopped' : 'done';
        } catch (e) {
            R.status = 'error';
            R.error = e.message;
        }
        renderSummary();
        if (S.current === R.name) renderDetail();
    }

    // -------------------------------------------------------------- selection

    function writable(R) { return R.status === 'done' && R.canApply; }

    function truncated(R, kind) { return R[COUNT[kind]] > R.listed[kind]; }

    function isPicked(R, i) {
        return R.picked ? R.picked.has(i) : !!R.opts[R.rows[i].Kind];
    }

    /** Switches a table to hand-picked rows, starting from what "by kind" currently means. */
    function goManual(R) {
        if (R.picked) return;
        R.picked = new Set();
        R.rows.forEach(function (r, i) { if (R.opts[r.Kind]) R.picked.add(i); });
    }

    /** What would be written for one table: per-kind counts, and the keys when picked by hand. */
    function selection(R) {
        var sel = { manual: !!R.picked, count: { Insert: 0, Update: 0, Delete: 0 }, keys: { Insert: [], Update: [], Delete: [] } };
        if (!writable(R)) return sel;

        if (!R.picked) {
            KIND_ORDER.forEach(function (k) { if (R.opts[k]) sel.count[k] = R[COUNT[k]]; });
        } else {
            R.picked.forEach(function (i) {
                var r = R.rows[i];
                sel.count[r.Kind]++;
                sel.keys[r.Kind].push(r.Key);
            });
        }
        sel.total = sel.count.Insert + sel.count.Update + sel.count.Delete;
        return sel;
    }

    // ------------------------------------------------------------------ render

    function render() {
        renderSummary();
        renderDetail();
    }

    function statusHtml(R) {
        switch (R.status) {
            case 'queued': return '<span class="st st--mut">Chờ</span>';
            case 'running':
                var pct = R.srcApprox > 0 ? Math.min(99, Math.floor(R.srcScanned * 100 / R.srcApprox)) : null;
                return '<span class="st st--warn">Đang so' + (pct != null ? ' ' + pct + '%' : '') + '</span>';
            case 'stopped': return '<span class="st st--mut">Đã dừng</span>';
            case 'error': return '<span class="st st--danger">Lỗi</span> <span class="st-note">' + esc(R.error) + '</span>';
            case 'applying': return '<span class="st st--warn">Đang ghi…</span>' + appliedHtml(R);
            case 'applied': return '<span class="st st--ok">Đã ghi</span>' + appliedHtml(R);
            default:
                if (!R.ins && !R.upd && !R.del) return '<span class="st st--ok">Giống hệt</span>';
                return R.canApply ? '<span class="st st--mut">Đã so xong</span>'
                    : '<span class="st st--mut">Chỉ xem</span>';
        }
    }

    function appliedHtml(R) {
        var a = R.applied;
        if (!a) return '';
        return ' <span class="st-note">+' + TP.num(a.ins) + ' ~' + TP.num(a.upd) + ' −' + TP.num(a.del)
            + (a.skipped ? ' · bỏ qua ' + TP.num(a.skipped) : '') + '</span>';
    }

    function renderSummary() {
        var wrap = $('sumWrap');
        if (!S.order.length) { wrap.hidden = true; renderFoot(); return; }
        wrap.hidden = false;

        var head = '<tr><th>Bảng</th><th class="num">Nguồn</th><th class="num">Đích</th>'
            + KIND_ORDER.map(function (k) { return '<th class="num">' + esc(KINDS[k].text) + '</th>'; }).join('')
            + '<th class="num">Giống</th><th>Kéo sang</th><th>Trạng thái</th></tr>';

        var body = S.order.map(function (name) {
            var R = S.results[name];
            var done = R.status !== 'queued';
            var src = R.status === 'done' || R.status === 'applied' ? TP.num(R.srcScanned) : rowsText(R.srcApprox);
            var tgt = R.status === 'done' || R.status === 'applied' ? TP.num(R.tgtScanned) : rowsText(R.tgtApprox);

            var opts = writable(R)
                ? '<span class="copy-opts">' + KIND_ORDER.map(function (k) {
                    var n = R[COUNT[k]];
                    return '<label class="' + (k === 'Delete' ? 'is-danger' : '') + '"'
                        + (n ? '' : ' style="opacity:.45"') + '>'
                        + '<input type="checkbox" data-opt="' + k + '"' + (R.opts[k] ? ' checked' : '')
                        + (n ? '' : ' disabled') + ' />' + esc(KINDS[k].opt) + '</label>';
                }).join('') + (R.picked ? ' <span class="badge">chọn tay</span>' : '') + '</span>'
                : '';

            return '<tr data-t="' + esc(name) + '"' + (S.current === name ? ' class="is-current"' : '') + '>'
                + '<td>' + esc(name) + '</td>'
                + '<td class="num">' + src + '</td>'
                + '<td class="num">' + tgt + '</td>'
                + KIND_ORDER.map(function (k) {
                    var n = R[COUNT[k]];
                    return '<td class="num">' + (done && n ? '<span class="st st--' + KINDS[k].cls + '">'
                        + KINDS[k].sign + TP.num(n) + '</span>' : (done ? '0' : '')) + '</td>';
                }).join('')
                + '<td class="num">' + (done ? TP.num(R.same) : '') + '</td>'
                + '<td>' + opts + '</td>'
                + '<td>' + statusHtml(R) + '</td>'
                + '</tr>';
        }).join('');

        wrap.innerHTML = '<table class="grid"><thead>' + head + '</thead><tbody>' + body + '</tbody></table>';
        renderFoot();
    }

    function renderDetail() {
        var head = $('detailHead');
        var wrap = $('gridWrap');
        var R = S.current ? S.results[S.current] : null;

        if (!R) {
            head.hidden = true;
            wrap.innerHTML = emptyHtml('Chưa có gì để so',
                'Chọn database nguồn, tick các bảng cần làm rồi bấm "So sánh". Mỗi dòng được ghép theo khóa chính.',
                'i-download');
            return;
        }

        head.hidden = false;
        var chips = '<b style="font-family:var(--mono);font-size:12px">' + esc(R.name) + '</b>'
            + KIND_ORDER.filter(function (k) { return R[COUNT[k]]; }).map(function (k) {
                var off = S.hiddenKinds.has(k);
                return '<button type="button" class="st st--' + KINDS[k].cls + (off ? ' is-off' : '')
                    + '" data-kind="' + k + '" title="Bấm để ẩn/hiện nhóm này">'
                    + esc(KINDS[k].text) + ' <b>' + TP.num(R[COUNT[k]]) + '</b></button>';
            }).join('');

        var cut = KIND_ORDER.filter(function (k) { return truncated(R, k); });
        if (cut.length) {
            chips += '<span class="st-note">Chỉ liệt kê ' + TP.num(CFG.maxListed) + ' dòng đầu mỗi nhóm'
                + (R.picked ? ' — đang chọn tay nên chỉ những dòng đã tick được ghi'
                    : ' — chọn theo nhóm sẽ ghi toàn bộ') + '</span>';
        }
        if (R.picked) {
            chips += '<button type="button" class="chip" id="btnByKind">Quay về chọn theo nhóm</button>';
        }
        head.innerHTML = chips;

        if (R.status === 'error') {
            wrap.innerHTML = emptyHtml('Không so được bảng này', R.error, 'i-alert');
            return;
        }
        if (!R.rows.length) {
            wrap.innerHTML = R.status === 'running' || R.status === 'queued'
                ? emptyHtml('Đang so sánh…', 'Các dòng lệch sẽ hiện ở đây ngay khi tìm thấy.', 'i-clock')
                : emptyHtml('Hai bên giống hệt nhau', 'Không có dòng nào thiếu, khác hay thừa.', 'i-check');
            return;
        }

        var pick = writable(R);
        var headRow = '<tr>'
            + (pick ? '<th class="rowno"><input type="checkbox" id="ckAll" aria-label="Chọn tất cả đang hiện" /></th>'
                : '<th class="rowno">#</th>')
            + '<th>Loại</th><th>Khóa chính</th>'
            + (R.label ? '<th>' + esc(R.label) + '</th>' : '')
            + '<th>Chi tiết</th></tr>';

        var body = R.rows.map(function (r, i) {
            if (S.hiddenKinds.has(r.Kind)) return '';
            var k = KINDS[r.Kind];
            return '<tr data-i="' + i + '">'
                + '<td class="rowno">' + (pick ? '<input type="checkbox" data-pick' + (isPicked(R, i) ? ' checked' : '') + ' />' : (i + 1)) + '</td>'
                + '<td><span class="st st--' + k.cls + '">' + esc(k.text) + '</span></td>'
                + '<td title="' + esc(keyText(r.Key)) + '">' + esc(keyText(r.Key)) + '</td>'
                + (R.label ? '<td' + (r.Label == null ? ' class="null"' : '') + '>' + esc(display(r.Label)) + '</td>' : '')
                + '<td class="chg" data-view title="Bấm để xem đầy đủ">' + detailHtml(R, r) + '</td>'
                + '</tr>';
        }).join('');

        wrap.innerHTML = '<table class="grid"><thead>' + headRow + '</thead><tbody>' + body + '</tbody></table>';
    }

    function detailHtml(R, r) {
        if (r.Kind === 'Update') {
            return (r.Changes || []).map(function (c) {
                return '<b>' + esc(c.Column) + '</b>: <span class="from">' + esc(display(c.From)) + '</span> → '
                    + esc(display(c.To));
            }).join(' · ');
        }
        // Key columns are already shown; the rest, compactly.
        var vals = r.Values || {};
        return Object.keys(vals).filter(function (c) { return !(c in r.Key); }).slice(0, 6).map(function (c) {
            return '<b>' + esc(c) + '</b>=' + esc(display(vals[c]));
        }).join(' · ');
    }

    function renderFoot() {
        $('statTables').textContent = S.order.length;

        var running = S.order.map(function (n) { return S.results[n]; })
            .filter(function (R) { return R.status === 'running' || R.status === 'applying'; })[0];
        $('statProgress').textContent = running
            ? (running.status === 'running' ? 'Đang so ' : 'Đang ghi ') + running.name
            + (running.status === 'running' ? ' · đã đọc ' + TP.num(running.srcScanned) + ' dòng nguồn' : '')
            : '';

        var t = totals();
        var bar = $('applyBar');
        bar.hidden = S.busy || !t.total;
        if (t.total) {
            $('applyText').textContent = 'Sẽ kéo sang ' + CFG.currentName + ': '
                + KIND_ORDER.filter(function (k) { return t.count[k]; }).map(function (k) {
                    return KINDS[k].verb + ' ' + TP.num(t.count[k]);
                }).join(', ')
                + ' dòng trên ' + t.tables + ' bảng'
                + (t.count.Delete ? ' — CÓ XOÁ dữ liệu ở đích' : '');
        }
    }

    function totals() {
        var t = { total: 0, tables: 0, count: { Insert: 0, Update: 0, Delete: 0 } };
        S.order.forEach(function (n) {
            var sel = selection(S.results[n]);
            if (!sel.total) return;
            t.tables++;
            t.total += sel.total;
            KIND_ORDER.forEach(function (k) { t.count[k] += sel.count[k]; });
        });
        return t;
    }

    // ------------------------------------------------------------------ modal

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

    function showRow(R, r) {
        var html = '<div class="kvgrid">'
            + '<div class="kvi"><span class="kvk">Bảng</span><span class="kvv">' + esc(R.name) + '</span></div>'
            + '<div class="kvi"><span class="kvk">Khóa</span><span class="kvv">' + esc(keyText(r.Key)) + '</span></div>'
            + '</div>';

        if (r.Kind === 'Update') {
            html += '<div class="section-title">Các cột khác nhau</div><table class="kv"><thead><tr><th>Cột</th>'
                + '<td><b>Đang có ở đích</b> → <b>Sẽ ghi (từ nguồn)</b></td></tr></thead><tbody>'
                + (r.Changes || []).map(function (c) {
                    return '<tr><th>' + esc(c.Column) + '</th><td><span class="null">' + esc(display(c.From))
                        + '</span> → <b>' + esc(display(c.To)) + '</b></td></tr>';
                }).join('') + '</tbody></table>';
        } else {
            var vals = r.Values || {};
            html += '<div class="section-title">' + (r.Kind === 'Insert' ? 'Dòng ở nguồn — sẽ được thêm vào đích'
                : 'Dòng ở đích — không còn ở nguồn, sẽ bị xoá nếu chọn') + '</div><table class="kv"><tbody>'
                + Object.keys(vals).map(function (c) {
                    return '<tr><th>' + esc(c) + '</th><td' + (vals[c] == null ? ' class="null"' : '') + '>'
                        + esc(display(vals[c])) + '</td></tr>';
                }).join('') + '</tbody></table>';
        }

        openModal(KINDS[r.Kind].text, html, '', 'Giá trị dài được rút gọn khi hiển thị; khi ghi luôn dùng giá trị đầy đủ.', null);
    }

    // ------------------------------------------------------------------- apply

    function confirmApply() {
        var t = totals();
        if (!t.total) return;

        var rows = S.order.map(function (n) {
            var R = S.results[n];
            var sel = selection(R);
            if (!sel.total) return '';
            return '<tr><th>' + esc(n) + (sel.manual ? ' <span class="badge">chọn tay</span>' : '') + '</th><td>'
                + KIND_ORDER.filter(function (k) { return sel.count[k]; }).map(function (k) {
                    return '<span class="st st--' + KINDS[k].cls + '">' + KINDS[k].verb + ' ' + TP.num(sel.count[k]) + '</span>';
                }).join(' ') + '</td></tr>';
        }).join('');

        var src = $('srcModule');
        var html = '<div class="kvgrid">'
            + '<div class="kvi"><span class="kvk">Lấy từ (chỉ đọc)</span><span class="kvv">'
            + esc(src.options[src.selectedIndex].text) + '</span></div>'
            + '<div class="kvi"><span class="kvk">Ghi vào</span><span class="kvv"><b>' + esc(CFG.currentName) + '</b></span></div>'
            + '</div>'
            + '<div class="section-title">' + t.tables + ' bảng</div>'
            + '<table class="kv"><tbody>' + rows + '</tbody></table>'
            + '<p class="hint" style="margin:10px 0 0">Dữ liệu được đọc lại từ nguồn ngay lúc ghi, nên số dòng thực tế có thể '
            + 'lệch chút ít nếu nguồn vừa thay đổi. Chọn theo nhóm = ghi mọi dòng thuộc nhóm, kể cả dòng không được liệt kê.</p>';

        if (t.count.Delete) {
            html += '<p class="err" style="margin:12px 0 6px">Sẽ XOÁ ' + TP.num(t.count.Delete)
                + ' dòng ở ' + esc(CFG.currentName) + '. Nội dung từng dòng bị xoá vẫn được ghi vào nhật ký. '
                + 'Gõ <b>' + esc(CFG.currentModule) + '</b> để xác nhận:</p>'
                + '<input class="input" id="confirmKey" autocomplete="off" spellcheck="false" />';
        }

        openModal('Xác nhận kéo ' + TP.num(t.total) + ' dòng', html, 'Kéo dữ liệu',
            'Thêm mới bằng bulk insert, cập nhật/xoá từng dòng theo khóa chính; mỗi lô một transaction.',
            function () {
                if (t.count.Delete && ($('confirmKey').value.trim() !== CFG.currentModule)) {
                    TP.toast('Gõ đúng tên module đích để xác nhận xoá', 'err');
                    return;
                }
                closeModal();
                apply();
            });
    }

    async function apply() {
        var work = S.order.filter(function (n) { return selection(S.results[n]).total > 0; });
        if (!work.length) return;

        // Frozen now: what the user confirmed is what runs, even if they click around meanwhile.
        var plan = {};
        work.forEach(function (n) { plan[n] = selection(S.results[n]); });

        setBusy(true);
        note('');
        var runId = EMPTY_RUN;
        var failed = null;

        try {
            // Parents before children for inserts, children before parents for deletes — so every
            // commit along the way leaves the foreign keys satisfied.
            var ordered = await TP.post('/copy/order', { Tables: work });
            if (ordered.Warnings && ordered.Warnings.length) note(ordered.Warnings.join(' · '));
            var order = ordered.Tables;

            var phases = [
                { phase: 'Delete', tables: order.slice().reverse(), wants: function (s) { return s.count.Delete > 0; } },
                { phase: 'Upsert', tables: order, wants: function (s) { return s.count.Insert + s.count.Update > 0; } }
            ];

            for (var p = 0; p < phases.length && !failed && !S.stop; p++) {
                var ph = phases[p];
                for (var i = 0; i < ph.tables.length && !failed && !S.stop; i++) {
                    var name = ph.tables[i];
                    var sel = plan[name];
                    if (!sel || !ph.wants(sel)) continue;

                    var R = S.results[name];
                    R.status = 'applying';
                    R.applied = R.applied || { ins: 0, upd: 0, del: 0, skipped: 0 };
                    renderSummary();

                    try {
                        runId = await applyPhase(R, ph.phase, sel, runId);
                    } catch (e) {
                        failed = name + ': ' + e.message;
                        R.status = 'error';
                        R.error = e.message;
                    }
                    renderSummary();
                }
            }
        } catch (e) {
            failed = e.message;
        } finally {
            work.forEach(function (n) {
                var R = S.results[n];
                if (R.status === 'applying' || (R.applied && R.status !== 'error')) R.status = 'applied';
                // The comparison is out of date now; ticks would point at rows that no longer differ.
                R.picked = null;
            });
            setBusy(false);
            render();
        }

        var t = { ins: 0, upd: 0, del: 0 };
        work.forEach(function (n) {
            var a = S.results[n].applied;
            if (a) { t.ins += a.ins; t.upd += a.upd; t.del += a.del; }
        });
        var summary = 'Đã thêm ' + TP.num(t.ins) + ', cập nhật ' + TP.num(t.upd) + ', xoá ' + TP.num(t.del) + ' dòng.';

        if (failed) {
            note('Dừng vì lỗi — ' + failed + ' · ' + summary + ' Các lô đã ghi trước lỗi được giữ nguyên; sửa nguyên nhân rồi chạy lại.', 'err');
            TP.toast('Dừng vì lỗi', 'err');
        } else {
            note(summary + (S.stop ? ' Đã dừng theo yêu cầu.' : '')
                + ' Bấm "So sánh" lần nữa để kiểm tra phần còn lệch. Lịch sử ghi nằm ở Tra cứu → Lịch sử (loại copy).');
            TP.toast(summary, 'ok');
        }
    }

    async function applyPhase(R, phase, sel, runId) {
        var body = {
            SourceModule: $('srcModule').value,
            Table: R.name,
            RunId: runId,
            Phase: phase,
            Insert: phase === 'Upsert' && sel.count.Insert > 0,
            Update: phase === 'Upsert' && sel.count.Update > 0,
            Delete: phase === 'Delete' && sel.count.Delete > 0,
            Keys: null,
            Cursor: null
        };

        if (sel.manual) {
            body.Keys = phase === 'Delete' ? sel.keys.Delete : sel.keys.Insert.concat(sel.keys.Update);
            if (!body.Keys.length) return runId;
        }

        do {
            var data = await TP.post('/copy/apply', body);
            runId = data.RunId;
            body.RunId = runId;
            body.Cursor = data.Cursor;

            R.applied.ins += data.Inserted;
            R.applied.upd += data.Updated;
            R.applied.del += data.Deleted;
            R.applied.skipped += data.Skipped;
            renderSummary();
        } while (body.Cursor && !S.stop);

        return runId;
    }

    // ------------------------------------------------------------------ wiring

    function initSummary() {
        $('sumWrap').addEventListener('change', function (e) {
            var kind = e.target.dataset.opt;
            if (!kind) return;
            var R = S.results[e.target.closest('tr').dataset.t];
            R.opts[kind] = e.target.checked;

            // Hand-picked rows follow the kind switch too, so it never silently means nothing.
            if (R.picked) {
                R.rows.forEach(function (r, i) {
                    if (r.Kind !== kind) return;
                    if (e.target.checked) R.picked.add(i); else R.picked.delete(i);
                });
            }
            if (S.current === R.name) renderDetail();
            renderFoot();
        });

        $('sumWrap').addEventListener('click', function (e) {
            if (e.target.closest('.copy-opts')) return;
            var tr = e.target.closest('tr[data-t]');
            if (!tr) return;
            S.current = tr.dataset.t;
            S.hiddenKinds = new Set();
            render();
        });
    }

    function initDetail() {
        $('gridWrap').addEventListener('change', function (e) {
            var R = S.current ? S.results[S.current] : null;
            if (!R) return;

            if (e.target.id === 'ckAll') {
                goManual(R);
                R.rows.forEach(function (r, i) {
                    if (S.hiddenKinds.has(r.Kind)) return;
                    if (e.target.checked) R.picked.add(i); else R.picked.delete(i);
                });
                render();
                return;
            }

            if (e.target.dataset.pick !== undefined) {
                goManual(R);
                var i = +e.target.closest('tr').dataset.i;
                if (e.target.checked) R.picked.add(i); else R.picked.delete(i);
                renderSummary();
                renderDetail();
            }
        });

        $('gridWrap').addEventListener('click', function (e) {
            var cell = e.target.closest('[data-view]');
            if (!cell) return;
            var R = S.results[S.current];
            showRow(R, R.rows[+cell.closest('tr').dataset.i]);
        });

        $('detailHead').addEventListener('click', function (e) {
            var R = S.current ? S.results[S.current] : null;
            if (!R) return;

            if (e.target.closest('#btnByKind')) {
                R.picked = null;
                render();
                return;
            }
            var chip = e.target.closest('[data-kind]');
            if (!chip) return;
            var k = chip.dataset.kind;
            if (S.hiddenKinds.has(k)) S.hiddenKinds.delete(k); else S.hiddenKinds.add(k);
            renderDetail();
        });
    }

    function initModal() {
        $('modal').addEventListener('click', function (e) {
            if (e.target.closest('[data-close]') || e.target.id === 'modal') closeModal();
        });
        $('modalOk').addEventListener('click', function () { if (S.modalOk) S.modalOk(); });
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeModal(); });
    }

    function exportCsv() {
        var R = S.current ? S.results[S.current] : null;
        if (!R || !R.rows.length) { TP.toast('Chưa có dòng lệch nào', 'err'); return; }

        var cols = ['Loai', 'KhoaChinh'];
        if (R.label) cols.push(R.label);
        cols.push('ChiTiet');

        var rows = R.rows.map(function (r) {
            var o = { Loai: KINDS[r.Kind].text, KhoaChinh: keyText(r.Key) };
            if (R.label) o[R.label] = r.Label;
            o.ChiTiet = r.Kind === 'Update'
                ? (r.Changes || []).map(function (c) { return c.Column + ': ' + display(c.From) + ' -> ' + display(c.To); }).join('; ')
                : Object.keys(r.Values || {}).map(function (c) { return c + '=' + display(r.Values[c]); }).join('; ');
            return o;
        });

        TP.downloadCsv('sosanh-' + R.name + '.csv', cols, rows);
    }

    function init() {
        initModules();
        initList();
        initSummary();
        initDetail();
        initModal();

        $('srcModule').addEventListener('change', loadTables);
        $('btnCompare').addEventListener('click', function () {
            // In the list's order rather than click order, so a re-run reads the same way.
            compareAll(S.tables.map(function (t) { return t.Full; }).filter(function (n) { return S.chosen.has(n); }));
        });
        $('btnStop').addEventListener('click', function () {
            S.stop = true;
            TP.toast('Sẽ dừng sau bước đang chạy');
        });
        $('btnApply').addEventListener('click', confirmApply);
        $('btnCsv').addEventListener('click', exportCsv);

        window.addEventListener('beforeunload', function (e) {
            if (S.busy) { e.preventDefault(); e.returnValue = ''; }
        });

        render();
        loadTables();
    }

    document.addEventListener('DOMContentLoaded', init);
})();
