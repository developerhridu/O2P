import { useState } from 'react';
import { Navigate, useNavigate } from 'react-router-dom';
import { changePassword, getAuthState, isAuthenticated, logout } from '../api';

export default function ChangePassword() {
  const navigate = useNavigate();
  const auth = getAuthState();
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  if (!isAuthenticated()) {
    return <Navigate to="/login" replace />;
  }

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    setSuccess(null);

    if (newPassword !== confirmPassword) {
      setError('New password and confirmation do not match.');
      return;
    }

    setSubmitting(true);
    try {
      await changePassword(currentPassword, newPassword);
      setSuccess('Password updated successfully.');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      if (auth?.mustChangePassword) {
        window.location.assign('/');
        return;
      }
    } catch (err: any) {
      setError(err.message || 'Failed to change password.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen flex items-center justify-center bg-slate-950 p-4">
      <div className="w-full max-w-lg rounded-2xl border border-slate-800 bg-slate-900 p-8 shadow-2xl">
        <h1 className="text-3xl font-bold text-slate-100">Update Password</h1>
        <p className="mt-2 text-sm text-slate-400">
          {auth?.mustChangePassword
            ? 'This account must change its password before continuing.'
            : 'Use a strong password with uppercase, lowercase, number, and symbol.'}
        </p>

        <form onSubmit={submit} className="mt-6 space-y-4">
          <div>
            <label className="block text-sm font-medium text-slate-300 mb-2">Current Password</label>
            <input
              type="password"
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.target.value)}
              className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-3 text-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-500/50"
              required
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-300 mb-2">New Password</label>
            <input
              type="password"
              value={newPassword}
              onChange={(event) => setNewPassword(event.target.value)}
              className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-3 text-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-500/50"
              minLength={12}
              required
            />
          </div>
          <div>
            <label className="block text-sm font-medium text-slate-300 mb-2">Confirm New Password</label>
            <input
              type="password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-3 text-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-500/50"
              minLength={12}
              required
            />
          </div>

          {error && <div className="rounded-lg border border-rose-500/30 bg-rose-500/10 px-4 py-3 text-sm text-rose-200">{error}</div>}
          {success && <div className="rounded-lg border border-emerald-500/30 bg-emerald-500/10 px-4 py-3 text-sm text-emerald-200">{success}</div>}

          <div className="flex gap-3 pt-2">
            {!auth?.mustChangePassword && (
              <button type="button" className="btn btn-secondary flex-1" onClick={() => navigate(-1)}>
                Cancel
              </button>
            )}
            <button type="submit" className="btn flex-1" disabled={submitting}>
              {submitting ? 'Updating...' : 'Update Password'}
            </button>
          </div>
        </form>

        {!auth?.mustChangePassword && (
          <button
            type="button"
            className="mt-6 text-sm text-slate-500 hover:text-slate-300"
            onClick={() => {
              logout();
              navigate('/login');
            }}
          >
            Sign out instead
          </button>
        )}
      </div>
    </div>
  );
}
