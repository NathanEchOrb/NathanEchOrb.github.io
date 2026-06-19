export default {
  async fetch(request, env) {
    const corsHeaders = {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Methods': 'GET, POST, OPTIONS',
      'Access-Control-Allow-Headers': 'Content-Type',
    };

    if (request.method === 'OPTIONS') {
      return new Response(null, { headers: corsHeaders });
    }

    const url = new URL(request.url);
    const jsonHeaders = { ...corsHeaders, 'Content-Type': 'application/json' };

    if (url.pathname === '/votes' && request.method === 'GET') {
      const [stars, downvotes] = await Promise.all([
        env.STARS.get('stars', 'json'),
        env.STARS.get('downvotes', 'json'),
      ]);
      return new Response(JSON.stringify({
        stars: stars || {},
        downvotes: downvotes || {},
      }), { headers: jsonHeaders });
    }

    // Legacy endpoint — still works
    if (url.pathname === '/stars' && request.method === 'GET') {
      const [stars, downvotes] = await Promise.all([
        env.STARS.get('stars', 'json'),
        env.STARS.get('downvotes', 'json'),
      ]);
      return new Response(JSON.stringify({
        stars: stars || {},
        downvotes: downvotes || {},
      }), { headers: jsonHeaders });
    }

    if ((url.pathname === '/star' || url.pathname === '/downvote') && request.method === 'POST') {
      let body;
      try {
        body = await request.json();
      } catch {
        return new Response(JSON.stringify({ error: 'Invalid JSON' }), {
          status: 400, headers: jsonHeaders,
        });
      }

      const { oppId, username, passphrase } = body;

      if (!env.PASSPHRASE || passphrase !== env.PASSPHRASE) {
        return new Response(JSON.stringify({ error: 'Invalid passphrase' }), {
          status: 403, headers: jsonHeaders,
        });
      }

      if (!oppId || !username) {
        return new Response(JSON.stringify({ error: 'Missing oppId or username' }), {
          status: 400, headers: jsonHeaders,
        });
      }

      const kvKey = url.pathname === '/star' ? 'stars' : 'downvotes';
      const data = await env.STARS.get(kvKey, 'json') || {};
      const users = data[oppId] || [];

      const idx = users.indexOf(username);
      if (idx >= 0) {
        users.splice(idx, 1);
      } else {
        users.push(username);
      }

      if (users.length === 0) {
        delete data[oppId];
      } else {
        data[oppId] = users;
      }

      await env.STARS.put(kvKey, JSON.stringify(data));

      const [stars, downvotes] = await Promise.all([
        kvKey === 'stars' ? data : (await env.STARS.get('stars', 'json') || {}),
        kvKey === 'downvotes' ? data : (await env.STARS.get('downvotes', 'json') || {}),
      ]);

      return new Response(JSON.stringify({ stars, downvotes }), {
        headers: jsonHeaders,
      });
    }

    return new Response('Not found', { status: 404, headers: corsHeaders });
  }
};
