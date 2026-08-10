/* Blueprint canvas — attach tables, link field to field, save and reuse.
   Vanilla JS: nodes are absolutely-positioned divs, links are SVG paths, all inside
   a .canvas-content layer that is transformed for pan/zoom so link coordinates
   do not need to be adjusted manually for pan/zoom.

   Key constraint: each table accepts EXACTLY 1 inbound link. A tree graph is required
   so the runner can cascade (parent fetches values -> child filters by those values)
   and each row keeps its own primary key so editing works. */
(function () {
    'use strict';

    var D = window.BP_DATA || { id: '', graph: {} };

    var MAX_NODES = 12;
    var PARAM_TYPES = [
        { v: 'text', t: 'text' }, { v: 'guid', t: 'guid' }, { v: 'int', t: 'number' },
        { v: 'date', t: 'date' }, { v: 'month', t: 'month' }
    ];
    var OPS = [
        { v: '=', t: '= equals' }, { v: '<>', t: '≠ not equal' },
        { v: 'like', t: 'contains' }, { v: 'startswith', t: 'starts with' },
        { v: '>', t: '> greater than' }, { v: '>=', t: '≥ from' },
        { v: '<', t: '< less than' }, { v: '<=', t: '≤ up to' },
        { v: 'in', t: 'in list' },
        { v: 'null', t: 'empty (NULL)' }, { v: 'notnull', t: 'has value' }
    ];

    /** Operators that require no value field. */
    function noValueOp(op) { return op === 'null' || op === 'notnull'; }

    /** Blueprint model. Property names are PascalCase to match the server-side DTO. */
    var G = {
        Params: [], Nodes: [], Edges: [],
        Zoom: 1, PanX: 0, PanY: 0, ParamX: 40, ParamY: 40
    };

    var meta = {};            // "dbo.hr_NhanVien" -> { Name, IsView, Columns[] }
    var tables = [];
    var selected = null;      // id of the currently selected node
    var seq = 1;
    var dirty = false;

    var canvas, content, edgesSvg, emptyBox, zoomLabel;
    var esc = function (s) { return TP.escapeHtml(s); };

    function $(id) { return document.getElementById(id); }

    // -------------------------------------------------------------- model utilities

    function nodeById(id) { return G.Nodes.filter(function (n) { return n.Id === id; })[0]; }
    function nodeEl(id) { return content.querySelector('.bp-node[data-id="' + id + '"]'); }
    function cols(node) { return (meta[node.Table] || { Columns: [] }).Columns; }

    function incomingEdge(nodeId) {
        return G.Edges.filter(function (e) { return e.ToNode === nodeId; })[0];
    }

    /** Walk up to the root to detect cycles — each node has at most one parent so this is linear. */
    function isAncestor(maybeAncestorId, nodeId) {
        var seen = {};
        var cur = nodeId;
        while (cur && !seen[cur]) {
            seen[cur] = true;
            if (cur === maybeAncestorId) return true;
            var e = incomingEdge(cur);
            cur = e && e.FromNode ? e.FromNode : null;
        }
        return false;
    }

    function markDirty() { dirty = true; updateStat(); }

    function updateStat() {
        $('bpStat').textContent = G.Nodes.length + ' table(s) · ' + G.Edges.length + ' link(s)'
            + (G.Params.length ? ' · ' + G.Params.length + ' param(s)' : '')
            + (dirty ? ' · unsaved' : '');
        emptyBox.hidden = G.Nodes.length > 0 || G.Params.length > 0;
    }

    // ------------------------------------------------------------------ palette

    async function loadTables() {
        try {
            tables = await TP.get('/blueprint/tables');
            renderTableList();
        } catch (e) {
            $('tableList').innerHTML = '<div class="empty"><h3>Could not load table list</h3><p>'
                + esc(e.message) + '</p></div>';
            $('tableCount').textContent = 'error';
        }
    }

    function renderTableList() {
        var kw = $('tableFilter').value.trim().toLowerCase();
        var list = kw ? tables.filter(function (t) { return t.Full.toLowerCase().indexOf(kw) >= 0; }) : tables;

        $('tableCount').textContent = list.length + '/' + tables.length + ' table(s)';
        $('tableList').innerHTML = list.slice(0, 400).map(function (t) {
            var used = G.Nodes.some(function (n) { return n.Table === t.Full; });
            return '<button type="button" class="table-item" data-full="' + esc(t.Full) + '" title="' + esc(t.Full) + '">'
                + '<span>' + esc(t.Name) + '</span>'
                + (t.IsView ? '<span class="badge badge--view">view</span>' : '')
                + (used ? '<span class="badge badge--pk">added</span>' : '')
                + '</button>';
        }).join('');
    }

    async function ensureMeta(full) {
        if (meta[full]) return meta[full];
        var res = await TP.get('/blueprint/columns?table=' + encodeURIComponent(full));
        meta[res.Table] = { Name: res.Name, IsView: res.IsView, Columns: res.Columns };
        return meta[res.Table];
    }

    /** Columns shown by default when a table is added: primary key + a few short recognisable columns. */
    function defaultColumns(full) {
        var all = meta[full].Columns;
        var picked = all.filter(function (c) { return c.IsPrimaryKey; }).map(function (c) { return c.Name; });

        all.forEach(function (c) {
            if (picked.length >= 6) return;
            if (c.IsSkipped || c.IsLong || c.IsPrimaryKey) return;
            if (/^(UserCreated|UserModified|UserDeleted|CreatedDate|ModifiedDate|DeletedDate)$/i.test(c.Name)) return;
            picked.push(c.Name);
        });
        return picked;
    }

    async function addNode(full) {
        if (G.Nodes.length >= MAX_NODES) {
            TP.toast('A blueprint can have at most ' + MAX_NODES + ' tables — more would be very slow to run', 'err');
            return;
        }

        try {
            await ensureMeta(full);
        } catch (e) {
            TP.toast(e.message, 'err');
            return;
        }

        var last = G.Nodes[G.Nodes.length - 1];
        var node = {
            Id: 'n' + (seq++),
            Table: full,
            Alias: meta[full].Name,
            X: last ? last.X + 360 : G.ParamX + 400,
            Y: last ? last.Y + (G.Nodes.length % 2 ? 60 : -40) : G.ParamY,
            Columns: defaultColumns(full),
            Filters: [],
            Limit: 50,
            SortColumn: null,
            SortDesc: false
        };
        if (node.X > 2400) { node.X = G.ParamX + 400; node.Y = (last ? last.Y : 0) + 420; }
        if (node.Y < 20) node.Y = 20;

        G.Nodes.push(node);
        renderAll();
        markDirty();
        await suggestFor(node);
    }

    function removeNode(id) {
        G.Nodes = G.Nodes.filter(function (n) { return n.Id !== id; });
        G.Edges = G.Edges.filter(function (e) { return e.FromNode !== id && e.ToNode !== id; });
        if (selected === id) closeProps();
        renderAll();
        markDirty();
    }

    // ------------------------------------------------------------ link suggestions

    async function suggestFor(newNode) {
        var others = G.Nodes.filter(function (n) { return n.Id !== newNode.Id; }).map(function (n) { return n.Table; });
        if (others.length === 0) return;

        var list;
        try {
            list = await TP.get('/blueprint/suggest?table=' + encodeURIComponent(newNode.Table)
                + '&others=' + encodeURIComponent(others.join(',')));
        } catch (e) {
            return;
        }
        if (!list || !list.length) return;

        // The new node is the filtered side: data flows from the existing table to it.
        for (var i = 0; i < list.length; i++) {
            var s = list[i];
            var newIsFrom = s.FromTable.toLowerCase() === newNode.Table.toLowerCase();
            var newIsTo = s.ToTable.toLowerCase() === newNode.Table.toLowerCase();
            if (!newIsFrom && !newIsTo) continue;

            var otherTable = newIsFrom ? s.ToTable : s.FromTable;
            var otherCol = newIsFrom ? s.ToColumn : s.FromColumn;
            var newCol = newIsFrom ? s.FromColumn : s.ToColumn;

            var source = G.Nodes.filter(function (n) {
                return n.Id !== newNode.Id && n.Table.toLowerCase() === otherTable.toLowerCase();
            })[0];
            if (!source) continue;

            if (addEdge({ FromNode: source.Id, FromColumn: otherCol, ToNode: newNode.Id, ToColumn: newCol }, true)) {
                ensureShown(source, otherCol);
                ensureShown(newNode, newCol);
                renderAll();
                TP.toast('Auto-linked ' + meta[source.Table].Name + '.' + otherCol + ' → '
                    + meta[newNode.Table].Name + '.' + newCol
                    + (s.Reason === 'fk' ? ' (foreign key)' : s.Reason === 'hint' ? ' (previously linked)' : ' (by name)')
                    + '. Click the link to remove it if wrong.', 'ok');
                return;
            }
        }
    }

    function ensureShown(node, colName) {
        if (node.Columns.indexOf(colName) < 0) node.Columns.push(colName);
    }

    // ---------------------------------------------------------------- parameters

    function addParam() {
        var i = G.Params.length + 1;
        var key = 'thamSo' + i;
        while (G.Params.some(function (p) { return p.Key === key; })) { i++; key = 'thamSo' + i; }

        G.Params.push({ Key: key, Label: '', Type: 'text', Required: true, Default: '' });
        renderAll();
        markDirty();
    }

    function removeParam(key) {
        G.Params = G.Params.filter(function (p) { return p.Key !== key; });
        G.Edges = G.Edges.filter(function (e) { return e.FromParam !== key; });
        renderAll();
        markDirty();
    }

    function renameParam(oldKey, newKey) {
        newKey = (newKey || '').trim();
        if (!newKey) return false;
        if (newKey !== oldKey && G.Params.some(function (p) { return p.Key === newKey; })) {
            TP.toast('A parameter named "' + newKey + '" already exists', 'err');
            return false;
        }

        G.Params.forEach(function (p) { if (p.Key === oldKey) p.Key = newKey; });
        G.Edges.forEach(function (e) { if (e.FromParam === oldKey) e.FromParam = newKey; });
        markDirty();
        return true;
    }

    // ------------------------------------------------------------------ links

    /** Returns true if the link is valid. quiet = true suppresses the error toast (used for suggestions). */
    function addEdge(edge, quiet) {
        function fail(msg) { if (!quiet) TP.toast(msg, 'err'); return false; }

        if (!edge.ToNode || !edge.ToColumn) return fail('A link must end at a table column');
        if (!edge.FromParam && (!edge.FromNode || !edge.FromColumn)) return fail('A link must start from a parameter or a column');
        if (edge.FromNode === edge.ToNode) return fail('Cannot link a table to itself');

        if (incomingEdge(edge.ToNode)) {
            return fail('Each table accepts only 1 inbound link (blueprint must be a tree). Remove the existing link first.');
        }
        if (edge.FromNode && isAncestor(edge.ToNode, edge.FromNode)) {
            return fail('This link would create a cycle');
        }

        // Data types on both ends must match, otherwise the runner will fail with a cast error.
        if (edge.FromNode) {
            var fromNode = nodeById(edge.FromNode), toNode = nodeById(edge.ToNode);
            var a = colInfo(fromNode, edge.FromColumn), b = colInfo(toNode, edge.ToColumn);
            if (a && b && a.DataType.toLowerCase() !== b.DataType.toLowerCase()) {
                return fail('Type mismatch: ' + edge.FromColumn + ' (' + a.DataType + ') vs '
                    + edge.ToColumn + ' (' + b.DataType + ')');
            }
        }

        edge.Id = 'e' + (seq++);
        G.Edges.push(edge);
        markDirty();
        return true;
    }

    function colInfo(node, name) {
        if (!node) return null;
        return cols(node).filter(function (c) { return c.Name === name; })[0] || null;
    }

    function removeEdge(id) {
        G.Edges = G.Edges.filter(function (e) { return e.Id !== id; });
        refreshLinked();
        markDirty();
    }

    // -------------------------------------------------------------------- render

    function renderAll() {
        // Remove the old node DOM element, keep the SVG layer.
        Array.prototype.slice.call(content.querySelectorAll('.bp-node, .bp-param-node'))
            .forEach(function (el) { el.remove(); });

        content.appendChild(buildParamNode());
        G.Nodes.forEach(function (n) { content.appendChild(buildNode(n)); });

        applyTransform();
        refreshLinked();
        renderTableList();
        updateStat();
    }

    function buildParamNode() {
        var el = document.createElement('div');
        el.className = 'bp-param-node';
        el.dataset.param = 'root';
        el.style.left = G.ParamX + 'px';
        el.style.top = G.ParamY + 'px';

        el.innerHTML =
            '<div class="bp-head" data-drag="param">'
            + '<svg width="14" height="14"><use href="#i-key"/></svg>'
            + '<span style="flex:1 1 auto;font-weight:600;font-size:12px">PARAMETERS</span>'
            + '<button type="button" class="bp-btn" data-act="add-param" title="Add parameter">'
            + '<svg width="13" height="13"><use href="#i-plus"/></svg></button>'
            + '</div>'
            + '<div class="bp-params">'
            + (G.Params.length ? G.Params.map(paramRow).join('')
                : '<div style="padding:8px var(--s2);font-size:11.5px;color:var(--fg-subtle)">'
                + 'No parameters. Add one then drag the right-hand dot to the column you want to filter by.</div>')
            + '</div>';
        return el;
    }

    /** The default value input type must match the parameter type, otherwise the browser silently discards the value. */
    function inputTypeFor(t) {
        return t === 'date' ? 'date' : t === 'month' ? 'month' : t === 'int' ? 'number' : 'text';
    }

    /** Check whether the existing default value is still valid for the newly selected type. */
    function defaultFits(p) {
        var v = (p.Default || '').trim();
        if (!v) return true;
        if (p.Type === 'date') return /^\d{4}-\d{2}-\d{2}$/.test(v);
        if (p.Type === 'month') return /^\d{4}-\d{2}$/.test(v);
        if (p.Type === 'int') return /^-?\d+$/.test(v);
        if (p.Type === 'guid') return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
        return true;
    }

    function paramRow(p) {
        return '<div class="bp-param" data-key="' + esc(p.Key) + '">'
            + '<input class="bp-mini k" value="' + esc(p.Key) + '" data-f="key" title="Variable name, used in filter conditions as {name}" />'
            + '<select class="bp-mini t" data-f="type">'
            + PARAM_TYPES.map(function (t) {
                return '<option value="' + t.v + '"' + (p.Type === t.v ? ' selected' : '') + '>' + t.t + '</option>';
            }).join('')
            + '</select>'
            + '<label class="bp-req" title="Unchecked = optional parameter: leaving it blank at run time skips any condition using it, no error.">'
            + '<input type="checkbox" data-f="required"' + (p.Required ? ' checked' : '') + ' />required</label>'
            + '<button type="button" class="bp-btn bp-btn--danger" data-act="del-param" title="Remove parameter">'
            + '<svg width="12" height="12"><use href="#i-x"/></svg></button>'
            + '<span class="bp-param-br"></span>'
            + '<input class="bp-mini l" value="' + esc(p.Label || '') + '" data-f="label" placeholder="display label"'
            + ' title="Label shown to users on the run page — NOT the value." />'
            + '<input class="bp-mini d" type="' + inputTypeFor(p.Type) + '" value="' + esc(p.Default || '') + '"'
            + ' data-f="default" placeholder="default value"'
            + ' title="Pre-filled value on the run page. If all defaults are filled the blueprint runs automatically on load." />'
            + '<span class="bp-handle bp-handle--out" data-h="out" data-param="' + esc(p.Key) + '" title="Drag to the column to filter by"></span>'
            + '</div>';
    }

    function buildNode(node) {
        var m = meta[node.Table] || { Name: node.Table, Columns: [], IsView: false };

        var el = document.createElement('div');
        el.className = 'bp-node';
        el.dataset.id = node.Id;
        el.style.left = node.X + 'px';
        el.style.top = node.Y + 'px';

        el.innerHTML =
            '<div class="bp-head" data-drag="node">'
            + '<svg width="14" height="14"><use href="#' + (m.IsView ? 'i-eye' : 'i-table') + '"/></svg>'
            + '<input class="bp-alias" value="' + esc(node.Alias || m.Name) + '" data-f="alias" title="Name shown in results" />'
            + (m.IsView ? '<span class="badge badge--view">view</span>' : '')
            + '<button type="button" class="bp-btn" data-act="props" title="Conditions &amp; properties">'
            + '<svg width="13" height="13"><use href="#i-filter"/></svg></button>'
            + '<button type="button" class="bp-btn bp-btn--danger" data-act="del-node" title="Remove table from canvas">'
            + '<svg width="13" height="13"><use href="#i-x"/></svg></button>'
            + '</div>'
            + '<div class="bp-sub">'
            + '<code>' + esc(node.Table) + '</code>'
            + '<input class="bp-mini" data-f="colfilter" placeholder="filter columns" style="width:88px" value="' + esc(node._filter || '') + '" />'
            + '</div>'
            + '<div class="bp-cols">' + colRows(node) + '</div>'
            + '<div class="bp-foot">'
            + '<span>' + node.Columns.length + '/' + m.Columns.length + ' col(s)</span>'
            + '<span class="grow"></span>'
            + (node.Filters.length ? '<span class="badge badge--pk">' + node.Filters.length + ' cond</span>' : '')
            + '<span>max rows</span>'
            + '<input class="bp-mini" data-f="limit" value="' + node.Limit + '" style="width:46px;text-align:center" />'
            + '</div>';

        if (selected === node.Id) el.dataset.selected = 'true';
        return el;
    }

    function colRows(node) {
        var kw = (node._filter || '').trim().toLowerCase();
        var list = cols(node);
        if (kw) list = list.filter(function (c) { return c.Name.toLowerCase().indexOf(kw) >= 0; });

        return list.map(function (c) {
            var shown = node.Columns.indexOf(c.Name) >= 0;
            return '<div class="bp-col" data-col="' + esc(c.Name) + '">'
                + '<span class="bp-handle bp-handle--in" data-h="in" title="Receives a value to filter by"></span>'
                + '<input type="checkbox" data-f="show"' + (shown ? ' checked' : '')
                + ' title="Show this column in results" />'
                + '<span class="bp-col-name">' + esc(c.Name) + '</span>'
                + (c.IsPrimaryKey ? '<span class="badge badge--pk">pk</span>' : '')
                + '<span class="bp-col-type">' + esc(c.DataType) + '</span>'
                + '<span class="bp-handle bp-handle--out" data-h="out" title="Use this column\u2019s value to filter another table"></span>'
                + '</div>';
        }).join('');
    }

    /** Highlight columns that are linked and redraw all edges. */
    function refreshLinked() {
        content.querySelectorAll('.bp-col').forEach(function (row) { delete row.dataset.linked; });

        G.Edges.forEach(function (e) {
            if (e.FromNode) mark(e.FromNode, e.FromColumn);
            mark(e.ToNode, e.ToColumn);
        });

        function mark(nodeId, col) {
            var el = nodeEl(nodeId);
            if (!el) return;
            var row = el.querySelector('.bp-col[data-col="' + cssEscape(col) + '"]');
            if (row) row.dataset.linked = 'true';
        }

        drawEdges();
    }

    function cssEscape(s) {
        return window.CSS && CSS.escape ? CSS.escape(s) : String(s).replace(/["\\]/g, '\\$&');
    }

    // ------------------------------------------------------------ draw links

    function anchor(nodeId, paramKey, col, side) {
        var host = paramKey != null
            ? content.querySelector('.bp-param-node')
            : nodeEl(nodeId);
        if (!host) return null;

        var row = paramKey != null
            ? host.querySelector('.bp-param[data-key="' + cssEscape(paramKey) + '"]')
            : host.querySelector('.bp-col[data-col="' + cssEscape(col) + '"]');

        var body = host.querySelector('.bp-cols') || host.querySelector('.bp-params');
        var hostRect = host.getBoundingClientRect();
        var cRect = content.getBoundingClientRect();
        var z = G.Zoom || 1;

        var y;
        if (row && row.offsetParent !== null) {
            var r = row.getBoundingClientRect();
            y = r.top + r.height / 2;
            if (body) {
                // Column scrolled out of view: clamp the link endpoint to the edge of the column frame.
                var b = body.getBoundingClientRect();
                y = Math.min(Math.max(y, b.top + 3), b.bottom - 3);
            }
        } else {
            y = hostRect.top + 16;   // column filtered away -> anchor to the node header
        }

        return {
            x: ((side === 'out' ? hostRect.right : hostRect.left) - cRect.left) / z,
            y: (y - cRect.top) / z
        };
    }

    function bez(a, b) {
        var dx = Math.max(48, Math.abs(b.x - a.x) * 0.45);
        return 'M' + a.x + ' ' + a.y
            + ' C' + (a.x + dx) + ' ' + a.y + ' ' + (b.x - dx) + ' ' + b.y + ' ' + b.x + ' ' + b.y;
    }

    function drawEdges(temp) {
        var out = [];

        G.Edges.forEach(function (e) {
            var a = e.FromParam
                ? anchor(null, e.FromParam, null, 'out')
                : anchor(e.FromNode, null, e.FromColumn, 'out');
            var b = anchor(e.ToNode, null, e.ToColumn, 'in');
            if (!a || !b) return;

            var d = bez(a, b);
            var param = e.FromParam ? ' edge-g--param' : '';
            out.push('<g class="edge-g' + param + '" data-id="' + e.Id + '">'
                + '<path class="edge-hit" d="' + d + '"></path>'
                + '<path class="edge' + (e.FromParam ? ' edge--param' : '') + '" d="' + d + '"></path>'
                + '<circle class="edge-dot" cx="' + b.x + '" cy="' + b.y + '" r="3.5"></circle>'
                + '</g>');
        });

        if (temp) out.push('<path class="edge-temp" d="' + temp + '"></path>');
        edgesSvg.innerHTML = out.join('');
    }

    // ------------------------------------------------------------- pan / zoom

    function applyTransform() {
        content.style.transform = 'translate(' + G.PanX + 'px,' + G.PanY + 'px) scale(' + G.Zoom + ')';
        zoomLabel.textContent = Math.round(G.Zoom * 100) + '%';
    }

    function setZoom(next, cx, cy) {
        next = Math.min(1.6, Math.max(0.35, next));
        if (Math.abs(next - G.Zoom) < 0.001) return;

        if (cx != null) {
            // Keep the point under the cursor stationary while zooming.
            G.PanX = cx - (cx - G.PanX) * (next / G.Zoom);
            G.PanY = cy - (cy - G.PanY) * (next / G.Zoom);
        }
        G.Zoom = next;
        applyTransform();
        drawEdges();
    }

    function fitView() {
        var boxes = [{ x: G.ParamX, y: G.ParamY, w: 344, h: 160 }].concat(
            G.Nodes.map(function (n) { return { x: n.X, y: n.Y, w: 320, h: 320 }; }));

        var minX = Math.min.apply(null, boxes.map(function (b) { return b.x; })) - 40;
        var minY = Math.min.apply(null, boxes.map(function (b) { return b.y; })) - 40;
        var maxX = Math.max.apply(null, boxes.map(function (b) { return b.x + b.w; })) + 40;
        var maxY = Math.max.apply(null, boxes.map(function (b) { return b.y + b.h; })) + 40;

        var rect = canvas.getBoundingClientRect();
        var z = Math.min(1.2, Math.min(rect.width / (maxX - minX), rect.height / (maxY - minY)));
        // Unlike setZoom() (manual zoom, floored at 0.35 for legibility), fitting a large
        // blueprint must be allowed to go below that floor — otherwise nodes past a certain
        // count no longer fit and stay clipped by .canvas{overflow:hidden} no matter what.
        G.Zoom = Math.max(0.1, z);
        G.PanX = -minX * G.Zoom;
        G.PanY = -minY * G.Zoom;
        applyTransform();
        drawEdges();
    }

    // ----------------------------------------------------------- mouse drag & link

    function initPointer() {
        var drag = null;     // { kind:'node'|'param'|'pan'|'edge', ... }

        canvas.addEventListener('mousedown', function (e) {
            // 1. Start dragging from a connector dot.
            var handle = e.target.closest('.bp-handle');
            if (handle) {
                e.preventDefault();
                var host = handle.closest('.bp-node, .bp-param-node');
                var row = handle.closest('.bp-col, .bp-param');
                drag = {
                    kind: 'edge',
                    dir: handle.dataset.h,
                    param: handle.dataset.param || null,
                    nodeId: host.dataset.id || null,
                    col: row && row.dataset.col ? row.dataset.col : null
                };
                return;
            }

            // 2. Drag a node by its header.
            var head = e.target.closest('.bp-head');
            if (head && !e.target.closest('button, select')) {
                var box = head.closest('.bp-node, .bp-param-node');
                var isParam = box.classList.contains('bp-param-node');

                // The name input covers most of the header. A click still allows dragging: wait
                // for the mouse to move more than 3px before treating it as a drag, otherwise the input receives focus for editing.
                var onInput = !!e.target.closest('input');
                if (!onInput) e.preventDefault();

                drag = {
                    kind: isParam ? 'param' : 'node',
                    id: box.dataset.id,
                    el: box,
                    startX: e.clientX,
                    startY: e.clientY,
                    origX: isParam ? G.ParamX : nodeById(box.dataset.id).X,
                    origY: isParam ? G.ParamY : nodeById(box.dataset.id).Y,
                    pending: onInput,
                    inputEl: onInput ? e.target : null
                };
                if (!isParam) select(box.dataset.id);
                return;
            }

            // 3. Drag the background to pan.
            if (e.target === canvas || e.target === content || e.target === edgesSvg) {
                drag = { kind: 'pan', startX: e.clientX, startY: e.clientY, origX: G.PanX, origY: G.PanY };
                canvas.dataset.panning = 'true';
            }
        });

        window.addEventListener('mousemove', function (e) {
            if (!drag) return;

            if (drag.kind === 'pan') {
                G.PanX = drag.origX + (e.clientX - drag.startX);
                G.PanY = drag.origY + (e.clientY - drag.startY);
                applyTransform();
                return;
            }

            if (drag.kind === 'node' || drag.kind === 'param') {
                if (drag.pending) {
                    // Below the threshold, treat it as a click on the input, not a drag.
                    if (Math.abs(e.clientX - drag.startX) < 3 && Math.abs(e.clientY - drag.startY) < 3) return;
                    drag.pending = false;
                    if (drag.inputEl) drag.inputEl.blur();
                    if (window.getSelection) window.getSelection().removeAllRanges();
                    document.body.style.userSelect = 'none';
                }

                var dx = (e.clientX - drag.startX) / G.Zoom;
                var dy = (e.clientY - drag.startY) / G.Zoom;
                var x = Math.round((drag.origX + dx) / 10) * 10;
                var y = Math.round((drag.origY + dy) / 10) * 10;

                if (drag.kind === 'param') { G.ParamX = x; G.ParamY = y; }
                else { var n = nodeById(drag.id); n.X = x; n.Y = y; }

                drag.el.style.left = x + 'px';
                drag.el.style.top = y + 'px';
                drag.moved = true;
                drawEdges();
                return;
            }

            if (drag.kind === 'edge') {
                var a = drag.dir === 'out'
                    ? anchor(drag.nodeId, drag.param, drag.col, 'out')
                    : anchor(drag.nodeId, drag.param, drag.col, 'in');
                if (!a) return;

                var cRect = content.getBoundingClientRect();
                var b = { x: (e.clientX - cRect.left) / G.Zoom, y: (e.clientY - cRect.top) / G.Zoom };
                drawEdges(drag.dir === 'out' ? bez(a, b) : bez(b, a));
            }
        });

        window.addEventListener('mouseup', function (e) {
            if (!drag) return;
            var d = drag;
            drag = null;
            delete canvas.dataset.panning;
            document.body.style.userSelect = '';

            if (d.kind === 'node' || d.kind === 'param') {
                if (d.moved) markDirty();
                return;
            }
            if (d.kind !== 'edge') return;

            var target = document.elementFromPoint(e.clientX, e.clientY);
            var handle = target && target.closest ? target.closest('.bp-handle') : null;
            if (!handle) { drawEdges(); return; }

            var host = handle.closest('.bp-node, .bp-param-node');
            var row = handle.closest('.bp-col, .bp-param');
            var end = {
                dir: handle.dataset.h,
                param: handle.dataset.param || null,
                nodeId: host.dataset.id || null,
                col: row && row.dataset.col ? row.dataset.col : null
            };

            // The 'out' end supplies the value; the 'in' end is the filtered side.
            var from = d.dir === 'out' ? d : end;
            var to = d.dir === 'out' ? end : d;

            if (from.dir === to.dir) {
                TP.toast('Link from the right dot (supplies value) to the left dot (filtered)', 'err');
                drawEdges();
                return;
            }
            if (to.param) {
                TP.toast('Parameters only supply values, they cannot receive them', 'err');
                drawEdges();
                return;
            }

            var ok = addEdge(from.param
                ? { FromParam: from.param, ToNode: to.nodeId, ToColumn: to.col }
                : { FromNode: from.nodeId, FromColumn: from.col, ToNode: to.nodeId, ToColumn: to.col });

            if (ok) {
                if (from.nodeId) ensureShown(nodeById(from.nodeId), from.col);
                ensureShown(nodeById(to.nodeId), to.col);
                renderAll();
            } else {
                drawEdges();
            }
        });

        // Zoom: ctrl+wheel anywhere, plain wheel when not inside a column frame.
        canvas.addEventListener('wheel', function (e) {
            if (!e.ctrlKey && !e.metaKey && e.target.closest && e.target.closest('.bp-cols, .bp-params')) return;
            e.preventDefault();
            var rect = canvas.getBoundingClientRect();
            setZoom(G.Zoom * (e.deltaY < 0 ? 1.12 : 1 / 1.12), e.clientX - rect.left, e.clientY - rect.top);
        }, { passive: false });

        // Click a link to delete it.
        edgesSvg.addEventListener('click', function (e) {
            var g = e.target.closest('.edge-g');
            if (!g) return;
            removeEdge(g.dataset.id);
            TP.toast('Link removed', 'ok');
        });
    }

    // ---------------------------------------------------- in-node events

    function initNodeEvents() {
        content.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-act]');
            if (btn) {
                var act = btn.dataset.act;
                if (act === 'add-param') { addParam(); return; }
                if (act === 'del-param') { removeParam(btn.closest('.bp-param').dataset.key); return; }

                var box = btn.closest('.bp-node');
                if (act === 'del-node') { removeNode(box.dataset.id); return; }
                if (act === 'props') { select(box.dataset.id); openProps(box.dataset.id); return; }
            }

            var node = e.target.closest('.bp-node');
            if (node) select(node.dataset.id);
        });

        content.addEventListener('change', function (e) {
            var f = e.target.dataset.f;
            if (!f) return;

            // ---- parameters
            var prow = e.target.closest('.bp-param');
            if (prow) {
                var p = G.Params.filter(function (x) { return x.Key === prow.dataset.key; })[0];
                if (!p) return;

                if (f === 'key') {
                    if (renameParam(p.Key, e.target.value)) renderAll();
                    else e.target.value = p.Key;
                } else if (f === 'label') {
                    p.Label = e.target.value; markDirty();
                } else if (f === 'default') {
                    p.Default = e.target.value; markDirty();
                } else if (f === 'required') {
                    p.Required = e.target.checked; markDirty();
                } else if (f === 'type') {
                    p.Type = e.target.value;
                    // The default input type changes too; if the old value is invalid the browser
                    // silently discards it while the model keeps it -> clear it to match what's shown.
                    if (!defaultFits(p)) {
                        TP.toast('"' + p.Default + '" is not valid for type ' + e.target.value + ' — default value cleared', 'err');
                        p.Default = '';
                    }
                    markDirty();
                    renderAll();
                }
                return;
            }

            // ---- node events
            var box = e.target.closest('.bp-node');
            if (!box) return;
            var n = nodeById(box.dataset.id);
            if (!n) return;

            if (f === 'show') {
                var col = e.target.closest('.bp-col').dataset.col;
                if (e.target.checked) ensureShown(n, col);
                else n.Columns = n.Columns.filter(function (c) { return c !== col; });

                box.querySelector('.bp-foot span').textContent = n.Columns.length + '/' + cols(n).length + ' col(s)';
                markDirty();
            } else if (f === 'limit') {
                var v = parseInt(e.target.value, 10);
                n.Limit = (v > 0 && v <= 2000) ? v : 50;
                e.target.value = n.Limit;
                markDirty();
            } else if (f === 'alias') {
                n.Alias = e.target.value.trim();
                markDirty();
            }
        });

        // Column filter: only redraw that node's column list, not the whole canvas.
        content.addEventListener('input', function (e) {
            if (e.target.dataset.f !== 'colfilter') return;

            var box = e.target.closest('.bp-node');
            var n = nodeById(box.dataset.id);
            n._filter = e.target.value;
            box.querySelector('.bp-cols').innerHTML = colRows(n);
            refreshLinked();
        });
    }

    function select(id) {
        selected = id;
        content.querySelectorAll('.bp-node').forEach(function (el) {
            if (el.dataset.id === id) el.dataset.selected = 'true';
            else delete el.dataset.selected;
        });
    }

    // ------------------------------------------------------- properties panel

    function openProps(id) {
        var n = nodeById(id);
        if (!n) return;

        if (!cols(n).length) {
            TP.toast('Could not load structure for ' + n.Table + ' — table may have been dropped', 'err');
            return;
        }

        $('propsTitle').textContent = n.Alias || (meta[n.Table] ? meta[n.Table].Name : n.Table);
        $('propsBody').innerHTML =
            '<div class="field"><span class="label">Table</span><code style="font-size:11.5px">' + esc(n.Table) + '</code></div>'
            + '<div class="field"><label class="label">Sort by</label>'
            + '<select class="select" data-p="sort"><option value="">(primary key)</option>'
            + cols(n).map(function (c) {
                return '<option value="' + esc(c.Name) + '"' + (n.SortColumn === c.Name ? ' selected' : '') + '>'
                    + esc(c.Name) + '</option>';
            }).join('') + '</select></div>'
            + '<label class="check"><input type="checkbox" data-p="desc"' + (n.SortDesc ? ' checked' : '') + ' /> Descending</label>'
            + '<div class="field"><label class="label">Max rows</label>'
            + '<input class="input" data-p="limit" value="' + n.Limit + '" /></div>'
            + '<div class="section-title" style="margin:var(--s2) 0">Conditions</div>'
            + '<div id="propsFilters"></div>'
            + '<button type="button" class="btn btn--block" data-p="add-filter">'
            + '<svg width="13" height="13"><use href="#i-plus"/></svg> Add condition</button>'
            + '<div class="example">'
            + '<b>Example — fetch orders for one month:</b><br>'
            + 'in the PARAMETERS node add parameter <code>month</code> of type <b>month</b>, then add 2 conditions:<br>'
            + '<code>OrderDate</code> · <code>≥ from</code> · <code>{month:tu}</code><br>'
            + '<code>OrderDate</code> · <code>&lt; less than</code> · <code>{month:den}</code><br>'
            + 'At run time you only pick the month; the two date boundaries are computed automatically.<br><br>'
            + 'A parameter only mentioned in <code>{braces}</code> is still prompted at run time — '
            + 'no need to drag a link to a column.'
            + '</div>'
            + '<div class="section-title" style="margin:var(--s2) 0">Columns</div>'
            + '<div style="display:flex;gap:var(--s2)">'
            + '<button type="button" class="btn btn--ghost" data-p="all">Select all</button>'
            + '<button type="button" class="btn btn--ghost" data-p="none">Deselect all</button></div>';

        renderPropFilters(n);
        $('props').hidden = false;
    }

    /** Suggests {parameter} tokens (and their :tu/:den variants) while typing after an unclosed '{'. */
    function paramTokenItems(value, caret) {
        var before = value.slice(0, caret);
        var m = /\{([A-Za-z0-9_:]*)$/.exec(before);
        if (!m) return null;

        var kw = m[1].toLowerCase();
        var items = [];
        function push(token, hint) {
            if (kw && token.toLowerCase().indexOf(kw) !== 0) return;
            items.push({ label: '{' + token + '}', hint: hint });
        }

        G.Params.forEach(function (p) {
            var who = p.Label || p.Key;
            push(p.Key, 'value for "' + who + '"');
            if (p.Type === 'month') {
                push(p.Key + ':tu', 'first day of month for "' + who + '"');
                push(p.Key + ':den', 'first day of NEXT month for "' + who + '"');
            } else if (p.Type === 'date') {
                push(p.Key + ':tu', '00:00 on day "' + who + '"');
                push(p.Key + ':den', '00:00 the day AFTER "' + who + '"');
            }
        });

        if (!items.length) return null;
        return { items: items, range: [caret - m[0].length, caret] };
    }

    function renderPropFilters(n) {
        var box = $('propsFilters');

        if (!n.Filters.length) {
            box.innerHTML = '<div class="hint">No conditions — node returns all rows matching the parent.</div>';
            return;
        }

        box.innerHTML = n.Filters.map(function (f, i) {
            var hideValue = noValueOp(f.Op);

            return '<div class="filter-card" data-i="' + i + '">'
                + '<div class="filter-card-top">'
                + '<select class="select" data-p="f-col">'
                + cols(n).map(function (c) {
                    return '<option value="' + esc(c.Name) + '"' + (f.Column === c.Name ? ' selected' : '') + '>'
                        + esc(c.Name) + '</option>';
                }).join('')
                + '</select>'
                + '<button type="button" class="bp-btn bp-btn--danger" data-p="f-del" aria-label="Remove condition">'
                + '<svg width="13" height="13"><use href="#i-x"/></svg></button>'
                + '</div>'
                + '<div class="filter-card-row">'
                + '<select class="select" data-p="f-op">'
                + OPS.map(function (o) {
                    return '<option value="' + o.v + '"' + (f.Op === o.v ? ' selected' : '') + '>' + o.t + '</option>';
                }).join('')
                + '</select>'
                + (hideValue
                    ? '<span class="hint">no value needed</span>'
                    : '<input class="input" data-p="f-val" value="' + esc(f.Value || '') + '" placeholder="value or {parameter}" />')
                + '</div>'
                + (hideValue ? '' : tokenChips())
                + '</div>';
        }).join('');

        box.querySelectorAll('[data-p="f-val"]').forEach(function (input) {
            TP.attachAutocomplete(input, { getItems: paramTokenItems });
        });
    }

    /**
     * Quick-insert button for parameters into the value field. Previously the docs just said "use {paramName}" without
     * listing what parameters exist — nobody could guess what to type.
     */
    function tokenChips() {
        if (!G.Params.length) {
            return '<div class="hint">No parameters yet. Add one in the <b>PARAMETERS</b> node then come back to insert.</div>';
        }

        var chips = [];
        G.Params.forEach(function (p) {
            var who = p.Label || p.Key;
            chips.push(chip('{' + p.Key + '}', 'Value the user enters for "' + who + '"'));
            if (p.Type === 'month') {
                chips.push(chip('{' + p.Key + ':tu}', 'First day of month for "' + who + '"'));
                chips.push(chip('{' + p.Key + ':den}', 'First day of the NEXT month for "' + who + '"'));
            } else if (p.Type === 'date') {
                chips.push(chip('{' + p.Key + ':tu}', '00:00 on day "' + who + '"'));
                chips.push(chip('{' + p.Key + ':den}', '00:00 the day AFTER "' + who
                    + '" — use with "< less than" to include the full last day, even rows with a time component'));
            }
        });

        return '<div class="chips">' + chips.join('') + '</div>';
    }

    function chip(token, title) {
        return '<button type="button" class="chip" data-p="f-token" data-token="' + esc(token) + '"'
            + ' title="' + esc(title) + ' — click to insert">' + esc(token) + '</button>';
    }

    /** Insert the token at the cursor position of the value input in the same condition row. */
    function insertToken(btn) {
        var card = btn.closest('.filter-card');
        var input = card.querySelector('[data-p="f-val"]');
        if (!input) return;

        var token = btn.dataset.token;
        var start = input.selectionStart != null ? input.selectionStart : input.value.length;
        var end = input.selectionEnd != null ? input.selectionEnd : input.value.length;

        input.value = input.value.slice(0, start) + token + input.value.slice(end);
        input.focus();
        input.setSelectionRange(start + token.length, start + token.length);

        var n = nodeById(selected);
        if (n) { n.Filters[+card.dataset.i].Value = input.value; markDirty(); }
    }

    function closeProps() {
        $('props').hidden = true;
        selected = null;
        content.querySelectorAll('.bp-node').forEach(function (el) { delete el.dataset.selected; });
    }

    function initProps() {
        $('propsClose').addEventListener('click', closeProps);

        $('propsBody').addEventListener('click', function (e) {
            var btn = e.target.closest('[data-p]');
            if (!btn || btn.tagName !== 'BUTTON') return;
            var n = nodeById(selected);
            if (!n) return;

            if (btn.dataset.p === 'f-token') {
                insertToken(btn);
                return;
            }

            if (btn.dataset.p === 'add-filter') {
                if (!cols(n).length) return;
                n.Filters.push({ Column: cols(n)[0].Name, Op: '=', Value: '' });
                renderPropFilters(n);
            } else if (btn.dataset.p === 'f-del') {
                n.Filters.splice(+btn.closest('.filter-card').dataset.i, 1);
                renderPropFilters(n);
            } else if (btn.dataset.p === 'all') {
                n.Columns = cols(n).filter(function (c) { return !c.IsSkipped; }).map(function (c) { return c.Name; });
            } else if (btn.dataset.p === 'none') {
                n.Columns = cols(n).filter(function (c) { return c.IsPrimaryKey; }).map(function (c) { return c.Name; });
            } else {
                return;
            }

            markDirty();
            redrawNode(n);
        });

        $('propsBody').addEventListener('change', function (e) {
            var p = e.target.dataset.p;
            if (!p) return;
            var n = nodeById(selected);
            if (!n) return;

            if (p === 'sort') n.SortColumn = e.target.value || null;
            else if (p === 'desc') n.SortDesc = e.target.checked;
            else if (p === 'limit') {
                var v = parseInt(e.target.value, 10);
                n.Limit = (v > 0 && v <= 2000) ? v : 50;
                e.target.value = n.Limit;
            } else if (p.indexOf('f-') === 0) {
                var f = n.Filters[+e.target.closest('.filter-card').dataset.i];
                if (p === 'f-col') f.Column = e.target.value;
                if (p === 'f-val') f.Value = e.target.value;
                if (p === 'f-op') {
                    f.Op = e.target.value;
                    renderPropFilters(n);   // show/hide the value input based on the selected operator
                }
            } else {
                return;
            }

            markDirty();
            redrawNode(n);
        });
    }

    /** Redraw a single node in place, preserving the canvas scroll position. */
    function redrawNode(n) {
        var old = nodeEl(n.Id);
        if (!old) return;
        var fresh = buildNode(n);
        old.replaceWith(fresh);
        refreshLinked();
        updateStat();
    }

    // --------------------------------------------------------------------- save

    function cleanGraph() {
        return {
            Params: G.Params.map(function (p) {
                return { Key: p.Key, Label: p.Label || '', Type: p.Type, Required: !!p.Required, Default: p.Default || '' };
            }),
            Nodes: G.Nodes.map(function (n) {
                return {
                    Id: n.Id, Table: n.Table, Alias: n.Alias || '', X: n.X, Y: n.Y,
                    Columns: n.Columns.slice(), Filters: n.Filters.slice(),
                    Limit: n.Limit, SortColumn: n.SortColumn || null, SortDesc: !!n.SortDesc,
                    Collapsed: !!n.Collapsed
                };
            }),
            Edges: G.Edges.map(function (e) {
                return {
                    Id: e.Id, FromParam: e.FromParam || null,
                    FromNode: e.FromNode || null, FromColumn: e.FromColumn || null,
                    ToNode: e.ToNode, ToColumn: e.ToColumn
                };
            }),
            Zoom: G.Zoom, PanX: G.PanX, PanY: G.PanY, ParamX: G.ParamX, ParamY: G.ParamY
        };
    }

    async function save() {
        var name = $('bpName').value.trim();
        if (!name) { TP.toast('Please set a blueprint name', 'err'); $('bpName').focus(); return; }

        var keys = G.Params.map(function (p) { return p.Key; });
        if (keys.some(function (k, i) { return !k || keys.indexOf(k) !== i; })) {
            TP.toast('Parameter names must be unique and non-empty', 'err');
            return;
        }

        var btn = $('btnSave');
        btn.disabled = true;
        try {
            var res = await TP.post('/blueprint/save', {
                Id: D.id || '00000000-0000-0000-0000-000000000000',
                Name: name,
                Description: $('bpDesc').value.trim(),
                Icon: $('bpIcon').value,
                Pinned: $('bpPinned').checked,
                IsShared: true,
                Graph: cleanGraph()
            });

            if (!D.id) {
                D.id = res.Id;
                history.replaceState(null, '', TP.u('/blueprint/ve/' + res.Id));
            }
            dirty = false;
            updateStat();
            TP.toast('Blueprint "' + res.Name + '" saved', 'ok');
        } catch (e) {
            TP.toast(e.message, 'err');
        } finally {
            btn.disabled = false;
        }
    }

    // -------------------------------------------------------------------- init

    async function boot() {
        canvas = $('canvas');
        content = $('canvasContent');
        edgesSvg = $('edges');
        emptyBox = $('canvasEmpty');
        zoomLabel = $('zoomLabel');

        // Load the existing blueprint.
        var g = D.graph || {};
        G.Params = g.Params || [];
        G.Nodes = g.Nodes || [];
        G.Edges = g.Edges || [];
        G.Zoom = g.Zoom || 1;
        G.PanX = g.PanX || 0;
        G.PanY = g.PanY || 0;
        G.ParamX = g.ParamX != null ? g.ParamX : 40;
        G.ParamY = g.ParamY != null ? g.ParamY : 40;

        // The next id must be larger than all used ids, otherwise there will be collisions.
        G.Nodes.concat(G.Edges).forEach(function (x) {
            var n = parseInt(String(x.Id || '').replace(/^\D+/, ''), 10);
            if (n >= seq) seq = n + 1;
        });

        initPointer();
        initNodeEvents();
        initProps();

        $('tableFilter').addEventListener('input', renderTableList);
        $('tableList').addEventListener('click', function (e) {
            var item = e.target.closest('.table-item');
            if (!item) return;
            addNode(item.dataset.full);
            if (window.innerWidth <= 1280) $('palette').dataset.open = 'false';
        });
        $('paletteToggle').addEventListener('click', function () {
            var p = $('palette');
            p.dataset.open = p.dataset.open === 'true' ? 'false' : 'true';
        });

        $('btnAddParam').addEventListener('click', addParam);
        $('btnSave').addEventListener('click', save);
        $('btnFit').addEventListener('click', fitView);
        $('zoomIn').addEventListener('click', function () { setZoom(G.Zoom * 1.15); });
        $('zoomOut').addEventListener('click', function () { setZoom(G.Zoom / 1.15); });
        $('zoomReset').addEventListener('click', function () { G.Zoom = 1; G.PanX = 0; G.PanY = 0; applyTransform(); drawEdges(); });

        ['bpName', 'bpDesc', 'bpIcon', 'bpPinned'].forEach(function (id) {
            $(id).addEventListener('change', markDirty);
        });

        document.addEventListener('keydown', function (e) {
            if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); save(); return; }
            if (e.key === 'Escape') { closeProps(); return; }

            var tag = (e.target.tagName || '').toLowerCase();
            if (tag === 'input' || tag === 'select' || tag === 'textarea') return;
            if (e.key === 'Delete' && selected) removeNode(selected);
        });

        window.addEventListener('beforeunload', function (e) {
            if (dirty) { e.preventDefault(); e.returnValue = ''; }
        });

        // Load the structure for all tables already in the blueprint before rendering.
        var uniq = [];
        G.Nodes.forEach(function (n) { if (uniq.indexOf(n.Table) < 0) uniq.push(n.Table); });

        try {
            await Promise.all(uniq.map(ensureMeta));
        } catch (e) {
            TP.toast('Some tables in the blueprint no longer exist: ' + e.message, 'err');
        }

        renderAll();
        loadTables();
    }

    document.addEventListener('DOMContentLoaded', boot);
})();
