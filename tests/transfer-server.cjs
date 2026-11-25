const http = require('node:http');
const fs = require('node:fs');
const bytes = Buffer.alloc(1024 * 1024 + 333);
for (let i = 0; i < bytes.length; i++) bytes[i] = (i * 31 + 7) % 251;
const server = http.createServer((req, res) => {
  if (req.url === '/redirect') { res.writeHead(302, { Location: '/file' }); return res.end(); }
  if (req.url === '/429') { res.writeHead(429, { 'Retry-After': '60' }); return res.end('limited'); }
  if (req.url === '/html') { res.writeHead(200, { 'Content-Type': 'text/html' }); return res.end('<html>verify</html>'); }
  let start = 0, end = bytes.length - 1;
  const range = req.headers.range?.match(/^bytes=(\d+)-(\d+)$/);
  if (range && req.url !== '/plain') { start = +range[1]; end = +range[2]; }
  if (start > end || end >= bytes.length) { res.writeHead(416); return res.end(); }
  const partial = !!range && req.url !== '/plain';
  const headers = { 'Content-Type': 'application/octet-stream', 'Content-Length': end - start + 1,
    ETag: req.url === '/mutate' && end > 0 ? '"changed"' : '"fixture-v1"', 'Accept-Ranges': 'bytes' };
  if (partial) headers['Content-Range'] = `bytes ${req.url === '/wrong' && end > 0 ? start + 1 : start}-${end}/${bytes.length}`;
  res.writeHead(partial ? 206 : 200, headers);
  if (req.url === '/slow' && end > 0) {
    let cursor = start;
    const timer = setInterval(() => { if (cursor > end) { clearInterval(timer); res.end(); return; }
      res.write(bytes.subarray(cursor, Math.min(cursor + 1024, end + 1))); cursor += 1024; }, 30);
    res.on('close', () => clearInterval(timer)); return;
  }
  res.end(bytes.subarray(start, end + 1));
});
server.listen(0, '127.0.0.1', () => { fs.writeFileSync(process.argv[2], JSON.stringify({port: server.address().port})); console.log('ready'); });
