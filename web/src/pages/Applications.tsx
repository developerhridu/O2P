import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { Plus, Trash2, X } from 'lucide-react';
import { API_BASE, apiFetch, getHeaders } from '../api';

export default function Applications() {
  const [apps, setApps] = useState<any[]>([]);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [isSubmitting, setIsSubmitting] = useState(false);

  const fetchApps = () => {
    fetch(`${API_BASE}/applications`, { headers: getHeaders() })
      .then(r => r.json())
      .then(setApps)
      .catch(console.error);
  };

  useEffect(() => {
    fetchApps();
  }, []);

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) return;
    setIsSubmitting(true);

    try {
      const response = await apiFetch(`${API_BASE}/applications`, {
        method: 'POST',
        headers: getHeaders(),
        body: JSON.stringify({ name, description }),
      });

      if (response.ok) {
        setIsModalOpen(false);
        setName('');
        setDescription('');
        fetchApps();
      } else {
        console.error('Could not create the migration');
      }
    } catch (error) {
      console.error('Error creating the migration:', error);
    } finally {
      setIsSubmitting(false);
    }
  };

  const handleDelete = async (id: number, appName: string) => {
    if (!confirm(`Delete the migration "${appName}"? This also deletes its table selections, settings and run history.`)) {
      return;
    }

    try {
      const response = await apiFetch(`${API_BASE}/applications/${id}`, {
        method: 'DELETE',
        headers: getHeaders(),
      });

      if (response.ok) {
        fetchApps();
      } else {
        console.error('Could not delete the migration');
      }
    } catch (error) {
      console.error('Error deleting the migration:', error);
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex justify-between items-center">
        <h1 className="text-3xl font-bold tracking-tight">Migrations</h1>
        <button 
          onClick={() => setIsModalOpen(true)}
          className="flex items-center gap-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white rounded-lg transition-colors font-medium shadow-lg shadow-blue-900/20 cursor-pointer"
        >
          <Plus size={20} />
          New Migration
        </button>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
        {apps.map(app => (
          <div key={app.id} className="bg-slate-950 border border-slate-800 rounded-xl overflow-hidden group hover:border-slate-700 transition-colors">
            <div className="p-6">
              <div className="flex justify-between items-start mb-4">
                <h3 className="text-xl font-semibold">{app.name}</h3>
                <div className="flex items-center space-x-2">
                  <button 
                    onClick={() => handleDelete(app.id, app.name)}
                    className="text-slate-500 hover:text-rose-400 p-1 rounded hover:bg-slate-900 transition-colors cursor-pointer"
                    title="Delete migration"
                  >
                    <Trash2 size={18} />
                  </button>
                </div>
              </div>
              <p className="text-slate-400 text-sm mb-6 line-clamp-2 h-10">
                {app.description || "No description provided."}
              </p>
              
              <Link 
                to={`/applications/${app.id}`}
                className="block w-full py-2 px-4 bg-slate-900 hover:bg-slate-800 text-center rounded-lg border border-slate-700 text-sm font-medium transition-colors"
              >
                Manage
              </Link>
            </div>
          </div>
        ))}
        {apps.length === 0 && (
          <div className="col-span-full py-12 text-center text-slate-500 bg-slate-900/50 rounded-xl border border-slate-800 border-dashed">
            No migrations set up yet.
          </div>
        )}
      </div>

      {/* New migration modal */}
      {isModalOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm p-4">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-md p-6 shadow-2xl relative">
            <button 
              onClick={() => setIsModalOpen(false)}
              className="absolute top-4 right-4 text-slate-400 hover:text-slate-200 cursor-pointer"
            >
              <X size={20} />
            </button>
            <h2 className="text-xl font-semibold mb-6">New migration</h2>
            <form onSubmit={handleCreate} className="space-y-4">
              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">Migration name</label>
                <input 
                  type="text" 
                  required
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm"
                  placeholder="e.g. Core Banking System"
                />
              </div>
              <div>
                <label className="block text-sm font-medium text-slate-300 mb-2">Description</label>
                <textarea 
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  className="w-full bg-slate-950 border border-slate-800 rounded-lg px-4 py-2.5 text-slate-100 placeholder-slate-600 focus:outline-none focus:ring-2 focus:ring-blue-500/50 focus:border-blue-500 transition-all text-sm h-24 resize-none"
                  placeholder="What is being moved, and why"
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
                  {isSubmitting ? 'Creating...' : 'Create'}
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}
