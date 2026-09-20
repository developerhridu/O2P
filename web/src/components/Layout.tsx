import { Link, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { Database, Activity, Settings, LayoutDashboard, FolderTree, Users, LogOut, KeyRound, UserRound } from 'lucide-react';
import { getAuthState, hasRole, logout } from '../api';

export default function Layout() {
  const location = useLocation();
  const navigate = useNavigate();
  const auth = getAuthState();

  const navItems = [
    { name: 'Dashboard', path: '/', icon: <LayoutDashboard size={20} /> },
    { name: 'Databases', path: '/connections', icon: <Database size={20} /> },
    { name: 'Migrations', path: '/applications', icon: <FolderTree size={20} /> },
    { name: 'Runs', path: '/jobs', icon: <Activity size={20} /> },
    ...(hasRole('Admin') ? [{ name: 'Users', path: '/users', icon: <Users size={20} /> }] : []),
    { name: 'Settings', path: '/settings', icon: <Settings size={20} /> },
  ];

  const accountName = auth?.displayName || auth?.username || 'Signed in';
  const accountRoles = (auth?.roles || []).join(', ');

  return (
    <div className="app-container">
      <aside className="sidebar">
        {/* Each rail tile carries its name twice: aria-label is the accessible name and is
            always present, while .rail-label is the visible tooltip and is hidden from
            screen readers so the name is not announced twice. No title= as well - that
            would stack a slow native tooltip on top of the styled one. */}
        <Link to="/" className="rail-item" aria-label="O2P Migration Studio">
          <span className="text-gradient" style={{ fontWeight: 700, fontSize: '0.9rem', letterSpacing: '0.02em' }}>O2P</span>
          <span className="rail-label" aria-hidden="true">
            O2P Migration Studio
            <small>Copy data from Oracle to PostgreSQL</small>
          </span>
        </Link>

        <div className="rail-divider" />

        <nav>
          {navItems.map(item => {
            const isActive = location.pathname === item.path || (item.path !== '/' && location.pathname.startsWith(item.path));
            return (
              <Link
                key={item.path}
                to={item.path}
                className={`rail-item ${isActive ? 'is-active' : ''}`}
                aria-label={item.name}
                aria-current={isActive ? 'page' : undefined}
              >
                {item.icon}
                <span className="rail-label" aria-hidden="true">{item.name}</span>
              </Link>
            );
          })}
        </nav>

        <div className="rail-group" style={{ marginTop: 'auto' }}>
          <div className="rail-divider" />

          <div className="rail-item is-static">
            <UserRound size={20} />
            <span className="rail-label" aria-hidden="true">
              {accountName}
              {accountRoles && <small>{accountRoles}</small>}
            </span>
          </div>

          <button
            type="button"
            className="rail-item"
            aria-label="Change Password"
            onClick={() => navigate('/change-password')}
          >
            <KeyRound size={20} />
            <span className="rail-label" aria-hidden="true">Change Password</span>
          </button>

          <button
            type="button"
            className="rail-item"
            aria-label="Sign Out"
            onClick={() => {
              logout();
              navigate('/login');
            }}
          >
            <LogOut size={20} />
            <span className="rail-label" aria-hidden="true">Sign Out</span>
          </button>
        </div>
      </aside>

      <main className="main-content">
        <Outlet />
      </main>
    </div>
  );
}
