# Changelog

The top section is used as the release notes when the version is published. The heading must be the version number.

## 1.21.0
- Drag the shortcut rail to dock it on the right instead, or drop it between the sections to make it a section of its own – and drag it back again.
- The rail now sits centred in a slim tab that grows with each shortcut you add.
- Choose where the shortcuts go under Settings → Shortcuts, or right-click the rail.

## 1.20.0
- New: a rail of large icons on the left of the widget opens your favourite websites and programs with one click – YouTube, Instagram, ChatGPT, Discord or anything else.
- Add your shortcuts under Settings → Shortcuts: type a web address, pick a program or a Start menu shortcut, or start with the suggestions.
- Programs show their own Windows icon and websites their own logo; you can also choose your own icon.

## 1.19.0
- Electricity prices in 12 more countries: Belgium, France, Italy, Switzerland, Czechia, Slovakia, Hungary, Slovenia, Croatia, Romania, Bulgaria and Greece
- Italy has all 7 price zones to choose from, and prices are shown in your own currency – Kč, Ft, bani or Rp. where the euro isn't used

## 1.18.1
- Fixed: unticking a disk in Settings now actually hides it from the widget
- Fixed: choosing your country's electricity price after "Other" now includes the right VAT instead of 0 %
- Prices from the previous country are no longer shown in the new currency while switching country
- Drag a section sideways out of the widget to open it in its own window, and hover text no longer stays on top of other programs when the widget doesn't
- More reliable updates: LabWidge starts again on its own if Windows blocks a new version, and changed grid tariffs are fetched straight away

## 1.18.0
- Choose which plugins you use: enable or disable electricity, system, network, audio, Home Assistant, Cloudflare and Proxmox in Settings.
- Disabled plugins stop their background work to save resources, while keeping your settings and pinned window positions.
- Each plugin has its own settings page and compact, expanded and separate window views.
- The compact widget now shows an interactive summary for every visible plugin.

## 1.17.1
- Fixed: section windows no longer disappear when moving the mouse over buttons or showing hover text
- Dragging a section onto the desktop now pins its window even if the window is already open
- Windows pinned by dragging are saved immediately, so their position and open state are restored after restarting LabWidge

## 1.17.0
- A new, modern settings window in the widget's light or dark theme, with a menu of pages, cards and on/off switches
- New pages: General, Windows (restore pinned windows, forget a window's place), System and network, and About
- Choose which disks the widget shows, whether to show the graphics card and virtual network adapters, and what to measure ping against – e.g. your router
- Set how often Home Assistant, Proxmox and Cloudflare are updated
- Fixed: opening a section window could show an error dialog ("Cannot access a disposed object")

## 1.16.0
- Every section can open in its own window with much more detail – drag a section out of the widget onto the desktop, or click the window button in its header
- New windows for the electricity price (large chart with price scale, cheapest and most expensive periods), system (CPU, RAM and GPU history, disks, top processes), network (traffic and ping history, all adapters) and audio (every output and microphone with volume)
- Pin a window to keep it open: it remembers its place and size and opens again when LabWidge starts
- The Home Assistant, Cloudflare and Proxmox panels can now be pinned, moved and resized too

## 1.15.3
- Expanded sections can now be dragged past shorter ones – e.g. an open System section above Cloudflare – without collapsing them first

## 1.15.2
- Updates are now checked before they are installed: if Windows (Smart App Control) refuses to run the new version, your current version is kept instead of being replaced
- The installer shows a clear message instead of an error dialog when Windows blocks LabWidge

## 1.15.1
- The electricity price header now shows the full price area (e.g. "DK1" or "SE3") instead of cutting it off

## 1.15.0
- Electricity prices for 14 European countries: Denmark, Sweden, Norway, Finland, Estonia, Latvia, Lithuania, Germany, Luxembourg, Austria, the Netherlands, Poland, Spain and Portugal
- The installer asks for your country as well as your language – choose "Other country" to hide the electricity price
- Choose country, price area, VAT and your own add-on per kWh under Settings → Electricity
- Prices are shown in your country's unit (øre, öre, ct or gr)

## 1.14.0
- Clicking the pin now keeps a section where you are looking: it goes back to the side it was last pinned to, or the nearest edge of the widget
- The pin's hover text tells you beforehand whether the section will be pinned to the top or the bottom

## 1.13.0
- Every section header now has a pin you can click: one click pins the section to the top, another click unpins it
- More space between the icons on the section headers

## 1.12.0
- Hover texts now appear beside the widget instead of on top of it, so they never cover what you're looking at
- The hover box uses the widget's own colours and follows the light/dark theme

## 1.11.0
- The first public release of LabWidge – open source under the MIT license
- Available in English and Danish: choose the language when installing, or change it later under Settings → Widget
- Electricity price, system, network, audio, Home Assistant, Cloudflare and Proxmox in one widget by the clock
