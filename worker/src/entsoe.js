// Pure helpers for the price Worker: request validation and parsing of ENTSO-E and ECB responses.
// Kept free of Worker APIs so they run under `node --test`.

import { ZONES } from "./zones.js";

const HOUR = 3600_000;
export const MAX_SPAN_HOURS = 72;

/**
 * Validates /v1/prices?zone=&from=&to=&currency= and returns the normalised request,
 * or { error } with a message for a 400 response.
 */
export function parseRequest(url, now = Date.now()) {
  const q = url.searchParams;
  const zone = (q.get("zone") || "").toUpperCase();
  if (!ZONES[zone]) return { error: `Unknown zone "${zone}". Known zones: ${Object.keys(ZONES).join(", ")}` };

  const from = Date.parse(q.get("from") || "");
  const to = Date.parse(q.get("to") || "");
  if (Number.isNaN(from) || Number.isNaN(to)) return { error: "from and to must be ISO 8601 times in UTC, e.g. 2026-10-02T22:00Z" };
  if (from % HOUR !== 0 || to % HOUR !== 0) return { error: "from and to must be whole hours" };
  if (to <= from || to - from > MAX_SPAN_HOURS * HOUR) return { error: `to must be after from and at most ${MAX_SPAN_HOURS} hours later` };
  if (from < now - 8 * 24 * HOUR || from > now + 3 * 24 * HOUR) return { error: "from must be within the last week or the next 3 days" };

  const currency = (q.get("currency") || "EUR").toUpperCase();
  if (!/^[A-Z]{3}$/.test(currency)) return { error: "currency must be a three-letter ISO code" };
  return { zone, eic: ZONES[zone], from, to, currency };
}

/** ENTSO-E wants yyyyMMddHHmm in UTC. */
export function entsoeTime(ms) {
  return new Date(ms).toISOString().replace(/[-:T]/g, "").slice(0, 12);
}

const tag = (xml, name) => {
  const m = xml.match(new RegExp(`<${name}>([^<]*)</${name}>`));
  return m ? m[1].trim() : null;
};
const blocks = (xml, name) => xml.match(new RegExp(`<${name}>[\\s\\S]*?</${name}>`, "g")) || [];

const RESOLUTIONS = { PT15M: 15, PT30M: 30, PT60M: 60, P1D: 1440 };

/**
 * Parses a Publication_MarketDocument (documentType A44) into [{ start (ms), minutes, eurPerMwh }].
 * - Points left out (curve type A03) repeat the previous price until the next point.
 * - When several series cover the same time (e.g. hourly and quarter-hourly), the finest resolution wins.
 * - Series for other auctions than day-ahead (contract type other than A01) are skipped.
 * An Acknowledgement_MarketDocument ("no matching data") gives an empty list.
 */
export function parsePrices(xml) {
  if (xml.includes("Acknowledgement_MarketDocument")) {
    const reason = tag(xml, "text") || "";
    if (/no matching data/i.test(reason)) return [];
    throw new Error(`ENTSO-E: ${reason || "request rejected"}`);
  }
  const best = new Map();
  for (const series of blocks(xml, "TimeSeries")) {
    const currency = tag(series, "currency_Unit.name");
    if (currency && currency !== "EUR") continue;
    // A44 returns every auction when contract_MarketAgreement.type is not filtered – keep the day-ahead one (A01)
    const contract = tag(series, "contract_MarketAgreement.type");
    if (contract && contract !== "A01") continue;
    for (const period of blocks(series, "Period")) {
      const start = Date.parse(tag(period, "start"));
      const end = Date.parse(tag(period, "end"));
      const minutes = RESOLUTIONS[tag(period, "resolution")];
      if (Number.isNaN(start) || Number.isNaN(end) || !minutes) continue;

      const points = new Map();
      for (const point of blocks(period, "Point")) {
        const position = Number(tag(point, "position"));
        const price = Number(tag(point, "price.amount"));
        if (Number.isFinite(position) && Number.isFinite(price)) points.set(position, price);
      }
      const count = Math.round((end - start) / (minutes * 60_000));
      let last = null;
      for (let position = 1; position <= count; position++) {
        if (points.has(position)) last = points.get(position);
        if (last === null) continue;
        const t = start + (position - 1) * minutes * 60_000;
        const existing = best.get(t);
        if (!existing || minutes < existing.minutes) best.set(t, { start: t, minutes, eurPerMwh: last });
      }
    }
  }
  return [...best.values()].sort((a, b) => a.start - b.start);
}

/** ECB reference rates: { CZK: 24.3, ... } per 1 EUR. */
export function parseEcbRates(xml) {
  const rates = { EUR: 1 };
  for (const m of xml.matchAll(/currency=['"]([A-Z]{3})['"]\s+rate=['"]([\d.]+)['"]/g)) rates[m[1]] = Number(m[2]);
  return rates;
}

/** Whether the prices reach the end of the requested window, so the answer will not change. */
export function isComplete(prices, to) {
  if (prices.length === 0) return false;
  const last = prices[prices.length - 1];
  return last.start + last.minutes * 60_000 >= to;
}
