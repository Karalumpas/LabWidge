// LabWidge price Worker: day-ahead prices for every European bidding zone from the ENTSO-E Transparency Platform.
// The app calls it for countries without a free keyless source, so users need no ENTSO-E token of their own.
//
//   GET /v1/prices?zone=BE&from=2026-10-02T22:00Z&to=2026-10-04T22:00Z&currency=EUR
//   → { zone, currency, source, prices: [{ start: "2026-10-02T22:00:00.000Z", minutes: 15, price: 0.0921 }] }
//
// price is in `currency` per kWh excluding VAT. Answers are cached, so ENTSO-E is asked a few times a day per zone.
// The token is the secret ENTSOE_TOKEN (`npx wrangler secret put ENTSOE_TOKEN`) and is never returned.

import { parseRequest, entsoeTime, parsePrices, parseEcbRates, isComplete } from "./entsoe.js";

const ENTSOE = "https://web-api.tp.entsoe.eu/api";
const ECB = "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml";
const COMPLETE_TTL = 12 * 3600; // the whole window is published – it will not change
const PARTIAL_TTL = 10 * 60;     // tomorrow may still arrive

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    if (request.method !== "GET") return json({ error: "Only GET is supported" }, 405);
    if (url.pathname === "/" || url.pathname === "/health") return json({ ok: true, service: "labwidge-prices" });
    if (url.pathname !== "/v1/prices") return json({ error: "Not found" }, 404);

    const req = parseRequest(url);
    if (req.error) return json({ error: req.error }, 400);

    // One cache entry per zone, window and currency – the same for every user in that zone
    const key = new Request(`https://cache.labwidge/v1/prices/${req.zone}/${req.from}/${req.to}/${req.currency}`);
    const cache = caches.default;
    const cached = await cache.match(key);
    if (cached) return cached;

    try {
      if (!env.ENTSOE_TOKEN) return json({ error: "The Worker has no ENTSOE_TOKEN yet" }, 503);
      const prices = await fetchPrices(env.ENTSOE_TOKEN, req);
      const rate = req.currency === "EUR" ? 1 : (await ecbRates(ctx))[req.currency];
      if (!rate) return json({ error: `No ECB exchange rate for ${req.currency}` }, 400);

      const body = {
        zone: req.zone,
        currency: req.currency,
        source: "ENTSO-E Transparency Platform",
        prices: prices.map((p) => ({
          start: new Date(p.start).toISOString(),
          minutes: p.minutes,
          price: Math.round((p.eurPerMwh / 1000) * rate * 1e6) / 1e6,
        })),
      };
      const ttl = isComplete(prices, req.to) ? COMPLETE_TTL : PARTIAL_TTL;
      const response = json(body, 200, ttl);
      ctx.waitUntil(cache.put(key, response.clone()));
      return response;
    } catch (err) {
      console.error(`${req.zone}: ${err.message}`);
      return json({ error: "The price source could not be reached – try again later" }, 502);
    }
  },
};

async function fetchPrices(token, req) {
  const params = new URLSearchParams({
    securityToken: token,
    documentType: "A44",
    in_Domain: req.eic,
    out_Domain: req.eic,
    periodStart: entsoeTime(req.from),
    periodEnd: entsoeTime(req.to),
  });
  const res = await fetch(`${ENTSOE}?${params}`, { headers: { "User-Agent": "labwidge-prices" } });
  const text = await res.text();
  // "No matching data" comes back as an acknowledgement, sometimes with HTTP 200, sometimes 400
  if (!res.ok && !text.includes("Acknowledgement_MarketDocument")) throw new Error(`HTTP ${res.status}`);
  return parsePrices(text).filter((p) => p.start >= req.from && p.start < req.to);
}

async function ecbRates(ctx) {
  const key = new Request("https://cache.labwidge/ecb-rates");
  const cached = await caches.default.match(key);
  if (cached) return cached.json();
  const res = await fetch(ECB);
  if (!res.ok) throw new Error(`ECB HTTP ${res.status}`);
  const rates = parseEcbRates(await res.text());
  ctx.waitUntil(caches.default.put(key, json(rates, 200, 6 * 3600)));
  return rates;
}

function json(body, status = 200, maxAge = 0) {
  return new Response(JSON.stringify(body), {
    status,
    headers: {
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": maxAge > 0 ? `public, max-age=${maxAge}` : "no-store",
    },
  });
}
