# Privacy

LabWidge runs entirely on your own computer. It has no user accounts, no analytics and no telemetry,
and it does not send usage data or personal data to the author or to anyone else.

The program only contacts the network services below, and only for the purpose described.

## Always (default features)

| Service | Purpose | Data sent |
|---|---|---|
| The electricity price source for your country (see below) | Electricity spot prices, and in Denmark grid tariffs | The price area and, in Denmark, the grid company chosen in the settings |
| `api.ipify.org`, `ipv4.icanhazip.com`, `ifconfig.me` | Show your external IP address | A normal web request (your IP address is visible to the service, as with any website) |
| [GitHub](https://github.com/) (`api.github.com`, `github.com`) | Check for and download updates | A normal web request; can be turned off under Settings → Widget |

The network section also pings a public DNS server to measure latency.

Electricity price sources – only the one for the country chosen in the settings is contacted, and none if you choose "Other country":
Denmark `api.energidataservice.dk`, Sweden `www.elprisetjustnu.se`, Norway `www.hvakosterstrommen.no`,
Finland and the Baltics `dashboard.elering.ee`, Germany, Luxembourg and Austria `api.awattar.de` / `api.awattar.at`,
the Netherlands `api.energyzero.nl`, Poland `api.raporty.pse.pl`, Spain and Portugal `www.omie.es`, and the other countries
LabWidge's own price service `labwidge-prices.karalumpas.workers.dev` – a Cloudflare Worker that receives only the price area,
the time window and the currency (and, like any website, sees your IP address), and fetches the prices from ENTSO-E with its own token.

## Only if you set it up

| Service | Purpose | Data sent |
|---|---|---|
| [Cloudflare](https://www.cloudflare.com/privacypolicy/) (`api.cloudflare.com`) | Update your DNS A-records and show tunnel status | Your API token, zone ID and current IP address |
| Your own Home Assistant server | Show and control the entities you selected | Your long-lived access token |
| Your own Proxmox server | Show and control your virtual machines | Your API token |
| The websites you add as shortcuts | Fetch the site's icon once (it is then kept in `%LOCALAPPDATA%\LabWidgeData\icons`) | A normal web request to the site |
| DuckDuckGo (`icons.duckduckgo.com`) | The icon of a shortcut whose website does not give one | The site's host name, e.g. `chatgpt.com` |
| Microsoft (`aka.ms`, during installation) | Download the .NET Desktop Runtime if it is missing | A normal web request, only after you agree |

Tokens are stored in Windows Credential Manager. Settings are stored in `%APPDATA%\LabWidge`. Nothing is
uploaded anywhere else. The privacy policies of the third-party services above apply to the requests sent to them.
