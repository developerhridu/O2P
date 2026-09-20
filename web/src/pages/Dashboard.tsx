export default function Dashboard() {
  return (
    <div className="space-y-6">
      <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
          <h3 className="text-slate-400 text-sm font-medium">Runs in progress</h3>
          <p className="text-3xl font-semibold mt-2 text-slate-100">0</p>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
          <h3 className="text-slate-400 text-sm font-medium">Total copied (GB)</h3>
          <p className="text-3xl font-semibold mt-2 text-slate-100">0.00</p>
        </div>
        <div className="bg-slate-900 border border-slate-800 rounded-xl p-6 shadow-sm">
          <h3 className="text-slate-400 text-sm font-medium">Overall speed</h3>
          <p className="text-3xl font-semibold mt-2 text-blue-400">0 MB/s</p>
        </div>
      </div>
      
      <div className="bg-slate-900 border border-slate-800 rounded-xl p-8 text-center mt-8">
        <h3 className="text-xl font-medium text-slate-200">Nothing running</h3>
        <p className="text-slate-400 mt-2">Open a migration to start a run.</p>
      </div>
    </div>
  );
}
