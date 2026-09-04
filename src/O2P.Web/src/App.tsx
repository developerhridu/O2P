import { BrowserRouter, Routes, Route } from 'react-router-dom';
import Layout from './components/Layout';
import Dashboard from './pages/Dashboard';
import Connections from './pages/Connections';
import Applications from './pages/Applications';
import ApplicationDetail from './pages/ApplicationDetail';
import ManifestBuilder from './pages/ManifestBuilder';
import JobRuns from './pages/JobRuns';
import JobDetails from './pages/JobDetails';
import Settings from './pages/Settings';
import './App.css'; 

function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<Layout />}>
          <Route index element={<Dashboard />} />
          <Route path="connections" element={<Connections />} />
          <Route path="applications" element={<Applications />} />
          <Route path="applications/:id" element={<ApplicationDetail />} />
          <Route path="applications/:appId/manifests/:manifestId/builder" element={<ManifestBuilder />} />
          <Route path="jobs" element={<JobRuns />} />
          <Route path="jobs/:id" element={<JobDetails />} />
          <Route path="settings" element={<Settings />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}

export default App;
