type Env = {
  ASSETS: Fetcher;
};

const SECURITY: Record<string, string> = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "strict-origin-when-cross-origin",
  "X-Frame-Options": "SAMEORIGIN",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
  "Cross-Origin-Opener-Policy": "same-origin",
};

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const res = await env.ASSETS.fetch(request);
    const headers = new Headers(res.headers);
    for (const [key, value] of Object.entries(SECURITY)) {
      headers.set(key, value);
    }
    if (url.hostname.endsWith(".workers.dev")) {
      headers.set("X-Robots-Tag", "noindex, nofollow");
    }
    if (url.pathname.startsWith("/assets/")) {
      headers.set("Cache-Control", "public, max-age=31536000, immutable");
    }
    return new Response(res.body, {
      status: res.status,
      statusText: res.statusText,
      headers,
    });
  },
} satisfies ExportedHandler<Env>;
