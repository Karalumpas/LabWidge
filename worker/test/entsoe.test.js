import { test } from "node:test";
import assert from "node:assert/strict";
import { parseRequest, entsoeTime, parsePrices, parseEcbRates, isComplete } from "../src/entsoe.js";

const now = Date.parse("2026-10-03T12:00Z");
const req = (q) => parseRequest(new URL(`https://w.test/v1/prices?${q}`), now);

test("a valid request is normalised", () => {
  const r = req("zone=be&from=2026-10-02T22:00Z&to=2026-10-04T22:00Z&currency=czk");
  assert.equal(r.zone, "BE");
  assert.equal(r.eic, "10YBE----------2");
  assert.equal(r.currency, "CZK");
  assert.equal(entsoeTime(r.from), "202610022200");
});

test("bad requests are rejected", () => {
  assert.match(req("zone=XX&from=2026-10-02T22:00Z&to=2026-10-03T22:00Z").error, /Unknown zone/);
  assert.match(req("zone=BE&from=2026-10-02T22:30Z&to=2026-10-03T22:00Z").error, /whole hours/);
  assert.match(req("zone=BE&from=2026-10-02T22:00Z&to=2026-10-06T22:00Z").error, /at most/);
  assert.match(req("zone=BE&from=2026-09-01T22:00Z&to=2026-09-02T22:00Z").error, /within/);
  assert.match(req("zone=BE&from=2026-10-02T22:00Z&to=2026-10-03T22:00Z&currency=euro").error, /three-letter/);
});

const doc = (series) => `<?xml version="1.0" encoding="UTF-8"?>
<Publication_MarketDocument xmlns="urn:iec62325.351:tc57wg16:451-3:publicationdocument:7:3">${series}</Publication_MarketDocument>`;
const period = (start, end, resolution, points) => `<TimeSeries><currency_Unit.name>EUR</currency_Unit.name><Period>
  <timeInterval><start>${start}</start><end>${end}</end></timeInterval><resolution>${resolution}</resolution>
  ${points.map(([p, v]) => `<Point><position>${p}</position><price.amount>${v}</price.amount></Point>`).join("")}
</Period></TimeSeries>`;

test("quarter-hours are read and left-out points repeat the previous price (A03)", () => {
  const prices = parsePrices(doc(period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT15M", [[1, 80.5], [3, 60]])));
  assert.deepEqual(prices.map((p) => p.eurPerMwh), [80.5, 80.5, 60, 60]);
  assert.equal(prices[1].start, Date.parse("2026-10-02T22:15Z"));
  assert.equal(prices[0].minutes, 15);
});

test("the finest resolution wins when series overlap", () => {
  const xml = doc(period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT60M", [[1, 99]])
                + period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT15M", [[1, 10], [2, 20], [3, 30], [4, 40]]));
  assert.deepEqual(parsePrices(xml).map((p) => p.eurPerMwh), [10, 20, 30, 40]);
});

test("only the day-ahead auction (contract type A01) is used", () => {
  const withContract = (type, price) => period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT15M", [[1, price]])
    .replace("<TimeSeries>", `<TimeSeries><contract_MarketAgreement.type>${type}</contract_MarketAgreement.type>`);
  const prices = parsePrices(doc(withContract("A07", 500) + withContract("A01", 70)));
  assert.deepEqual(prices.map((p) => p.eurPerMwh), [70, 70, 70, 70]);

  // An untyped series may be another auction: with an A01 series present, even a finer one is ignored
  const untypedQuarter = period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT15M", [[1, 900]]);
  const hourlyA01 = period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT60M", [[1, 70]])
    .replace("<TimeSeries>", "<TimeSeries><contract_MarketAgreement.type>A01</contract_MarketAgreement.type>");
  assert.deepEqual(parsePrices(doc(untypedQuarter + hourlyA01)).map((p) => p.eurPerMwh), [70]);
});

test("untyped series are used when no series is marked A01", () => {
  const prices = parsePrices(doc(period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT60M", [[1, 55]])));
  assert.deepEqual(prices.map((p) => p.eurPerMwh), [55]);
});

test("no matching data is an empty list, other acknowledgements are errors", () => {
  const ack = (text) => `<Acknowledgement_MarketDocument><Reason><code>999</code><text>${text}</text></Reason></Acknowledgement_MarketDocument>`;
  assert.deepEqual(parsePrices(ack("No matching data found for Data item Day-ahead Prices")), []);
  assert.throws(() => parsePrices(ack("Unauthorized. Missing or invalid security token")), /Unauthorized/);
});

test("ECB rates are parsed per euro", () => {
  const rates = parseEcbRates(`<Cube currency='CZK' rate='24.310'/><Cube currency="HUF" rate="390.15"/>`);
  assert.deepEqual(rates, { EUR: 1, CZK: 24.31, HUF: 390.15 });
});

test("a window is complete when the last period reaches its end", () => {
  const prices = parsePrices(doc(period("2026-10-02T22:00Z", "2026-10-02T23:00Z", "PT15M", [[1, 1], [2, 2], [3, 3], [4, 4]])));
  assert.equal(isComplete(prices, Date.parse("2026-10-02T23:00Z")), true);
  assert.equal(isComplete(prices, Date.parse("2026-10-03T22:00Z")), false);
  assert.equal(isComplete([], 0), false);
});
