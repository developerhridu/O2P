import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { changePasswordPublic, login } from '../api';

const inputClass =
  'w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-3 text-slate-100 placeholder-slate-500 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all';

export default function Login() {
  const navigate = useNavigate();
  const [username, setUsername] = useState('admin');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [info, setInfo] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [changeMode, setChangeMode] = useState(false);
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');

  const switchMode = (change: boolean) => {
    setChangeMode(change);
    setError(null);
    setInfo(null);
    setNewPassword('');
    setConfirmPassword('');
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setSubmitting(true);
    setError(null);
    setInfo(null);
    try {
      if (changeMode) {
        if (newPassword !== confirmPassword) {
          setError('New password and confirmation do not match.');
          return;
        }
        await changePasswordPublic(username, password, newPassword);
        setChangeMode(false);
        setPassword('');
        setNewPassword('');
        setConfirmPassword('');
        setInfo('Password updated. Sign in with your new password.');
        return;
      }
      await login(username, password);
      navigate('/');
    } catch (err: any) {
      const message = err?.message || (changeMode ? 'Password change failed' : 'Sign in failed');
      setError(
        message === 'Failed to fetch'
          ? 'Cannot reach the server. Check that it is running and try again.'
          : message
      );
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen flex items-center justify-center bg-slate-950 p-4">
      <div className="max-w-md w-full bg-slate-900 border border-slate-800 rounded-2xl shadow-2xl p-8 relative overflow-hidden">
        <div className="absolute top-0 inset-x-0 h-px bg-gradient-to-r from-transparent via-blue-500 to-transparent"></div>
        <div className="absolute -top-48 -inset-x-24 h-96 bg-blue-500/10 blur-3xl rounded-full pointer-events-none"></div>

        <div className="relative">
          <div className="text-center mb-8">
            <h2 className="text-3xl font-bold bg-gradient-to-r from-blue-400 to-indigo-400 bg-clip-text text-transparent">O2P Migration Studio</h2>
            <p className="text-slate-400 mt-2">{changeMode ? 'Change your password' : 'Sign in to manage your migrations'}</p>
          </div>

          <form className="space-y-6" onSubmit={submit}>
            <div>
              <label className="block text-sm font-medium text-slate-300 mb-2">Username</label>
              <input
                type="text"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
                className={inputClass}
                placeholder="admin"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-slate-300 mb-2">{changeMode ? 'Current Password' : 'Password'}</label>
              <input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                className={inputClass}
                placeholder="Password"
              />
            </div>
            {changeMode && (
              <>
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">New Password</label>
                  <input
                    type="password"
                    value={newPassword}
                    onChange={(event) => setNewPassword(event.target.value)}
                    className={inputClass}
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
                    className={inputClass}
                    minLength={12}
                    required
                  />
                </div>
              </>
            )}

            {error && <div style={{ color: '#f87171', fontSize: '0.9rem' }}>{error}</div>}
            {info && <div style={{ color: '#6ee7b7', fontSize: '0.9rem' }}>{info}</div>}

            <button
              type="submit"
              disabled={submitting}
              className="w-full bg-blue-600 hover:bg-blue-500 text-white font-medium py-3 rounded-lg transition-colors shadow-[0_0_15px_rgba(59,130,246,0.3)] hover:shadow-[0_0_20px_rgba(59,130,246,0.5)]"
            >
              {submitting
                ? (changeMode ? 'Updating...' : 'Signing In...')
                : (changeMode ? 'Update Password' : 'Sign In')}
            </button>
            <button
              type="button"
              className="w-full text-sm text-slate-400 hover:text-slate-200"
              onClick={() => switchMode(!changeMode)}
            >
              {changeMode ? 'Back to sign in' : 'Change password'}
            </button>
          </form>
        </div>
      </div>
    </div>
  );
}
