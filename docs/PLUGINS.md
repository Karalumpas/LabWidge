# Internal plugins

LabWidge currently ships seven plugins as part of the application. No server, marketplace, external assembly loading,
account or payment mechanism is required. This is an internal extension contract, not a published third-party API.

## Responsibilities

`IWidgetPlugin` in `LabWidge/Plugins/WidgetPlugin.cs` defines a stable section key, localized name and description,
compact and expanded rendering, a detail-window factory, a settings-page factory and the plugin's polling jobs.
`WidgetPlugins.All` is the single registration list used by widget ordering, window support, settings navigation and polling.

The widget owns shared drawing helpers, layout, hit targets, scrolling, theme, drag-and-drop and section pins.
Section renderers live in `Plugins/*View.cs`; they currently share the `DashboardForm` drawing context through partial classes.
Detail windows inherit `PopupPanel`, which owns moving, resizing, pinning, focus handling and remembered bounds.
The bundled plugins reuse the existing services and detail panels so their data formats and interactions remain compatible.

## Activation and settings

The Sections page edits a cloned settings draft. Only Save applies changes to the running application; Cancel leaves it alone.
Only active plugins appear in settings navigation. Temporarily switching a plugin off and on retains its unsaved controls.
Disabled plugin pages are not validated or written when saving, so a disabled integration does not require credentials.

Activation is stored by section key in `AppSettings.Plugins`. Missing entries fall back to legacy preferences. In particular,
a configured Cloudflare integration remains active even if its section was hidden, preserving status monitoring.
Existing settings keys, credential storage, section order, pins and window state are preserved.

## Lifecycle

`PluginRuntime` owns a cancellation lifetime for each active plugin and a timer for each declared job. It starts no jobs for
disabled plugins. On settings changes it cancels the old generation, disposes timers, releases GPU counters and unsubscribes
network notifications. New workers wait for old scheduled work to finish before starting; each worker skips overlapping ticks.
Cancellation flows through price sources and tariffs, Home Assistant requests, Proxmox requests, Cloudflare DNS and tunnel
requests, external IP lookups, service checks and ping. Cancellation does not count as a service outage.
Native headset reads already in progress finish within their normal device timeout; canceled results are discarded and no new
read is scheduled. Plugin job failures are logged and do not stop other plugins.

System, network, Home Assistant and Proxmox sampling is skipped when neither their widget section nor detail window is visible.
Cloudflare monitoring/DNS and electricity alerts keep running while the application is hidden, provided the plugin is active.
Opening a detail window or showing the widget refreshes the relevant workers without starting disabled plugins.

Deactivation closes a plugin's windows while retaining the saved pinned/open state. Re-enabling restores pinned windows when
the user's restore-windows preference allows it. User-closed windows remain closed.

## Adding a bundled plugin

1. Implement `WidgetPlugin` with a unique permanent key and English/Danish labels.
2. Supply compact/expanded views, a `PopupPanel` detail window and a `SettingsPage`.
3. Declare polling jobs in `CreateJobs`; pass their cancellation token through every asynchronous request and delay.
4. Register the plugin in `WidgetPlugins.All`. Unknown keys default to inactive, so the user chooses when to enable it.
5. Keep configuration across deactivation; avoid timers, subscriptions or data collection in constructors.
6. Add tests for activation, cancellation, reactivation, settings isolation and all three views.

`Tests/Ui/PluginChecks.cs` covers activation, retained pinned windows, settings navigation and drafts, worker cancellation,
rapid reactivation, stopped timers, failure isolation and compact/expanded views. Regression tests cover legacy settings
and cancellation of price requests before subsequent tariff fetching.

A future distribution server can build on these keys and descriptors. Download/install manifests, version compatibility,
authentication and paid access should be designed separately from this bundled-plugin implementation.
