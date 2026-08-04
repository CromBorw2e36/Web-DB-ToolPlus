/* Users — ToolPlus accounts and their permission on each module.
   The module list comes from the server rather than being hard-coded: add a connection string and a
   new permission row appears without touching this file. */
(function () {
    'use strict';

    var S = {
        users: [],
        modules: [],
        me: null,
        editing: null      // null = creating a new account
    };

    function $(id) { return document.getElementById(id); }
    var esc = function (s) { return TP.escapeHtml(s); };

    // ------------------------------------------------------------------ load

    async function load() {
        try {
            var data = await TP.get('/user/danhsach');
            S.users = data.Users || [];
            S.modules = data.Modules || [];
            S.me = data.Me;
            render();
        } catch (e) {
            TP.toast(e.message, 'err');
        }
    }

    function render() {
        var body = $('userBody');

        if (!S.users.length) {
            body.innerHTML = '<tr><td colspan="8">No accounts found.</td></tr>';
            return;
        }

        body.innerHTML = S.users.map(function (u) {
            return '<tr>'
                + '<td><b>' + esc(u.Username) + '</b></td>'
                + '<td>' + esc(u.FullName) + '</td>'
                + '<td>' + (u.IsAdmin ? '<span class="badge badge--pk">admin</span>' : 'user') + '</td>'
                + '<td>' + trangThai(u) + '</td>'
                + '<td>' + (u.ExpiresAt ? ngay(u.ExpiresAt) : '—') + '</td>'
                + '<td>' + quyenModule(u) + '</td>'
                + '<td>' + (u.LastLoginAt ? ngayGio(u.LastLoginAt) : '—') + '</td>'
                + '<td style="white-space:nowrap">'
                + '<button type="button" class="btn btn--ghost" data-edit="' + u.Id + '">Edit</button>'
                + (u.Id === S.me ? '' : '<button type="button" class="btn btn--ghost" data-del="' + u.Id + '">Delete</button>')
                + '</td>'
                + '</tr>';
        }).join('');
    }

    function trangThai(u) {
        if (!u.IsActive) return '<span class="badge">locked</span>';
        if (!u.IsUsable) return '<span class="badge">expired</span>';
        if (u.MustChangePassword) return '<span class="badge badge--view">must change password</span>';
        return 'active';
    }

    function quyenModule(u) {
        if (u.IsAdmin) return '<span class="badge badge--pk">all modules</span>';
        if (!u.Modules.length) return '<span class="badge badge--ro">none</span>';

        return u.Modules.map(function (m) {
            return '<span class="badge' + (m.CanEdit ? ' badge--view' : '') + '">'
                + esc(m.ModuleKey) + (m.CanEdit ? ' · edit' : '') + '</span>';
        }).join(' ');
    }

    function ngay(s) { return new Date(s).toLocaleDateString('en-GB'); }
    function ngayGio(s) { return new Date(s).toLocaleString('en-GB'); }

    // ---------------------------------------------------------------- modal

    function openUserModal(user) {
        S.editing = user || null;

        $('userModalTitle').textContent = user ? 'Edit ' + user.Username : 'New user';
        $('fUsername').value = user ? user.Username : '';
        $('fUsername').disabled = !!user;
        $('fFullName').value = user ? user.FullName : '';
        $('fPassword').value = '';
        $('fPasswordHint').textContent = user
            ? 'Leave blank to keep the current password. If you set a new one here, the user will be prompted to change it on next login.'
            : 'Minimum 8 characters.';
        $('fExpires').value = user && user.ExpiresAt ? String(user.ExpiresAt).substring(0, 10) : '';
        $('fIsActive').checked = user ? user.IsActive : true;
        $('fIsAdmin').checked = user ? user.IsAdmin : false;
        $('userModalHint').textContent = '';

        renderModules(user);
        applyAdminState();

        $('userModal').dataset.open = 'true';
        $('fUsername').focus();
    }

    function renderModules(user) {
        var co = {};
        (user ? user.Modules : []).forEach(function (m) { co[m.ModuleKey.toLowerCase()] = m; });

        $('fModules').innerHTML = S.modules.map(function (m) {
            var q = co[m.Key.toLowerCase()];
            return '<div style="display:flex;align-items:center;gap:var(--s3)">'
                + '<label class="check" style="flex:1 1 auto">'
                + '<input type="checkbox" data-mod="' + esc(m.Key) + '"' + (q ? ' checked' : '') + ' /> '
                + esc(m.Name) + ' <code>' + esc(m.Key) + '</code>'
                + '</label>'
                + '<label class="check">'
                + '<input type="checkbox" data-edit-mod="' + esc(m.Key) + '"'
                + (q && q.CanEdit ? ' checked' : '') + (m.AllowEdit ? '' : ' disabled') + ' /> '
                + (m.AllowEdit ? 'can edit' : 'module is read-only')
                + '</label>'
                + '</div>';
        }).join('');
    }

    /** Admins already have everything, so the permission grid would only mislead — dim and lock it. */
    function applyAdminState() {
        var admin = $('fIsAdmin').checked;
        $('fModules').style.opacity = admin ? '.45' : '1';

        S.modules.forEach(function (m) {
            var view = $('fModules').querySelector('[data-mod="' + m.Key + '"]');
            var edit = $('fModules').querySelector('[data-edit-mod="' + m.Key + '"]');

            if (view) view.disabled = admin;
            // A module configured read-only locks its "may edit" box regardless of admin status.
            if (edit) edit.disabled = admin || !m.AllowEdit;
        });
    }

    function closeUserModal() {
        $('userModal').dataset.open = 'false';
        S.editing = null;
    }

    function thuThapModules() {
        var out = [];
        $('fModules').querySelectorAll('[data-mod]').forEach(function (box) {
            if (!box.checked) return;
            var key = box.dataset.mod;
            var editBox = $('fModules').querySelector('[data-edit-mod="' + key + '"]');
            out.push({ ModuleKey: key, CanEdit: !!(editBox && editBox.checked) });
        });
        return out;
    }

    async function luu() {
        var btn = $('userModalSave');
        var req = {
            Id: S.editing ? S.editing.Id : null,
            Username: $('fUsername').value.trim(),
            FullName: $('fFullName').value.trim(),
            Password: $('fPassword').value,
            IsAdmin: $('fIsAdmin').checked,
            IsActive: $('fIsActive').checked,
            ExpiresAt: $('fExpires').value ? $('fExpires').value : null,
            Modules: thuThapModules()
        };

        if (!req.Username) { $('userModalHint').textContent = 'Username is required'; return; }
        if (!req.IsAdmin && !req.Modules.length) {
            $('userModalHint').textContent = 'No module assigned — this user can sign in but cannot access anything';
        }

        btn.disabled = true;
        try {
            await TP.post('/user/luu', req);
            TP.toast('Saved', 'ok');
            closeUserModal();
            await load();
        } catch (e) {
            $('userModalHint').textContent = e.message;
        } finally {
            btn.disabled = false;
        }
    }

    async function xoa(id) {
        var u = S.users.find(function (x) { return x.Id === id; });
        if (!u) return;
        if (!confirm('Delete account "' + u.Username + '"? All their sessions will also be removed.')) return;

        try {
            await TP.postForm('/user/xoa', { id: id });
            TP.toast('Deleted', 'ok');
            await load();
        } catch (e) {
            TP.toast(e.message, 'err');
        }
    }

    // ----------------------------------------------------------------- init

    $('btnAdd').addEventListener('click', function () { openUserModal(null); });
    $('userModalClose').addEventListener('click', closeUserModal);
    $('userModalCancel').addEventListener('click', closeUserModal);
    $('userModalSave').addEventListener('click', luu);
    $('fIsAdmin').addEventListener('change', applyAdminState);

    $('userModal').addEventListener('click', function (e) {
        if (e.target === this) closeUserModal();
    });

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && $('userModal').dataset.open === 'true') closeUserModal();
    });

    $('userBody').addEventListener('click', function (e) {
        var edit = e.target.closest('[data-edit]');
        if (edit) {
            var u = S.users.find(function (x) { return x.Id === edit.dataset.edit; });
            if (u) openUserModal(u);
            return;
        }

        var del = e.target.closest('[data-del]');
        if (del) xoa(del.dataset.del);
    });

    load();
})();
