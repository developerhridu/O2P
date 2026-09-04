import { useEffect, useState } from 'react';
import { KeyRound, LockOpen, Plus, Shield, UserCog, X } from 'lucide-react';
import { createUser, fetchUsers, hasRole, resetUserPassword, unlockUser, updateUser } from '../api';

const emptyForm = {
  username: '',
  email: '',
  displayName: '',
  password: '',
  roles: ['Viewer'],
  mustChangePassword: true,
  isActive: true,
};

export default function Users() {
  const [users, setUsers] = useState<any[]>([]);
  const [roles, setRoles] = useState<string[]>(['Admin', 'Operator', 'Viewer']);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [modalMode, setModalMode] = useState<'create' | 'edit' | null>(null);
  const [editingUser, setEditingUser] = useState<any>(null);
  const [form, setForm] = useState({ ...emptyForm });
  const [submitting, setSubmitting] = useState(false);
  const [passwordUser, setPasswordUser] = useState<any>(null);
  const [resetPasswordValue, setResetPasswordValue] = useState('');
  const [forceResetChange, setForceResetChange] = useState(true);

  const canManage = hasRole('Admin');

  const loadUsers = async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await fetchUsers();
      setUsers(data.users || []);
      setRoles(data.roles || ['Admin', 'Operator', 'Viewer']);
    } catch (err: any) {
      setError(err.message || 'Failed to load users.');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadUsers();
  }, []);

  const openCreate = () => {
    setEditingUser(null);
    setForm({ ...emptyForm });
    setModalMode('create');
  };

  const openEdit = (user: any) => {
    setEditingUser(user);
    setForm({
      username: user.username,
      email: user.email || '',
      displayName: user.displayName || '',
      password: '',
      roles: user.roles || ['Viewer'],
      mustChangePassword: user.mustChangePassword,
      isActive: user.isActive,
    });
    setModalMode('edit');
  };

  const toggleRole = (role: string) => {
    setForm((current) => {
      const exists = current.roles.includes(role);
      const roles = exists ? current.roles.filter((item) => item !== role) : [...current.roles, role];
      return { ...current, roles: roles.length ? roles : current.roles };
    });
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setSubmitting(true);
    try {
      if (modalMode === 'create') {
        await createUser(form);
      } else if (editingUser) {
        await updateUser(editingUser.id, {
          email: form.email,
          displayName: form.displayName,
          roles: form.roles,
          mustChangePassword: form.mustChangePassword,
          isActive: form.isActive,
        });
      }
      setModalMode(null);
      await loadUsers();
    } catch (err: any) {
      setError(err.message || 'Failed to save user.');
    } finally {
      setSubmitting(false);
    }
  };

  const runResetPassword = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!passwordUser) return;
    setSubmitting(true);
    try {
      await resetUserPassword(passwordUser.id, resetPasswordValue, forceResetChange);
      setPasswordUser(null);
      setResetPasswordValue('');
      await loadUsers();
    } catch (err: any) {
      setError(err.message || 'Failed to reset password.');
    } finally {
      setSubmitting(false);
    }
  };

  if (!canManage) {
    return <div className="card text-center py-12 text-slate-300">Admin role required.</div>;
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Users & Roles</h1>
          <p className="mt-1 text-sm text-slate-400">Manage operator access, role assignment, lockouts, and password reset flows.</p>
        </div>
        <button onClick={openCreate} className="btn">
          <Plus size={18} />
          New User
        </button>
      </div>

      {error && <div className="rounded-lg border border-rose-500/30 bg-rose-500/10 px-4 py-3 text-sm text-rose-200">{error}</div>}

      <div className="bg-slate-900 border border-slate-800 rounded-xl overflow-hidden shadow-sm">
        {loading ? (
          <div className="px-6 py-12 text-center text-slate-500">Loading users...</div>
        ) : (
          <table className="w-full text-left">
            <thead className="bg-slate-950/50 border-b border-slate-800 text-slate-400 text-sm">
              <tr>
                <th className="px-6 py-4 font-medium">User</th>
                <th className="px-6 py-4 font-medium">Roles</th>
                <th className="px-6 py-4 font-medium">Status</th>
                <th className="px-6 py-4 font-medium">Password</th>
                <th className="px-6 py-4 font-medium">Last Login</th>
                <th className="px-6 py-4 font-medium text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-800 text-slate-300">
              {users.map((user) => (
                <tr key={user.id} className="hover:bg-slate-800/30 transition-colors">
                  <td className="px-6 py-4">
                    <div className="font-medium text-slate-100">{user.displayName || user.username}</div>
                    <div className="text-xs text-slate-500">{user.username}</div>
                    <div className="text-xs text-slate-500">{user.email}</div>
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex flex-wrap gap-2">
                      {user.roles.map((role: string) => (
                        <span key={role} className="inline-flex items-center rounded-md border border-slate-700 bg-slate-950 px-2.5 py-1 text-xs font-medium text-slate-300">
                          <Shield size={12} className="mr-1.5" />
                          {role}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-6 py-4 text-sm">
                    <div className={user.isActive ? 'text-emerald-300' : 'text-rose-300'}>{user.isActive ? 'Active' : 'Disabled'}</div>
                    {user.lockoutEnd && <div className="text-xs text-amber-300">Locked until {new Date(user.lockoutEnd).toLocaleString()}</div>}
                  </td>
                  <td className="px-6 py-4 text-sm">
                    {user.mustChangePassword ? <span className="text-amber-300">Change required</span> : <span className="text-slate-400">Current</span>}
                  </td>
                  <td className="px-6 py-4 text-sm text-slate-400">
                    {user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : 'Never'}
                  </td>
                  <td className="px-6 py-4">
                    <div className="flex justify-end gap-2">
                      <button className="btn btn-secondary px-3 text-sm" onClick={() => openEdit(user)}>
                        <UserCog size={15} />
                        Edit
                      </button>
                      <button
                        className="btn btn-secondary px-3 text-sm"
                        onClick={() => {
                          setPasswordUser(user);
                          setResetPasswordValue('');
                          setForceResetChange(true);
                        }}
                      >
                        <KeyRound size={15} />
                        Reset Password
                      </button>
                      {!!user.lockoutEnd && (
                        <button className="btn btn-secondary px-3 text-sm" onClick={async () => { await unlockUser(user.id); await loadUsers(); }}>
                          <LockOpen size={15} />
                          Unlock
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {modalMode && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-lg p-6 shadow-2xl relative">
            <button onClick={() => setModalMode(null)} className="absolute top-4 right-4 text-slate-400 hover:text-slate-200">
              <X size={20} />
            </button>
            <h2 className="text-xl font-semibold mb-6">{modalMode === 'create' ? 'Create User' : `Edit ${editingUser.username}`}</h2>
            <form onSubmit={submit} className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Username</label>
                  <input
                    type="text"
                    value={form.username}
                    disabled={modalMode === 'edit'}
                    onChange={(event) => setForm({ ...form, username: event.target.value })}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100"
                    required
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Display Name</label>
                  <input
                    type="text"
                    value={form.displayName}
                    onChange={(event) => setForm({ ...form, displayName: event.target.value })}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100"
                  />
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">Email</label>
                <input
                  type="email"
                  value={form.email}
                  onChange={(event) => setForm({ ...form, email: event.target.value })}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100"
                  required
                />
              </div>

              {modalMode === 'create' && (
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Temporary Password</label>
                  <input
                    type="password"
                    value={form.password}
                    onChange={(event) => setForm({ ...form, password: event.target.value })}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100"
                    minLength={12}
                    required
                  />
                </div>
              )}

              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">Roles</label>
                <div className="grid grid-cols-3 gap-3">
                  {roles.map((role) => (
                    <label key={role} className="flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-950 px-3 py-2 text-sm text-slate-300">
                      <input type="checkbox" checked={form.roles.includes(role)} onChange={() => toggleRole(role)} />
                      {role}
                    </label>
                  ))}
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <label className="flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-950 px-3 py-2 text-sm text-slate-300">
                  <input
                    type="checkbox"
                    checked={form.isActive}
                    onChange={(event) => setForm({ ...form, isActive: event.target.checked })}
                  />
                  Active
                </label>
                <label className="flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-950 px-3 py-2 text-sm text-slate-300">
                  <input
                    type="checkbox"
                    checked={form.mustChangePassword}
                    onChange={(event) => setForm({ ...form, mustChangePassword: event.target.checked })}
                  />
                  Force password change
                </label>
              </div>

              <div className="flex gap-3 pt-4">
                <button type="button" onClick={() => setModalMode(null)} className="btn btn-secondary flex-1">Cancel</button>
                <button type="submit" disabled={submitting} className="btn flex-1">{submitting ? 'Saving...' : 'Save User'}</button>
              </div>
            </form>
          </div>
        </div>
      )}

      {passwordUser && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70 p-4 backdrop-blur-sm">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-md p-6 shadow-2xl relative">
            <button onClick={() => setPasswordUser(null)} className="absolute top-4 right-4 text-slate-400 hover:text-slate-200">
              <X size={20} />
            </button>
            <h2 className="text-xl font-semibold mb-2">Reset Password</h2>
            <p className="text-sm text-slate-400 mb-5">Set a temporary password for {passwordUser.username}.</p>
            <form onSubmit={runResetPassword} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">New Temporary Password</label>
                <input
                  type="password"
                  value={resetPasswordValue}
                  onChange={(event) => setResetPasswordValue(event.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100"
                  minLength={12}
                  required
                />
              </div>
              <label className="flex items-center gap-2 rounded-lg border border-slate-800 bg-slate-950 px-3 py-2 text-sm text-slate-300">
                <input type="checkbox" checked={forceResetChange} onChange={(event) => setForceResetChange(event.target.checked)} />
                Require password change on next login
              </label>
              <div className="flex gap-3 pt-2">
                <button type="button" onClick={() => setPasswordUser(null)} className="btn btn-secondary flex-1">Cancel</button>
                <button type="submit" disabled={submitting} className="btn flex-1">{submitting ? 'Resetting...' : 'Reset Password'}</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
