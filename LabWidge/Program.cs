using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The installer starts the new version with --probe before it replaces the old one,
        // to find out whether Windows (Smart App Control) allows it to run
        if (args.Length == 1 && args[0] == "--probe") return;

        if (args.Length == 2 && args[0] == "--write-icon")
        {
            WidgetIcon.WriteIco(args[1]);
            return;
        }

        if (args.Contains("--uninstall"))
        {
            L.Use(SettingsStore.Load().Language ?? L.FromWindows());
            ApplicationConfiguration.Initialize();
            SelfInstaller.Uninstall();
            return;
        }

        if (SelfInstaller.RedirectToInstalledCopy(args))
        {
            return;
        }

        // After a language change the old instance is still closing, so a restart waits a moment for it
        using var mutex = new Mutex(false, "Local\\LabWidge");
        if (!AcquireSingleInstance(mutex, args.Contains(RestartArgument) ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            return; // Already running
        }

        SelfInstaller.RegisterUninstallEntry();
        ApplicationConfiguration.Initialize();
        // An error in a click or a timer must not stop the widget with .NET's crash dialog: log it and carry on
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Logger.Error($"Unexpected error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error($"Unexpected error (fatal): {e.ExceptionObject}");
        Application.Run(new TrayAppContext(forceSetup: args.Contains("--setup")));
    }

    public const string RestartArgument = "--restart";

    /// <summary>
    /// The old instance never releases the mutex, so when it exits while we wait, Windows hands it over as
    /// abandoned. That still means we own it now.
    /// </summary>
    private static bool AcquireSingleInstance(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>Starts a new instance and closes this one – used when the language changes.</summary>
    public static void Restart()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = RestartArgument,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not restart: {ex.Message}");
            return;
        }
        Application.Exit();
    }
}

internal sealed class PopupForm : Form
{
    private static PopupForm? _instance;
    private static Label? _statusLabel;
    private bool _suppressDeactivateClose;
    private readonly AppSettings _settings;
    private readonly DataGridView _dnsGrid;
    private readonly string _currentIp;

    private PopupForm(string ip, string timestamp, AppSettings settings, Action onCopy, Func<Task> onUpdate, Action onCloudflare, Action onSettings, Func<Task> onUpdateCloudflare)
    {
        _settings = settings;
        _currentIp = ip;
        Text = "LabWidge";
        Icon = AppIconProvider.GetIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Font;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.White;
        ClientSize = new Size(500, 380);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(12, 6)
        };

