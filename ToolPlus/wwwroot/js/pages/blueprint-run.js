/* Blueprint run page: fill parameters -> run -> read the per-level results -> click ✎ to edit one row.
   Saving reuses /explorer/update: keyed by primary key, exactly one row, always audited. */
(function () {
    'use strict';

    var CFG = window.RUN_DATA || { id: '', allowEdit: false };

    var S = {
        nodes: [],        // results from the server
        byId: {},
        sel: {},          // nodeId -> selected row index (child tables filter down to this row)
        fold: {},         // nodeId -> true while collapsed; survives re-renders
        warnings: [],     // kept so a re-render (selecting a row) does not swallow the warnings
        edit: null        // { node, row, fields }
    };

    function $(id) { return document.getElementById(id); }
    var esc = function (s) { return TP.escapeHtml(s); };

    function display(v) {
        if (v === null || v === undefined) return 'NULL';
        if (v === true) return 'true';
        if (v === false) return 'false';
        return String(v);
    }

    function same(a, b) {
        var an = (a === null || a === undefined), bn = (b === null || b === undefined);
        if (an || bn) return an && bn;
        return String(a) === String(b);
    }

    // --------------------------------------------------------------------- run

    async function run(e) {
        if (e) e.preventDefault();

        var params = {};
        document.querySelectorAll('#runForm input[data-key]').forEach(function (i) {
            params[i.dataset.key] = i.value;
        });

        var box = $('runResults');
        box.innerHTML = '<div class="empty" style="padding:var(--s6)"><div class="spinner"></div>'
            + '<p>Running…</p></div>';
        $('btnRun').disabled = true;

        try {
            var res = await TP.post('/blueprint/run', { Id: CFG.id, Params: params });

            S.nodes = res.Nodes || [];
            S.byId = {};
            S.sel = {};
            S.nodes.forEach(function (n) {
                S.byId[n.NodeId] = n;
                (n.Rows || []).forEach(function (r, i) { r.__i = i; });
            });

            $('runStat').textContent = S.nodes.reduce(function (s, n) { return s + n.Rows.length; }, 0)
                + ' rows · ' + res.ElapsedMs + ' ms';
            S.warnings = res.Warnings || [];
            render();
        } catch (err) {
            $('runIndex').hidden = true;
            box.innerHTML = '<div class="empty" style="padding:var(--s6)">'
                + '<svg width="40" height="40" style="color:var(--danger)"><use href="#i-alert"/></svg>'
                + '<h3>Run failed</h3><p>' + esc(err.message) + '</p></div>';
            $('runStat').textContent = '';
        } finally {
            $('btnRun').disabled = false;
        }
    }

    // ------------------------------------------------ filtering by selected row

    /** A node's rows after filtering by whichever row is selected in its ancestors. */
    function visibleRows(n) {
        if (!n.ParentNodeId || !n.LinkFromColumn || !n.LinkToColumn) return n.Rows;

        var parent = S.byId[n.ParentNodeId];
        if (!parent) return n.Rows;

        var pRows = visibleRows(parent);
        var sel = S.sel[parent.NodeId];
        if (sel != null) {
            var pick = pRows.filter(function (r) { return r.__i === sel; });
            if (pick.length) pRows = pick;
        }

        var keys = {};
        pRows.forEach(function (r) {
            var v = r[n.LinkFromColumn];
            if (v !== null && v !== undefined) keys[String(v)] = 1;
        });

        return n.Rows.filter(function (r) {
            var v = r[n.LinkToColumn];
            return v !== null && v !== undefined && keys[String(v)];
        });
    }

    function depthOf(n) {
        var d = 0, cur = n;
        while (cur && cur.ParentNodeId) { d++; cur = S.byId[cur.ParentNodeId]; }
        return d;
    }

    // ----------------------------------------------------------------- render

    function render() {
        var box = $('runResults');

        if (!S.nodes.length) {
            box.innerHTML = '<div class="empty" style="padding:var(--s6)"><h3>No results</h3></div>';
            $('runIndex').hidden = true;
            return;
        }

        var html = S.warnings.map(function (w) {
            return '<div class="banner" style="border-radius:var(--r2);margin-bottom:var(--s2)">'
                + '<svg width="14" height="14"><use href="#i-alert"/></svg>' + esc(w) + '</div>';
        }).join('');

        // Parents always precede children, so iterating in order is already the display order.
        S.nodes.forEach(function (n) { html += nodeCard(n); });

        box.innerHTML = html;
        renderIndex();
    }

    /**
     * Table of contents: with 5-6 tables, scrolling to find one is tedious — especially right after
     * selecting a row in a parent, when a child's row count changes while it sits off-screen and
     * nobody sees it happen.
     */
    function renderIndex() {
        var bar = $('runIndex');

        bar.innerHTML = S.nodes.map(function (n) {
            var rows = visibleRows(n);
            var state = n.Error ? ' rchip--err' : (rows.length === 0 ? ' rchip--empty' : '');
            return '<button type="button" class="rchip' + state + '" data-go="' + n.NodeId + '">'
                + esc(n.Alias || n.Table)
                + '<b>' + (n.Error ? 'error' : rows.length) + '</b></button>';
        }).join('')
            + '<span class="spacer"></span>'
            + '<button type="button" class="btn btn--ghost" id="btnFoldAll"></button>';

        bar.hidden = false;
        syncFoldAll();
    }

    function allFolded() {
        return S.nodes.length > 0 && S.nodes.every(function (n) { return S.fold[n.NodeId]; });
    }

    function syncFoldAll() {
        var b = $('btnFoldAll');
        if (b) b.textContent = allFolded() ? 'Expand all' : 'Collapse all';
    }

    function cardOf(nodeId) {
        return $('runResults').querySelector('.rescard[data-node="' + nodeId + '"]');
    }

    function setFold(nodeId, folded) {
        S.fold[nodeId] = folded;
        var card = cardOf(nodeId);
        if (card) card.dataset.fold = folded ? 'true' : 'false';
        syncFoldAll();
    }

    function nodeCard(n) {
        var rows = visibleRows(n);
        var depth = depthOf(n);
        var title = n.Alias || n.Table;

        var head = '<div class="res-head" data-act="fold" data-node="' + n.NodeId + '" title="Click to collapse/expand this table">'
            + '<svg class="res-caret" width="12" height="12"><use href="#i-caret"/></svg>'
            + '<svg width="14" height="14"><use href="#' + (n.IsView ? 'i-eye' : 'i-table') + '"/></svg>'
            + '<strong>' + esc(title) + '</strong>'
            + '<code>' + esc(n.Table) + '</code>'
            + (n.ParentNodeId
                ? '<span class="badge">by ' + esc(n.LinkToColumn || '') + '</span>' : '')
            + '<span class="spacer"></span>'
            + (S.sel[n.NodeId] != null
                ? '<button type="button" class="btn btn--ghost" data-act="clear-sel" data-node="' + n.NodeId + '">Clear filter</button>' : '')
            + '<span class="hint">' + rows.length
            + (rows.length !== n.Rows.length ? '/' + n.Rows.length : '') + ' rows'
            + (n.Truncated ? ' · truncated' : '')
            + (n.ElapsedMs ? ' · ' + n.ElapsedMs + 'ms' : '') + '</span>'
            + (n.Sql ? '<button type="button" class="btn btn--ghost" data-act="sql" data-node="' + n.NodeId + '">SQL</button>' : '')
            + '</div>';

        var body;
        if (n.Error) {
            body = '<div class="res-msg res-msg--err">'
                + '<svg width="14" height="14"><use href="#i-alert"/></svg>' + esc(n.Error) + '</div>';
        } else if (rows.length === 0) {
            body = '<div class="res-msg">No rows'
                + (n.ParentNodeId ? ' matching the parent' : '') + '.</div>';
        } else if (rows.length === 1) {
            body = kvHtml(n, rows[0]);
        } else {
            body = tableHtml(n, rows);
        }

        return '<section class="rescard" data-node="' + n.NodeId + '"'
            + ' data-fold="' + (S.fold[n.NodeId] ? 'true' : 'false') + '"'
            + ' style="margin-left:' + (depth * 22) + 'px">'
            + head + body + '</section>';
    }

    /**
     * A single-row table is shown as a two-column record instead. Left as a wide table, each child
     * would spend its full width on one row and every column would need horizontal scrolling to
     * read — and a blueprint's child tables almost always return exactly one row.
     */
    function kvHtml(n, row) {
        var body = '<div class="kvgrid">' + n.Shown.map(function (c) {
            var t = display(row[c]);
            return '<div class="kvi"><span class="kvk" title="' + esc(c) + '">' + esc(c) + '</span>'
                + '<span class="kvv' + (row[c] == null ? ' null' : '') + '" title="' + esc(t.slice(0, 300)) + '">'
                + esc(t) + '</span></div>';
        }).join('') + '</div>';

        return '<div class="res-body res-body--kv">' + body
            + (n.CanEdit
                ? '<div style="margin-top:var(--s3)"><button type="button" class="btn" data-act="edit" '
                  + 'data-node="' + n.NodeId + '" data-row="' + row.__i + '">'
                  + '<svg width="13" height="13"><use href="#i-save"/></svg> Edit this record</button></div>'
                : '')
            + '</div>';
    }

    function tableHtml(n, rows) {
        var h = ['<div class="res-body"><table class="grid"><thead><tr>'];
        if (n.CanEdit) h.push('<th class="rowno"></th>');
        h.push('<th class="rowno">#</th>');
        n.Shown.forEach(function (c) { h.push('<th style="cursor:default"><div class="th"><span>' + esc(c) + '</span></div></th>'); });
        h.push('</tr></thead><tbody>');

        rows.forEach(function (r, i) {
            var picked = S.sel[n.NodeId] === r.__i;
            h.push('<tr data-row="' + r.__i + '" data-node="' + n.NodeId + '"' + (picked ? ' data-picked="true"' : '') + '>');
            if (n.CanEdit) {
                h.push('<td class="rowno"><button type="button" class="icon-btn res-edit" data-act="edit" '
                    + 'data-node="' + n.NodeId + '" data-row="' + r.__i + '" title="Edit this row">'
                    + '<svg width="13" height="13"><use href="#i-save"/></svg></button></td>');
            }
            h.push('<td class="rowno">' + (i + 1) + '</td>');
            n.Shown.forEach(function (c) {
                var t = display(r[c]);
                h.push('<td' + (r[c] == null ? ' class="null"' : '') + ' title="' + esc(t.slice(0, 300)) + '">'
                    + esc(t) + '</td>');
            });
            h.push('</tr>');
        });

        return h.join('') + '</tbody></table></div>';
    }

    // ------------------------------------------------------------- record edit

    function openEdit(nodeId, rowIndex) {
        var n = S.byId[nodeId];
        var row = n.Rows[rowIndex];
        if (!n || !row) return;

        var pks = n.Columns.filter(function (c) { return c.IsPrimaryKey; });
        if (!pks.length) { TP.toast('Table has no primary key and cannot be edited', 'err'); return; }

        S.edit = { node: n, row: row };

        var keyChips = pks.map(function (c) {
            return '<span class="keychip">' + esc(c.Name) + ' = ' + esc(display(row[c.Name])) + '</span>';
        }).join(' ');

        var editable = n.Columns.filter(function (c) {
            return !c.IsPrimaryKey && !c.IsReadOnly && !c.IsSkipped;
        });

        var fields = editable.map(function (c) {
            var v = row[c.Name];
            var isNull = (v === null || v === undefined);
            var long = c.IsLong || c.MaxLength === -1 || c.MaxLength > 200;

            return '<div class="editfield" data-col="' + esc(c.Name) + '">'
                + '<label class="label">' + esc(c.Name)
                + ' <span class="hint">' + esc(c.DataType) + (c.IsNullable ? '' : ' · NOT NULL') + '</span></label>'
                + '<div class="editrow">'
                + (long
                    ? '<textarea class="textarea" data-in rows="3"' + (isNull ? ' disabled' : '') + '>' + esc(isNull ? '' : v) + '</textarea>'
                    : '<input class="input" data-in value="' + esc(isNull ? '' : display(v)) + '"' + (isNull ? ' disabled' : '') + ' />')
                + (c.IsNullable
                    ? '<label class="check" title="Set value to NULL"><input type="checkbox" data-null' + (isNull ? ' checked' : '') + ' /> NULL</label>'
                    : '')
                + '</div></div>';
        }).join('');

        $('editTitle').textContent = 'Edit ' + (n.Alias || n.Table);
        $('editBody').innerHTML =
            '<div style="display:flex;gap:6px;flex-wrap:wrap;margin-bottom:var(--s3)">' + keyChips + '</div>'
            + (fields || '<div class="res-msg">No editable columns selected in the blueprint. Open "Edit blueprint" and tick some columns.</div>');
        $('editHint').textContent = 'Only columns selected in the blueprint can be edited (' + editable.length + ' column(s)).';
        $('editSave').hidden = editable.length === 0;
        $('editModal').dataset.open = 'true';
    }

    /** Reads the edit form and returns only the columns whose value actually changed. */
    function collectChanges() {
        var n = S.edit.node, row = S.edit.row;
        var changes = [];

        $('editBody').querySelectorAll('.editfield').forEach(function (f) {
            var col = f.dataset.col;
            var nullChk = f.querySelector('[data-null]');
            var input = f.querySelector('[data-in]');

            var next = (nullChk && nullChk.checked) ? null : input.value;
            if (same(next, row[col])) return;

            changes.push({ Column: col, From: row[col], To: next });
        });

        return changes;
    }

    function openDiff() {
        var changes = collectChanges();
        if (!changes.length) { TP.toast('Nothing changed', 'err'); return; }

        S.edit.changes = changes;
        $('diffBody').innerHTML = '<table class="diff"><thead><tr><th>Column</th><th>Before</th><th>After</th></tr></thead><tbody>'
            + changes.map(function (c) {
                return '<tr><td class="col">' + esc(c.Column) + '</td>'
                    + '<td class="from">' + esc(display(c.From)) + '</td>'
                    + '<td class="to">' + esc(display(c.To)) + '</td></tr>';
            }).join('') + '</tbody></table>';

        $('editModal').dataset.open = 'false';
        $('diffModal').dataset.open = 'true';
    }

    async function doSave() {
        var n = S.edit.node, row = S.edit.row, changes = S.edit.changes;

        var key = {};
        n.Columns.filter(function (c) { return c.IsPrimaryKey; }).forEach(function (c) {
            var v = row[c.Name];
            key[c.Name] = (v === null || v === undefined) ? null : String(v);
        });

        var payload = {};
        changes.forEach(function (c) { payload[c.Column] = c.To; });

        $('diffOk').disabled = true;
        try {
            await TP.post('/explorer/update', { Table: n.Table, Key: key, Changes: payload });

            changes.forEach(function (c) { row[c.Column] = c.To; });
            $('diffModal').dataset.open = 'false';
            S.edit = null;
            render();
            TP.toast('Updated ' + changes.length + ' column(s)', 'ok');
        } catch (err) {
            TP.toast(err.message, 'err');
        } finally {
            $('diffOk').disabled = false;
        }
    }

    // -------------------------------------------------------------------- init

    document.addEventListener('DOMContentLoaded', function () {
        $('runForm').addEventListener('submit', run);

        $('runResults').addEventListener('click', function (e) {
            var btn = e.target.closest('[data-act]');
            if (btn) {
                var act = btn.dataset.act;
                if (act === 'edit') { openEdit(btn.dataset.node, +btn.dataset.row); return; }
                if (act === 'fold') { setFold(btn.dataset.node, !S.fold[btn.dataset.node]); return; }
                if (act === 'clear-sel') { delete S.sel[btn.dataset.node]; render(); return; }
                if (act === 'sql') {
                    var n = S.byId[btn.dataset.node];
                    TP.toast(n.Sql.replace(/\s+/g, ' ').slice(0, 400), 'ok');
                    return;
                }
            }

            // Click a row to filter the child tables down to that row.
            var tr = e.target.closest('tr[data-row]');
            if (!tr) return;
            var nodeId = tr.dataset.node;
            var idx = +tr.dataset.row;
            if (S.sel[nodeId] === idx) delete S.sel[nodeId];
            else S.sel[nodeId] = idx;
            render();
        });

        // TOC: a chip jumps to its table (expanding it if collapsed); the right button folds/unfolds all.
        $('runIndex').addEventListener('click', function (e) {
            if (e.target.closest('#btnFoldAll')) {
                var fold = !allFolded();
                S.nodes.forEach(function (n) { setFold(n.NodeId, fold); });
                return;
            }

            var chip = e.target.closest('[data-go]');
            if (!chip) return;

            var card = cardOf(chip.dataset.go);
            if (!card) return;

            setFold(chip.dataset.go, false);
            card.scrollIntoView({ behavior: 'smooth', block: 'start' });

            card.classList.remove('is-flash');
            void card.offsetWidth;          // force the animation to replay when the same chip is clicked twice
            card.classList.add('is-flash');
        });

        $('editSave').addEventListener('click', openDiff);
        $('diffOk').addEventListener('click', doSave);

        // Enable/disable the input based on its NULL checkbox.
        $('editBody').addEventListener('change', function (e) {
            if (!e.target.matches('[data-null]')) return;
            var input = e.target.closest('.editfield').querySelector('[data-in]');
            input.disabled = e.target.checked;
            if (!e.target.checked) input.focus();
        });

        [$('editModal'), $('diffModal')].forEach(function (m) {
            m.addEventListener('click', function (e) {
                if (e.target === m || e.target.closest('[data-close]')) m.dataset.open = 'false';
            });
        });

        document.addEventListener('keydown', function (e) {
            if (e.key !== 'Escape') return;
            $('editModal').dataset.open = 'false';
            $('diffModal').dataset.open = 'false';
        });

        // Every parameter already filled in (defaults) -> run straight away and save a click.
        var inputs = document.querySelectorAll('#runForm input[data-key]');
        var ready = inputs.length === 0
            || Array.prototype.every.call(inputs, function (i) { return i.value.trim() !== ''; });
        if (ready) run();
        else if (inputs.length) inputs[0].focus();
    });
})();
