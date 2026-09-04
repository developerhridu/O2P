import { useState, useEffect } from 'react';
import { Plus, Database, CheckCircle, XCircle, X, RefreshCw } from 'lucide-react';
import { API_BASE, apiFetch, getHeaders } from '../api';

export default function Connections() {
  const [connections, setConnections] = useState<any[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  
  // Modal states
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [editingConn, setEditingConn] = useState<any>(null);
  
  // Form states
  const [name, setName] = useState('');
  const [kind, setKind] = useState('postgres'); // 'postgres' or 'oracle'
  const [host, setHost] = useState('');
  const [port, setPort] = useState(5432);
  const [serviceOrDb, setServiceOrDb] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [optionsJson, setOptionsJson] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);

  // Connection testing states
  const [testingStatus, setTestingStatus] = useState<Record<number, { 
    loading: boolean; 
    success?: boolean; 
    error?: string;
    latencyMs?: number;
    serverVersion?: string;
    privileges?: any[];
  }>>({});

  const fetchConnections = async () => {
    setIsLoading(true);
    try {
      const response = await apiFetch(`${API_BASE}/connections`, { headers: getHeaders() });
      if (response.ok) {
        const data = await response.json();
        setConnections(data);
      } else {
        console.error('Failed to fetch connections');
      }
    } catch (err) {
      console.error('Error fetching connections:', err);
    } finally {
      setIsLoading(false);
    }
  };

  useEffect(() => {
    fetchConnections();
  }, []);

  const openCreateModal = () => {
    setEditingConn(null);
    setName('');
    setKind('postgres');
    setHost('');
    setPort(5432);
    setServiceOrDb('');
    setUsername('');
    setPassword('');
    setOptionsJson('');
    setIsModalOpen(true);
  };

  const openEditModal = (conn: any) => {
    setEditingConn(conn);
    setName(conn.name);
    setKind(conn.kind === 0 ? 'oracle' : 'postgres');
    setHost(conn.host);
    setPort(conn.port);
    setServiceOrDb(conn.serviceOrDb);
    setUsername(conn.username);
    setPassword(''); // Don't prefill password for security
    setOptionsJson(conn.optionsJson || '');
    setIsModalOpen(true);
  };

  // Toggle port default value when database kind changes
  const handleKindChange = (newKind: string) => {
    setKind(newKind);
    if (newKind === 'oracle' && port === 5432) {
      setPort(1521);
    } else if (newKind === 'postgres' && port === 1521) {
      setPort(5432);
    }
  };

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim() || !host.trim()) return;
    setIsSubmitting(true);

    const payload = {
      name,
      kind,
      host,
      port,
      serviceOrDb,
      username,
      password,
      optionsJson: optionsJson.trim() ? optionsJson : null
    };

    try {
      let response;
      if (editingConn) {
        // Edit connection
        response = await apiFetch(`${API_BASE}/connections/${editingConn.id}`, {
          method: 'PUT',
          headers: getHeaders(),
          body: JSON.stringify(payload)
        });
      } else {
        // Create connection
        response = await apiFetch(`${API_BASE}/connections`, {
          method: 'POST',
          headers: getHeaders(),
          body: JSON.stringify(payload)
        });
      }

      if (response.ok) {
        setIsModalOpen(false);
        fetchConnections();
      } else {
        let message = '';
        try {
          const data = await response.json();
          message = typeof data === 'string' ? data : data?.message || '';
        } catch {
          // empty or non-JSON body
        }
        if (!message) {
          message =
            response.status === 401 || response.status === 403
              ? 'You do not have permission to do this (Admin role required).'
              : `Request failed with status ${response.status}.`;
        }
        alert(`Failed to save connection: ${message}`);
      }
    } catch (err) {
      console.error('Error saving connection:', err);
      alert('An error occurred while saving the connection.');
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleDelete = async (id: number, connName: string) => {
    if (!confirm(`Are you sure you want to delete connection "${connName}"?`)) {
      return;
    }

    try {
      const response = await apiFetch(`${API_BASE}/connections/${id}`, {
        method: 'DELETE',
        headers: getHeaders()
      });

      if (response.ok) {
        fetchConnections();
      } else {
        console.error('Failed to delete connection');
      }
    } catch (err) {
      console.error('Error deleting connection:', err);
    }
  };

  const handleTest = async (id: number) => {
    setTestingStatus(prev => ({
      ...prev,
      [id]: { loading: true }
    }));

    try {
      const response = await apiFetch(`${API_BASE}/connections/${id}/test`, {
        method: 'POST',
        headers: getHeaders()
      });

      if (response.ok) {
        const result = await response.json();
        if (result.success === false) {
          setTestingStatus(prev => ({
            ...prev,
            [id]: {
              loading: false,
              success: false,
              error: result.message || 'Connection test failed.'
            }
          }));
          return;
        }

        setTestingStatus(prev => ({
          ...prev,
          [id]: {
            loading: false,
            success: true,
            latencyMs: result.latencyMs,
            serverVersion: result.serverVersion,
            privileges: result.privileges
          }
        }));
      } else {
        const errorData = await response.json().catch(() => ({ message: 'Unknown connection failure' }));
        setTestingStatus(prev => ({
          ...prev,
          [id]: {
            loading: false,
            success: false,
            error: errorData.message || 'Connection test failed.'
          }
        }));
      }
    } catch (err) {
      setTestingStatus(prev => ({
        ...prev,
        [id]: {
          loading: false,
          success: false,
          error: 'Network error communicating with the API.'
        }
      }));
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h2 className="text-xl font-medium text-slate-200">Connection Profiles</h2>
        <button 
          onClick={openCreateModal}
          className="bg-blue-600 hover:bg-blue-500 text-white font-medium py-2 px-4 rounded-lg flex items-center space-x-2 transition-colors shadow-[0_0_10px_rgba(59,130,246,0.2)] cursor-pointer"
        >
          <Plus size={18} />
          <span>New Connection</span>
        </button>
      </div>
      
      {isLoading ? (
        <div className="py-12 text-center text-slate-500">Loading connection profiles...</div>
      ) : (
        <div className="bg-slate-900 border border-slate-800 rounded-xl overflow-hidden shadow-sm">
          <table className="w-full text-left">
            <thead className="bg-slate-950/50 border-b border-slate-800 text-slate-400 text-sm">
              <tr>
                <th className="px-6 py-4 font-medium">Name</th>
                <th className="px-6 py-4 font-medium">Kind</th>
                <th className="px-6 py-4 font-medium">Host</th>
                <th className="px-6 py-4 font-medium">Database/Service</th>
                <th className="px-6 py-4 font-medium">Status / Info</th>
                <th className="px-6 py-4 font-medium text-right">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-800 text-slate-300">
              {connections.map((conn) => {
                const test = testingStatus[conn.id];
                return (
                  <tr key={conn.id} className="hover:bg-slate-800/30 transition-colors">
                    <td className="px-6 py-4 font-medium text-slate-200">{conn.name}</td>
                    <td className="px-6 py-4">
                      <span className={`inline-flex items-center space-x-1.5 px-2.5 py-1 rounded-md text-xs font-medium border ${
                        conn.kind === 0 
                          ? 'bg-rose-500/10 text-rose-400 border-rose-500/20' 
                          : 'bg-blue-500/10 text-blue-400 border-blue-500/20'
                      }`}>
                        <Database size={12} />
                        <span>{conn.kind === 0 ? 'Oracle' : 'PostgreSQL'}</span>
                      </span>
                    </td>
                    <td className="px-6 py-4 font-mono text-sm">{conn.host}:{conn.port}</td>
                    <td className="px-6 py-4 font-mono text-sm">{conn.serviceOrDb}</td>
                    <td className="px-6 py-4">
                      {test ? (
                        test.loading ? (
                          <span className="flex items-center text-slate-400 text-sm animate-pulse">
                            <RefreshCw size={14} className="mr-1.5 animate-spin" />
                            Testing...
                          </span>
                        ) : test.success ? (
                          <div className="space-y-1">
                            <span className="flex items-center text-emerald-400 text-sm font-medium">
                              <CheckCircle size={14} className="mr-1.5" />
                              Online ({test.latencyMs}ms)
                            </span>
                            <div className="text-xs text-slate-500 truncate max-w-[200px]" title={test.serverVersion}>
                              {test.serverVersion}
                            </div>
                          </div>
                        ) : (
                          <div className="space-y-1">
                            <span className="flex items-center text-rose-400 text-sm font-medium" title={test.error}>
                              <XCircle size={14} className="mr-1.5" />
                              Failed
                            </span>
                            <div className="text-xs text-rose-500/80 truncate max-w-[200px]" title={test.error}>
                              {test.error}
                            </div>
                          </div>
                        )
                      ) : (
                        <span className="flex items-center text-slate-500 text-sm">
                          <XCircle size={14} className="mr-1.5" />
                          Untested
                        </span>
                      )}
                    </td>
                    <td className="px-6 py-4 text-right space-x-3">
                      <button 
                        onClick={() => handleTest(conn.id)}
                        disabled={test?.loading}
                        className="text-blue-400 hover:text-blue-300 text-sm font-medium transition-colors cursor-pointer disabled:opacity-50"
                      >
                        Test
                      </button>
                      <span className="text-slate-700">|</span>
                      <button 
                        onClick={() => openEditModal(conn)}
                        className="text-slate-400 hover:text-slate-200 text-sm font-medium transition-colors cursor-pointer"
                      >
                        Edit
                      </button>
                      <span className="text-slate-700">|</span>
                      <button 
                        onClick={() => handleDelete(conn.id, conn.name)}
                        className="text-rose-400 hover:text-rose-300 text-sm font-medium transition-colors cursor-pointer"
                      >
                        Delete
                      </button>
                    </td>
                  </tr>
                );
              })}
              {connections.length === 0 && (
                <tr>
                  <td colSpan={6} className="px-6 py-8 text-center text-slate-500 bg-slate-900/50">
                    No connection profiles created yet. Click "New Connection" to add one.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}

      {/* New/Edit Connection Modal */}
      {isModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-lg p-6 shadow-2xl relative max-h-[90vh] overflow-y-auto">
            <button 
              onClick={() => setIsModalOpen(false)}
              className="absolute top-4 right-4 text-slate-400 hover:text-slate-200 cursor-pointer"
            >
              <X size={20} />
            </button>
            <h2 className="text-xl font-semibold mb-6">
              {editingConn ? `Edit Connection: ${editingConn.name}` : 'Create Connection Profile'}
            </h2>
            <form onSubmit={handleSave} className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Profile Name</label>
                  <input 
                    type="text" 
                    required
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                    placeholder="e.g. Oracle Production"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Database Engine</label>
                  <select 
                    value={kind}
                    onChange={(e) => handleKindChange(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                  >
                    <option value="postgres">PostgreSQL</option>
                    <option value="oracle">Oracle Database</option>
                  </select>
                </div>
              </div>

              <div className="grid grid-cols-3 gap-4">
                <div className="col-span-2">
                  <label className="block text-sm font-medium text-slate-300 mb-2">Host Address / IP</label>
                  <input 
                    type="text" 
                    required
                    value={host}
                    onChange={(e) => setHost(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                    placeholder="e.g. db.internal or 10.0.1.5"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Port</label>
                  <input 
                    type="number" 
                    required
                    value={port}
                    onChange={(e) => setPort(parseInt(e.target.value) || 0)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                  />
                </div>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">
                    {kind === 'oracle' ? 'Service Name / SID' : 'Database Name'}
                  </label>
                  <input 
                    type="text" 
                    required
                    value={serviceOrDb}
                    onChange={(e) => setServiceOrDb(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                    placeholder={kind === 'oracle' ? 'ORCL or service_name' : 'postgres'}
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium text-slate-300 mb-2">Username</label>
                  <input 
                    type="text" 
                    required
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                    placeholder="db_user"
                  />
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">
                  Password {editingConn && <span className="text-slate-500">(Leave blank to keep current)</span>}
                </label>
                <input 
                  type="password" 
                  required={!editingConn}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                  placeholder="••••••••"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">Connection Options (JSON - Optional)</label>
                <textarea 
                  value={optionsJson}
                  onChange={(e) => setOptionsJson(e.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm h-16 resize-none"
                  placeholder='e.g. { "MaxPoolSize": 100, "SSL": true }'
                />
              </div>

              <div className="flex space-x-3 pt-4">
                <button 
                  type="button"
                  onClick={() => setIsModalOpen(false)}
                  className="flex-1 py-2 px-4 bg-slate-800 hover:bg-slate-700 text-slate-300 text-sm font-medium rounded-lg transition-colors cursor-pointer"
                >
                  Cancel
                </button>
                <button 
                  type="submit"
                  disabled={isSubmitting}
                  className="flex-1 py-2 px-4 bg-blue-600 hover:bg-blue-500 text-white text-sm font-medium rounded-lg transition-colors shadow-lg shadow-blue-500/20 disabled:opacity-50 cursor-pointer"
                >
                  {isSubmitting ? 'Saving...' : 'Save Profile'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