        // --- Tab 1: Overview ---
        var tabOverview = new TabPage(L.T("Overview", "Oversigt"));
        tabOverview.BackColor = Color.White;

        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = Color.FromArgb(30, 40, 52) };
        var ipTitle = new Label
        {
            Text = L.T("YOUR IP ADDRESS", "DIN IP-ADRESSE") + $"  (v{Application.ProductVersion.Split('+')[0]})",
            ForeColor = Color.FromArgb(150, 160, 170),
            Location = new Point(20, 15),
            AutoSize = true,
            Font = new Font("Segoe UI", 8F, FontStyle.Bold)
        };
        var ipDisplay = new Label
        {
            Text = ip,
            ForeColor = Color.White,
            Location = new Point(18, 35),
            AutoSize = true,
            Font = new Font("Segoe UI", 20F, FontStyle.Bold)
        };
        headerPanel.Controls.AddRange(new Control[] { ipTitle, ipDisplay });

        var timeLabel = new Label
        {
            Text = L.T("Last updated: ", "Sidst opdateret: ") + timestamp,
            AutoSize = true,
            ForeColor = Color.Gray,
            Location = new Point(20, 100)
        };

        var btnCopy = CreateButton(L.T("Copy IP", "Kopiér IP"), 20, 140, onCopy);
        var btnUpdate = CreateButtonAsync(L.T("Refresh IP now", "Opdatér IP nu"), 130, 140, onUpdate);
        var btnSettings = CreateButton(L.T("Settings", "Indstillinger"), 240, 140, () =>
        {
            _suppressDeactivateClose = true;
            Hide();
            onSettings();
            Close();
        });

        var btnCf = CreateButton(L.T("Open Cloudflare", "Åbn Cloudflare"), 20, 180, onCloudflare);
        var btnUpdateCf = CreateButtonAsync(L.T("Run auto-update", "Kør auto-opdatering"), 130, 180, onUpdateCloudflare);

        _statusLabel = new Label
        {
            Text = L.T("Ready.", "Klar."),
            AutoSize = false,
            Dock = DockStyle.Bottom,
            Height = 30,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(20, 80, 40),
            Padding = new Padding(10, 0, 0, 0),
            BackColor = Color.FromArgb(245, 247, 250)
        };

        tabOverview.Controls.AddRange(new Control[] { headerPanel, timeLabel, btnCopy, btnUpdate, btnSettings, btnCf, btnUpdateCf, _statusLabel });

        // --- Tab 2: Cloudflare DNS ---
        var tabDns = new TabPage(L.T("DNS records", "DNS-records"));
        tabDns.BackColor = Color.White;

        var dnsHeader = new Panel { Dock = DockStyle.Top, Height = 45, BackColor = Color.White };
        var btnRefresh = CreateButton(L.T("Refresh", "Opfrisk"), 10, 8, async () => await LoadDnsRecords());
        btnRefresh.Width = 100;
        dnsHeader.Controls.Add(btnRefresh);

        _dnsGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            GridColor = Color.FromArgb(240, 240, 240)
        };
        _dnsGrid.Columns.Add("Type", "Type");
        _dnsGrid.Columns["Type"].Width = 60;
        _dnsGrid.Columns.Add("Name", "Host");
        _dnsGrid.Columns.Add("Content", L.T("Content", "Indhold"));
        _dnsGrid.Columns.Add("Proxied", "Proxy");

        var btnCol = new DataGridViewButtonColumn
        {
            Name = "Action",
            HeaderText = L.T("Action", "Handling"),
            Text = L.T("Update", "Opdatér"),
            UseColumnTextForButtonValue = true
        };
        _dnsGrid.Columns.Add(btnCol);

        _dnsGrid.CellClick += async (s, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == _dnsGrid.Columns["Action"].Index)
            {
                var row = _dnsGrid.Rows[e.RowIndex];
                var recordId = row.Tag as string;
                var recordName = row.Cells["Name"].Value?.ToString() ?? L.T("unknown", "ukendt");

                if (recordId != null)
                {
                    await UpdateSingleRecord(recordId, recordName);
                }
            }
        };

        // Load the records when the tab is selected
        tabs.SelectedIndexChanged += async (s, e) =>
        {
            if (tabs.SelectedTab == tabDns)
            {
                await LoadDnsRecords();
            }
        };

        // Docking follows reverse z-order: the header is added last so it takes the top strip and the grid fills the rest
        tabDns.Controls.Add(_dnsGrid);
        tabDns.Controls.Add(dnsHeader);

        tabs.TabPages.Add(tabOverview);
        tabs.TabPages.Add(tabDns);

        Controls.Add(tabs);

        Deactivate += (_, _) =>
        {
            if (_suppressDeactivateClose) return;
            Close();
        };
    }

    private Button CreateButton(string text, int x, int y, Action onClick)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 245, 255);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private Button CreateButtonAsync(string text, int x, int y, Func<Task> onClick)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(100, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 245, 255);
        btn.Click += async (_, _) => 
        {
            btn.Enabled = false;
            try { await onClick(); }
            finally { btn.Enabled = true; }
        };
        return btn;
    }

    public static void ShowPopup(string ip, string timestamp, AppSettings settings, Action onCopy, Func<Task> onUpdate, Action onCloudflare, Action onSettings, Func<Task> onUpdateCloudflare)
    {
        _instance?.Close();
        _instance = new PopupForm(ip, timestamp, settings, onCopy, onUpdate, onCloudflare, onSettings, onUpdateCloudflare);

        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor);
        var x = Math.Min(cursor.X, screen.WorkingArea.Right - _instance.Width - 10);
        var y = Math.Min(cursor.Y, screen.WorkingArea.Bottom - _instance.Height - 10);
        _instance.Location = new Point(Math.Max(screen.WorkingArea.Left + 10, x),
                                       Math.Max(screen.WorkingArea.Top + 10, y));

        _instance.Show();
        _instance.Activate();
    }

    private async Task LoadDnsRecords()
    {
        _dnsGrid.Rows.Clear();
        var token = CredentialStore.ReadToken();
        if (string.IsNullOrWhiteSpace(_settings.ZoneId) || string.IsNullOrWhiteSpace(token))
        {
             _dnsGrid.Rows.Add(L.T("Not set up", "Mangler opsætning"), "", "", "");
             return;
        }

        try
        {
            var records = await CloudflareClient.GetRecordsAsync(_settings.ZoneId, token, "A,CNAME");
            foreach (var r in records)
            {
                var actionText = r.Type == "A" ? L.T("Update IP", "Opdatér IP") : L.T("Edit", "Redigér");
                var rowIndex = _dnsGrid.Rows.Add(r.Type, r.Name, r.Content, (r.Proxied ?? false) ? "Proxied" : "DNS Only", actionText);
                _dnsGrid.Rows[rowIndex].Tag = r.Id; // Store ID for update
                
                // Highlight if different (only for A records matching current IP logic)
                if (r.Type == "A" && !string.Equals(r.Content, _currentIp, StringComparison.Ordinal))
                {
                     _dnsGrid.Rows[rowIndex].DefaultCellStyle.BackColor = Color.FromArgb(255, 245, 235); // Slight orange
                }
            }
        }
        catch (Exception ex)
        {
            _dnsGrid.Rows.Add(L.T("Error: ", "Fejl: ") + ex.Message, "", "", "");
        }
    }

    private async Task UpdateSingleRecord(string recordId, string recordName)
    {
        var token = CredentialStore.ReadToken();
        if (string.IsNullOrWhiteSpace(_settings.ZoneId) || string.IsNullOrWhiteSpace(token)) return;

        try
        {
            SetStatus(L.T($"Updating {recordName}...", $"Opdaterer {recordName}..."), StatusLevel.Info);
            
            // We need full record info, simpler to fetch again or store it. 
            // For now, let's just fetch everything to find the one we want to be safe.
            
            var records = await CloudflareClient.GetRecordsAsync(_settings.ZoneId, token, "A,CNAME");
            var record = records.FirstOrDefault(r => r.Id == recordId);
            
            if (record != null)
            {
                if (record.Type == "A")
                {
                    await CloudflareClient.UpdateRecordAsync(_settings.ZoneId, token, recordId, record, _currentIp);
                    SetStatus(L.T($"{recordName} updated!", $"{recordName} opdateret!"), StatusLevel.Success);
                }
                else
                {
                   SetStatus(L.T("Only A records can be updated automatically.", "Kun A-records kan opdateres automatisk."), StatusLevel.Warning);
                }
                await LoadDnsRecords(); // Refresh list
            }
        }
        catch (Exception ex)
        {
            SetStatus(L.T("Error: ", "Fejl: ") + ex.Message, StatusLevel.Error);
        }
    }

    public static void SetStatus(string message, StatusLevel level = StatusLevel.Info)
    {
        if (_statusLabel == null || _instance == null || _instance.IsDisposed)
        {
            return;
        }

        if (_instance.InvokeRequired)
        {
            _instance.BeginInvoke(new Action(() => SetStatus(message)));
            return;
        }

        _statusLabel.Text = message;
        _statusLabel.ForeColor = level switch
        {
            StatusLevel.Success => Color.Green,
            StatusLevel.Warning => Color.Orange,
            StatusLevel.Error => Color.Red,
            _ => Color.Black
        };
    }
}

