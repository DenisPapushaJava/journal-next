// Local fixture of the existing Velopack JSON feed contract. No installer is executed.
const http = require('http');
const crypto = require('crypto');
const bytes = Buffer.from('ARTIFICIAL WORKFLOW UPDATE PAYLOAD - NOT AN INSTALLER');
const hash = crypto.createHash('sha256').update(bytes).digest('hex').toUpperCase();
http.createServer((req, res) => {
  const path = new URL(req.url, 'http://localhost').pathname;
  const match = /^\/(own|foreign)\/releases\.workflow-win-x64\.json$/.exec(path);
  if (req.method === 'GET' && match) {
    const id = match[1] === 'own' ? 'DenisPapusha.JournalWorkflow' : 'DenisPapusha.Journal';
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify({ Assets: [{ PackageId: id, Version: '2026.1.0-alpha.2', Type: 'Full',
      FileName: `${id}-2026.1.0-alpha.2-full.nupkg`, SHA256: hash, Size: bytes.length,
      NotesMarkdown: '• Локальная проверка отдельного канала.' }] }));
  }
  if (req.method === 'GET' && path === '/own/DenisPapusha.JournalWorkflow-2026.1.0-alpha.2-full.nupkg') {
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': bytes.length });
    return res.end(bytes);
  }
  res.writeHead(501, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ error: 'Path is outside the tested feed contract' }));
}).listen(process.env.PORT || 4500);
