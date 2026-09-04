import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { Database, Activity, Settings, LayoutDashboard, FolderTree, Users, LogOut, KeyRound } from 'lucide-react';
import { getAuthState, hasRole, logout } from '../api';

export default function Layout() {
  const location = useLocation();
  const navigate = useNavigate();
  const auth = getAuthState();

  const navItems = [
    { name: 'Dashboard', path: '/', icon: <LayoutDashboard size={20} /> },
    { name: 'Connections', path: '/connections', icon: <Database size={20} /> },
    { name: 'Applications', path: '/applications', icon: <FolderTree size={20} /> },
    { name: 'Job Runs', path: '/jobs', icon: <Activity size={20} /> },
    ...(hasRole('Admin') ? [{ name: 'Users', path: '/users', icon: <Users size={20} /> }] : []),
    { name: 'Settings', path: '/settings', icon: <Settings size={20} /> },
  ];

  return (
    <div className="app-container">
      <aside className="sidebar">
        <div style={{ marginBottom: '32px' }}>
          <h2 className="text-gradient" style={{ margin: 0 }}>O2P Migration Studio</h2>
          <p style={{ fontSize: '0.85rem', margin: 0 }}>Oracle to PostgreSQL Control Center</p>
        </div>
        
        <nav className="flex-col gap-2">
          {navItems.map(item => {
            const isActive = location.pathname === item.path || (item.path !== '/' && location.pathname.startsWith(item.path));
            return (
              <Link
                key={item.path}
                to={item.path}
                className={`btn ${isActive ? '' : 'btn-secondary'}`}
                style={{ justifyContent: 'flex-start', padding: '10px 16px' }}
              >
                {item.icon}
                <span>{item.name}</span>
              </Link>
            );
          })}
        </nav>

        <div style={{ marginTop: 'auto', display: 'flex', flexDirection: 'column', gap: '10px' }}>
          <div style={{ padding: '12px 4px', borderTop: '1px solid rgba(148, 163, 184, 0.12)' }}>
            <div style={{ fontSize: '0.8rem', color: '#cbd5e1', fontWeight: 600 }}>
              {auth?.displayName || auth?.username}
            </div>
            <div style={{ fontSize: '0.75rem', color: '#64748b' }}>
              {(auth?.roles || []).join(', ')}
            </div>
          </div>
          <button
            className="btn btn-secondary"
            style={{ justifyContent: 'flex-start', padding: '10px 16px' }}
            onClick={() => navigate('/change-password')}
          >
            <KeyRound size={18} />
            <span>Change Password</span>
          </button>
          <button
            className="btn btn-secondary"
            style={{ justifyContent: 'flex-start', padding: '10px 16px' }}
            onClick={() => {
              logout();
              navigate('/login');
            }}
          >
            <LogOut size={18} />
            <span>Sign Out</span>
          </button>
        </div>
      </aside>

      <main className="main-content">
        <Outlet />
      </main>
    </div>
  );
}