internal enum StatusLevel
{
    Info,
    Success,
    Warning,
    Error
}

internal static class CredentialStore
{
    private const string CloudflareTarget = "LabWidge.CloudflareToken";
    private const string HomeAssistantTarget = "LabWidge.HomeAssistantToken";
    private const string ProxmoxTarget = "LabWidge.ProxmoxToken";

    public static void WriteToken(string token) => Write(CloudflareTarget, token);
    public static string? ReadToken() => Read(CloudflareTarget);
    public static void DeleteToken() => Delete(CloudflareTarget);

    public static void WriteHomeAssistantToken(string token) => Write(HomeAssistantTarget, token);
    public static string? ReadHomeAssistantToken() => Read(HomeAssistantTarget);
    public static void DeleteHomeAssistantToken() => Delete(HomeAssistantTarget);

    public static void WriteProxmoxSecret(string secret) => Write(ProxmoxTarget, secret);
    public static string? ReadProxmoxSecret() => Read(ProxmoxTarget);
    public static void DeleteProxmoxSecret() => Delete(ProxmoxTarget);

    private static void Write(string targetName, string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var cred = new CREDENTIAL
        {
            Type = CRED_TYPE.GENERIC,
            TargetName = targetName,
            CredentialBlobSize = (uint)bytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(bytes.Length),
            Persist = CRED_PERSIST.LOCAL_MACHINE
        };

        try
        {
            Marshal.Copy(bytes, 0, cred.CredentialBlob, bytes.Length);
            if (!CredWrite(ref cred, 0))
            {
                throw new InvalidOperationException(L.T("Could not save the token in Credential Manager.", "Kunne ikke gemme token i Credential Manager."));
            }
        }
        finally
        {
            if (cred.CredentialBlob != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(cred.CredentialBlob);
            }
        }
    }

