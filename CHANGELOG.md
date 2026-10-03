# Changelog

The top section is used as the release notes when the version is published. The heading must be the version number.
LabWidge was called IpTrayWidget before version 1.11.

## 1.10.2
- A new build of 1.10.1: if the widget would not start after the last update because Windows blocked it, install this version

## 1.10.1
- Fixed an error in the right-click menu that showed an error message when you pinned or unpinned a section

## 1.10.0
- Pin sections to the top or bottom by right-clicking the header – the rest scrolls beneath them
- Choose a compact summary for pinned sections; when space runs short they are shown compactly automatically
- Adjust the widget's width and height by dragging its edges and corners – the size is remembered
- Move sections with the visible drag handle, and use the arrow to open or close the details
- See the update time and clear markers when data is being fetched, is outdated or the connection fails

## 1.9.2
- Automatic updates work again: the installer releases the program folder that older widgets kept locked during the update

## 1.9.1
- Cloudflare only changes the chosen addresses – an empty host list no longer changes all A records
- A failed Cloudflare update is retried automatically, also when your IP address has not changed
- Changing grid company and price area now goes through even if prices are already being fetched
- Updates keep the previous installation until the new version is in place, and can be restored after an interruption
- Uninstalling waits for the widget to close before the program files are deleted

## 1.9.0
- See the battery of your wireless headset right on the audio button – the button fills up like a battery, and you are notified when it is running low
- Mute and unmute the microphone with one click in the widget, and scroll over an audio button or the microphone to turn the volume up and down
- When you switch to the headset, the microphone follows – and back to e.g. the webcam when you switch away again
- The widget no longer jumps when all sections are expanded: it becomes as tall as the screen, and the rest scrolls with the mouse wheel
- Everything can be set under Settings → Audio: battery colour, notifications, microphone per device and volume step

## 1.8.2
- The widget can be dragged freely around the desktop again – it got stuck at the screen edge
- The bottom of the widget stays put when you collapse or expand a section, and it is in the same place after a restart

## 1.8.1
- The widget now always starts the installed version when you sign in to Windows – also if an older copy had set its own path

## 1.8.0
- New Proxmox section: see CPU, RAM and storage on your server, which VMs and containers are running, and start, shut down or reboot them with a click
- The widget now checks that the addresses behind your Cloudflare Tunnels respond, and tells you if a service stops responding
- Right-click an address in the Cloudflare panel to turn its check off, e.g. for services you switched off on purpose
- The widget now uses about half as much CPU while it is hidden
- The panels can be scrolled when they are taller than the screen
- Reorder the sections: grab a header and drag it up or down. The widget also snaps to the screen edges when you move it

## 1.7.0
- New Cloudflare section in the widget: see at a glance whether your Cloudflare Tunnels are up and whether your A records point to your current IP
- Click for details: which addresses each tunnel forwards and where to, all A records and links straight to Cloudflare
- Get notified when a tunnel goes down – and when it is up again (can be turned off under Notifications)
- Update the A records with one click right from the widget
- Tunnels require your Cloudflare token to also have the permission Account → Cloudflare Tunnel → Read

## 1.6.2
- Tokens for Home Assistant and Cloudflare are now remembered, also when you restart or sign out. After the update you need to paste them once more

## 1.6.1
- You are no longer signed out of the Home Assistant panel when the app updates
- If you turn automatic installation off, an update that was already downloaded is not installed either
- Headphones you connect later show up in the widget even if you saved other settings
- The default audio device is remembered, also while it is disconnected
- Home Assistant entities that were deleted can be removed again in the settings
- Updates are only installed if the file can be verified, and uninstalling also deletes the Home Assistant login

## 1.6.0
- Switch audio output with one click at the bottom of the widget – e.g. between headphones and the monitor's speakers. Choose devices, names and a default device under Settings → Audio
- Updates are now downloaded and installed by themselves when the PC is not in use (can be turned off under Settings → Widget)
- After an update you get a message about what's new
- Updates are also checked when the PC wakes from sleep

## 1.5.1
- Updates are now fetched from the public releases repository, so all installations can see new versions
- Graphics card in the widget: load (with a chart) and VRAM
- NVIDIA cards also show temperature, power draw and fan speed

## 1.5.0
- Graphics card in the widget: load, VRAM and NVIDIA sensors

## 1.4.0
- Home Assistant panel (optional, off by default)
- Automatic updates through GitHub Releases
- Reorganised setup guide and settings
