export default function Discovery() {
  return (
    <div className="flex-col gap-6">
      <header>
        <h1>Scan a database</h1>
        <p>Look at a source Oracle database and list its tables.</p>
      </header>
      
      <div className="glass-panel mt-4">
        <h3>Connect and scan</h3>
        <div className="flex-col gap-4 mt-4" style={{ maxWidth: '400px' }}>
          <input className="input-field" placeholder="Oracle Connection String" />
          <input className="input-field" type="password" placeholder="Password" />
          <button className="btn" style={{ width: 'fit-content' }}>Scan</button>
        </div>
      </div>
    </div>
  );
}
