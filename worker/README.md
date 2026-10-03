# labwidge-prices

A small Cloudflare Worker that serves day-ahead electricity prices for every European bidding zone to LabWidge,
from the [ENTSO-E Transparency Platform](https://transparency.entsoe.eu/). LabWidge uses it for countries without a free
keyless price source, so users need no ENTSO-E account. The ENTSO-E token stays in the Worker and is never returned.

```
GET /v1/prices?zone=BE&from=2026-10-02T22:00Z&to=2026-10-04T22:00Z&currency=EUR
```

```json
{ "zone": "BE", "currency": "EUR", "source": "ENTSO-E Transparency Platform",
  "prices": [{ "start": "2026-10-02T22:00:00.000Z", "minutes": 15, "price": 0.0921 }] }
```

- `zone` – a code from [`src/zones.js`](src/zones.js), e.g. `BE`, `FR`, `IT-NORD`, `CZ`.
- `from`/`to` – whole hours in UTC, at most 72 hours apart.
- `currency` – optional; prices are converted from EUR with the ECB's daily reference rates (e.g. `CZK`, `HUF`, `CHF`).
- `price` – per kWh, excluding VAT. Tomorrow's prices are an empty list until they are published (around 13:00 CET).

Answers are cached per zone and window – 12 hours once complete, 10 minutes while tomorrow may still arrive –
so ENTSO-E is asked a few times a day per zone, however many people use the app. `GET /health` answers `{"ok":true}`.

## Deploy

1. Get an ENTSO-E API token: register at transparency.entsoe.eu, email transparency@entsoe.eu with the subject
   "Restful API access", then generate the token under *My Account Settings → Web API Security Token*.
2. From this folder:

   ```bash
   npx wrangler login
   npx wrangler deploy
   npx wrangler secret put ENTSOE_TOKEN
   ```

   `deploy` prints the Worker's address, e.g. `https://labwidge-prices.<account>.workers.dev`.

## Develop

```bash
npm test                 # unit tests of the parsing (node --test)
npx wrangler dev         # run locally; add ENTSOE_TOKEN=... to a .dev.vars file to fetch real prices
```