    private static string? Read(string targetName)
    {
        if (!CredRead(targetName, CRED_TYPE.GENERIC, 0, out var ptr))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
            {
                return null;
            }

            var data = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, data, 0, data.Length);
            return System.Text.Encoding.UTF8.GetString(data);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    private static void Delete(string targetName)
    {
        CredDelete(targetName, CRED_TYPE.GENERIC, 0);
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, CRED_TYPE type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, CRED_TYPE type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr cred);

    private enum CRED_TYPE : uint
    {
        GENERIC = 1
    }

    private enum CRED_PERSIST : uint
    {
        LOCAL_MACHINE = 2 // Survives sign-out and restart. 1 (SESSION) is deleted at sign-out.
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public CRED_TYPE Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CRED_PERSIST Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LabWidge",
        "app.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message)
    {
        Write("ERROR", message);
    }

    private static void Write(string level, string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            RotateIfNeeded();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(LogPath, line);
        }
        catch
        {
            // Ignore logging failures.
        }
    }

    private static void RotateIfNeeded()
    {
        const long maxBytes = 5 * 1024 * 1024;
        try
        {
            if (File.Exists(LogPath))
            {
                var info = new FileInfo(LogPath);
                if (info.Length > maxBytes)
                {
                    var backup = LogPath + ".1";
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                    File.Move(LogPath, backup);
                }
            }
        }
        catch
        {
            // Ignore rotation errors.
        }
    }
}

internal static class ToastHelper
{
    // A new AppId (it changed in v1.1 and with the rename) keeps Windows from reusing a cached old logo in the toast header
    private const string AppId = "LabWidge.App";
    private const string ShortcutName = "LabWidge.lnk";

    private static readonly string LogoPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabWidge", "toast-logo.png");

