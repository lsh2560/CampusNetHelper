const http = require('node:http');
const fs = require('node:fs');
const counts = {};
http.createServer((req, res) => {
  const path = new URL(req.url, 'http://127.0.0.1').pathname;
  if (path === '/stats') { res.setHeader('Content-Type', 'application/json'); return res.end(JSON.stringify(counts)); }
  counts[path] = (counts[path] || 0) + 1;
  if (path.startsWith('/429')) {
    const headers = path === '/429-seconds' ? { 'Retry-After': '120' } : path === '/429-date' ? { 'Retry-After': new Date(Date.now() + 90000).toUTCString() } : path === '/429-invalid' ? { 'Retry-After': 'not-a-date' } : {};
    res.writeHead(429, headers); return res.end('rate limited');
  }
  res.writeHead(path === '/503' ? 503 : 200, { 'Content-Type': 'text/html' }); res.end('<a href="pip.whl">pip</a>');
}).listen(0, '127.0.0.1', function () { fs.writeFileSync(process.argv[2], JSON.stringify({ port: this.address().port })); });
