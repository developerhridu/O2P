import { Link, Outlet, useLocation } from 'react-router-dom';
import { Database, Activity, Settings, LayoutDashboard, FolderTree } from 'lucide-react';

export default function Layout() {
  const location = useLocation();

  const navItems = [
    { name: 'Dashboard', path: '/', icon: <LayoutDashboard size={20} /> },
    { name: 'Connections', path: '/connections', icon: <Database size={20} /> },
    { name: 'Applications', path: '/applications', icon: <FolderTree size={20} /> },
    { name: 'Job Runs', path: '/jobs', icon: <Activity size={20} /> },
    { name: 'Settings', path: '/settings', icon: <Settings size={20} /> },
  ];

  return (
    <div className="app-container">
      <aside className="sidebar">
        <div style={{ marginBottom: '32px' }}>
          <h2 className="text-gradient" style={{ margin: 0 }}>O2P Engine</h2>
          <p style={{ fontSize: '0.85rem', margin: 0 }}>Data Migration Suite</p>
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
      </aside>

      <main className="main-content">
        <Outlet />
      </main>
    </div>
  );
}
