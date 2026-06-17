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

    if (url.pathname === '/stars' && request.method === 'GET') {
      const stars = await env.STARS.get('stars', 'json') || {};
      return new Response(JSON.stringify(stars), {
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      });
    }

    if (url.pathname === '/star' && request.method === 'POST') {
      let body;
      try {
        body = await request.json();
      } catch {
        return new Response(JSON.stringify({ error: 'Invalid JSON' }), {
          status: 400,
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
        });
      }

      const { oppId, username, passphrase } = body;

      if (!env.PASSPHRASE || passphrase !== env.PASSPHRASE) {
        return new Response(JSON.stringify({ error: 'Invalid passphrase' }), {
          status: 403,
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
        });
      }

      if (!oppId || !username) {
        return new Response(JSON.stringify({ error: 'Missing oppId or username' }), {
          status: 400,
          headers: { ...corsHeaders, 'Content-Type': 'application/json' },
        });
      }

      const stars = await env.STARS.get('stars', 'json') || {};
      const users = stars[oppId] || [];

      const idx = users.indexOf(username);
      if (idx >= 0) {
        users.splice(idx, 1);
      } else {
        users.push(username);
      }

      if (users.length === 0) {
        delete stars[oppId];
      } else {
        stars[oppId] = users;
      }

      await env.STARS.put('stars', JSON.stringify(stars));

      return new Response(JSON.stringify({ stars }), {
        headers: { ...corsHeaders, 'Content-Type': 'application/json' },
      });
    }

    return new Response('Not found', { status: 404, headers: corsHeaders });
  }
};
