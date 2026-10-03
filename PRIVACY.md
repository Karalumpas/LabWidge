# Privacy

LabWidge runs entirely on your own computer. It has no user accounts, no analytics and no telemetry,
and it does not send usage data or personal data to the author or to anyone else.

The program only contacts the network services below, and only for the purpose described.

## Always (default features)

| Service | Purpose | Data sent |
|---|---|---|
| [Energi Data Service](https://www.energidataservice.dk/) (`api.energidataservice.dk`) | Electricity spot prices and grid tariffs | Price area and grid company chosen in the settings |
| `api.ipify.org`, `ipv4.icanhazip.com`, `ifconfig.me` | Show your external IP address | A normal web request (your IP address is visible to the service, as with any website) |
| [GitHub](https://github.com/) (`api.github.com`, `github.com`) | Check for and download updates | A normal web request; can be turned off under Settings → Widget |

The network section also pings a public DNS server to measure latency.

## Only if you set it up

| Service | Purpose | Data sent |
|---|---|---|
| [Cloudflare](https://www.cloudflare.com/privacypolicy/) (`api.cloudflare.com`) | Update your DNS A-records and show tunnel status | Your API token, zone ID and current IP address |
| Your own Home Assistant server | Show and control the entities you selected | Your long-lived access token |
| Your own Proxmox server | Show and control your virtual machines | Your API token |
| Microsoft (`aka.ms`, during installation) | Download the .NET Desktop Runtime if it is missing | A normal web request, only after you agree |

Tokens are stored in Windows Credential Manager. Settings are stored in `%APPDATA%\LabWidge`. Nothing is
uploaded anywhere else. The privacy policies of the third-party services above apply to the requests sent to them.
