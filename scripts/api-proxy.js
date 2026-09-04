const http = require('http');

const listenPort = Number(process.env.O2P_PROXY_PORT || 3052);
const targetHost = process.env.O2P_TARGET_HOST || '127.0.0.1';
const targetPort = Number(process.env.O2P_TARGET_PORT || 5050);

function writeCorsHeaders(res) {
  res.setHeader('access-control-allow-origin', '*');
  res.setHeader('access-control-allow-methods', 'GET,POST,PUT,PATCH,DELETE,OPTIONS');
  res.setHeader('access-control-allow-headers', 'authorization,content-type');
}

const server = http.createServer((req, res) => {
  writeCorsHeaders(res);

  if (req.method === 'OPTIONS') {
    res.writeHead(204);
    res.end();
    return;
  }

  const upstream = http.request(
    {
      hostname: targetHost,
      port: targetPort,
      path: req.url,
      method: req.method,
      headers: {
        ...req.headers,
        host: `${targetHost}:${targetPort}`,
      },
    },
    (upstreamRes) => {
      const headers = {
        ...upstreamRes.headers,
        'access-control-allow-origin': '*',
        'access-control-allow-methods': 'GET,POST,PUT,PATCH,DELETE,OPTIONS',
        'access-control-allow-headers': 'authorization,content-type',
      };
      res.writeHead(upstreamRes.statusCode || 502, headers);
      upstreamRes.pipe(res);
    },
  );

  upstream.on('error', (error) => {
    res.writeHead(502, { 'content-type': 'text/plain' });
    res.end(`O2P API proxy error: ${error.message}`);
  });

  req.pipe(upstream);
});

server.listen(listenPort, '0.0.0.0', () => {
  console.log(`O2P API proxy listening on 0.0.0.0:${listenPort}, forwarding to ${targetHost}:${targetPort}`);
});
