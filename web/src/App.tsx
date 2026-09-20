import { useEffect, useState } from 'react';
import { BrowserRouter as Router, Routes, Route, Navigate } from 'react-router-dom';
import Login from './pages/Login';
import ChangePassword from './pages/ChangePassword';
import Dashboard from './pages/Dashboard';
import Connections from './pages/Connections';
import Applications from './pages/Applications';
import ApplicationDetail from './pages/ApplicationDetail';
import ManifestBuilder from './pages/ManifestBuilder';
import JobRuns from './pages/JobRuns';
import JobDetails from './pages/JobDetails';
import Settings from './pages/Settings';
import Discovery from './pages/Discovery';
import Users from './pages/Users';
import Layout from './components/Layout';
import { AUTH_EVENT, getAuthState, isAuthenticated, msUntilTokenExpiry } from './api';

function App() {
  const [authVersion, setAuthVersion] = useState(0);

  useEffect(() => {
    const onAuthChanged = () => setAuthVersion((v) => v + 1);
    window.addEventListener(AUTH_EVENT, onAuthChanged);
    window.addEventListener('storage', onAuthChanged);

    // Re-render (and so redirect to /login) the moment the stored token expires,
    // even if the user is sitting idle on a page.
    const remaining = msUntilTokenExpiry();
    const expiryTimer = remaining !== null
      ? window.setTimeout(onAuthChanged, Math.min(Math.max(remaining, 0) + 500, 2 ** 31 - 1))
      : undefined;

    return () => {
      window.removeEventListener(AUTH_EVENT, onAuthChanged);
      window.removeEventListener('storage', onAuthChanged);
      if (expiryTimer !== undefined) window.clearTimeout(expiryTimer);
    };
  }, [authVersion]);

  const authenticated = isAuthenticated();
  const auth = getAuthState();
  const mustChangePassword = !!auth?.mustChangePassword;

  return (
    <Router>
      <Routes>
        <Route path="/login" element={authenticated ? <Navigate to={mustChangePassword ? '/change-password' : '/'} replace /> : <Login />} />
        <Route path="/change-password" element={authenticated ? <ChangePassword /> : <Navigate to="/login" replace />} />
        
        <Route path="/" element={authenticated ? (mustChangePassword ? <Navigate to="/change-password" replace /> : <Layout />) : <Navigate to="/login" replace />}>
          <Route index element={<Dashboard />} />
          <Route path="connections" element={<Connections />} />
          <Route path="applications" element={<Applications />} />
          <Route path="applications/:id" element={<ApplicationDetail />} />
          <Route path="applications/:appId/manifests/:manifestId/builder" element={<ManifestBuilder />} />
          <Route path="discovery" element={<Discovery />} />
          <Route path="jobs" element={<JobRuns />} />
          <Route path="jobs/:id" element={<JobDetails />} />
          <Route path="users" element={<Users />} />
          <Route path="settings" element={<Settings />} />
        </Route>
      </Routes>
    </Router>
  );
}

export default App;
