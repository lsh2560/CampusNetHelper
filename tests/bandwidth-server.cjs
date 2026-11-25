const http = require('node:http');
const fs = require('node:fs');
const block = Buffer.alloc(32768);
for (let i = 0; i < block.length; i++) block[i] = (i * 31 + 17) % 251;
let acceptedUpload = 0, active = 0, uploads = 0;
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  if (url.pathname === '/stats') { res.setHeader('Content-Type', 'application/json'); return res.end(JSON.stringify({ acceptedUpload, active, uploads })); }
  active++; res.on('close', () => active--);
  if (url.pathname.includes('/limited/')) { res.writeHead(429, { 'Retry-After': '60' }); return res.end('limited'); }
  if (url.pathname.includes('/html/')) { res.writeHead(200, { 'Content-Type': 'text/html' }); return res.end('<html>sign in</html>'); }
  if (url.pathname.endsWith('/__down')) {
    const bytes = +url.searchParams.get('bytes');
    if (!(bytes > 0 && bytes <= 64 * 1024 * 1024)) { res.writeHead(400); return res.end(); }
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': url.pathname.includes('/wrong/') ? bytes - 1 : bytes, 'Cache-Control': 'no-store' });
    if (url.pathname.includes('/stall/')) return;
    let sent = 0;
    const limit = url.pathname.includes('/wrong/') ? bytes - 1 : bytes;
    const timer = setInterval(() => {
      const n = Math.min(block.length, limit - sent);
      res.write(block.subarray(0, n)); sent += n;
      if (sent >= limit) { clearInterval(timer); res.end(); }
    }, url.pathname.includes('/slow/') ? 90 : 4);
    res.on('close', () => clearInterval(timer)); return;
  }
  if (url.pathname.endsWith('/__up') && req.method === 'POST') {
    uploads++;
    if (url.pathname.includes('/reject-upload/')) { res.writeHead(403); return res.end('rejected'); }
    let bytes = 0;
    req.on('data', data => { bytes += data.length; });
    req.on('end', () => {
      acceptedUpload += bytes;
      const delay = url.pathname.includes('/unacked/') ? 4000 : url.pathname.includes('/slow/') ? 850 : 200;
      const timer = setTimeout(() => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify({ received: bytes })); }, delay);
      res.on('close', () => clearTimeout(timer));
    }); return;
  }
  res.writeHead(404); res.end();
});
server.listen(0, '127.0.0.1', () => { fs.writeFileSync(process.argv[2], JSON.stringify({ port: server.address().port })); console.log('ready'); });
