export default function Discovery() {
  return (
    <div className="flex-col gap-6">
      <header>
        <h1>Database Discovery</h1>
        <p>Scan source Oracle instances and construct target manifests.</p>
      </header>
      
      <div className="glass-panel mt-4">
        <h3>Connect & Scan</h3>
        <div className="flex-col gap-4 mt-4" style={{ maxWidth: '400px' }}>
          <input className="input-field" placeholder="Oracle Connection String" />
          <input className="input-field" type="password" placeholder="Password" />
          <button className="btn" style={{ width: 'fit-content' }}>Run Discovery</button>
        </div>
      </div>
    </div>
  );
}