    public static void EnsureToastRegistration()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);

            // Written every time, so the shortcut always points to the current exe, icon and AppId
            CreateShortcut(GetShortcutPath());

            Directory.CreateDirectory(Path.GetDirectoryName(LogoPath)!);
            using (var logo = WidgetIcon.Render(128, WidgetIcon.Amber))
            {
                logo.Save(LogoPath, System.Drawing.Imaging.ImageFormat.Png);
            }

            // Name and icon in the toast header for apps without an MSIX package
            using var aumid = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            aumid.SetValue("DisplayName", "LabWidge");
            aumid.SetValue("IconUri", LogoPath);
            aumid.SetValue("IconBackgroundColor", "FF0E141F");
        }
        catch (Exception ex)
        {
            Logger.Error($"Toast registration failed: {ex.Message}");
        }
    }

    public static void RemoveRegistration()
    {
        try { File.Delete(GetShortcutPath()); } catch { /* ignored */ }
        try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\AppUserModelId\{AppId}", throwOnMissingSubKey: false); } catch { /* ignored */ }
    }

    public static void RebuildShortcut()
    {
        try
        {
            var shortcutPath = GetShortcutPath();
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
            }
            CreateShortcut(shortcutPath);
            Logger.Info("Toast shortcut rebuilt.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not rebuild the toast shortcut: {ex.Message}");
        }
    }

    /// <summary>
    /// Shows a toast with the app logo, up to two lines of text under the title, and buttons.
    /// Clicks are handled in-process (while the app runs) and marshalled to the UI thread.
    /// </summary>
    public static void Show(string tag, string title, string line1, string? line2,
                            IReadOnlyList<(string Label, Action OnClick)> buttons, Action onBody,
                            bool silent = true, TimeSpan? expireAfter = null)
    {
        try
        {
            static string E(string? s) => System.Security.SecurityElement.Escape(s ?? "");
            var logoUri = new Uri(LogoPath).AbsoluteUri;

            var sb = new StringBuilder();
            sb.Append("<toast launch=\"body\" activationType=\"foreground\"><visual><binding template=\"ToastGeneric\">");
            sb.Append($"<image placement=\"appLogoOverride\" src=\"{E(logoUri)}\" hint-crop=\"none\"/>");
            sb.Append($"<text>{E(title)}</text><text>{E(line1)}</text>");
            if (!string.IsNullOrWhiteSpace(line2)) sb.Append($"<text>{E(line2)}</text>");
            sb.Append("</binding></visual>");
            if (buttons.Count > 0)
            {
                sb.Append("<actions>");
                for (var i = 0; i < buttons.Count; i++)
                    sb.Append($"<action content=\"{E(buttons[i].Label)}\" arguments=\"btn={i}\" activationType=\"foreground\"/>");
                sb.Append("</actions>");
            }
            sb.Append($"<audio silent=\"{(silent ? "true" : "false")}\"/></toast>");

            var xml = new XmlDocument();
            xml.LoadXml(sb.ToString());
            var toast = new ToastNotification(xml)
            {
                Tag = tag,
                Group = "LabWidge",
                ExpirationTime = expireAfter is TimeSpan ttl ? DateTimeOffset.Now.Add(ttl) : null
            };

            var ui = SynchronizationContext.Current;
            toast.Activated += (_, args) =>
            {
                var arguments = (args as ToastActivatedEventArgs)?.Arguments ?? "";
                Action action = onBody;
                if (arguments.StartsWith("btn=") && int.TryParse(arguments[4..], out var index) && index < buttons.Count)
                    action = buttons[index].OnClick;
                if (ui != null) ui.Post(_ => action(), null);
                else action();
            };

            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not show toast: {ex.Message}");
        }
    }

    private static string GetShortcutPath()
    {
        var startMenu = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        return Path.Combine(startMenu, "Programs", ShortcutName);
    }

    private static void CreateShortcut(string shortcutPath)
    {
        var exePath = Application.ExecutablePath;
        var shellLink = (IShellLinkW)new CShellLink();
        shellLink.SetPath(exePath);
        shellLink.SetArguments("");
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? exePath);
        var iconPath = AppIconProvider.IconPath;
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            shellLink.SetIconLocation(iconPath, 0);
        }

        var propertyStore = (IPropertyStore)shellLink;
        var pv = PropVariant.FromString(AppId);
        var key = PKEY_AppUserModel_ID;
        propertyStore.SetValue(ref key, ref pv);
        propertyStore.Commit();
        pv.Dispose();

        var file = (IPersistFile)shellLink;
        file.Save(shortcutPath, true);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount(out uint cProps);
        uint GetAt(uint iProp, out PROPERTYKEY pkey);
        uint GetValue(ref PROPERTYKEY key, out PropVariant pv);
        uint SetValue(ref PROPERTYKEY key, ref PropVariant pv);
        uint Commit();
    }

    [ComImport]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;

        public static PropVariant FromString(string value)
        {
            var pv = new PropVariant
            {
                vt = 31, // VT_LPWSTR
                p = Marshal.StringToCoTaskMemUni(value)
            };
            return pv;
        }

        public void Dispose()
        {
            PropVariantClear(ref this);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}

internal static class AppIconProvider
{
    public static string? IconPath { get; } = ResolveIconPath();
    private static Icon? _icon;

    public static Icon? GetIcon()
    {
        if (_icon != null)
        {
            return (Icon)_icon.Clone();
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(IconPath) && File.Exists(IconPath))
            {
                _icon = new Icon(IconPath);
                return (Icon)_icon.Clone();
            }
        }
        catch
        {
            // Ignore icon load errors.
        }

        try
        {
            _icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return _icon != null ? (Icon)_icon.Clone() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveIconPath()
    {
        var baseDir = AppContext.BaseDirectory;
        
        // Check in Assets folder first (common for structured projects)
        var assetsPath = Path.Combine(baseDir, "Assets", "app.ico");
        if (File.Exists(assetsPath))
        {
            return assetsPath;
        }

        // Fallback to root (where it might be copied flat)
        var rootPath = Path.Combine(baseDir, "app.ico");
        if (File.Exists(rootPath))
        {
            return rootPath;
        }

        // Self-contained single-file exe: the icon is embedded in the exe
        return Environment.ProcessPath;
    }
}
