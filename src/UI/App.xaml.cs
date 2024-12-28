using System;
using System.Windows;
using UI.Services;
using Shared;
using Forms = System.Windows.Forms;  // Alias Windows.Forms
using System.IO;
using System.Collections.Generic;

namespace UI
{
    public partial class App : System.Windows.Application
    {
        private MainWindow? _mainWindow;
        private readonly IpcClient _ipcClient;
        private Forms.NotifyIcon? _notifyIcon;
        private Dictionary<ConnectionState, System.Drawing.Icon> _icons = new();

        public App()
        {
            _ipcClient = new IpcClient();
            _ipcClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            Exit += App_Exit;
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);

                // Create notify icon
                try
                {
                    var resourcesPath = Path.Combine(AppContext.BaseDirectory, "Resources");
                    _icons[ConnectionState.Connected] = new System.Drawing.Icon(Path.Combine(resourcesPath, "connected.ico"));
                    _icons[ConnectionState.Disconnected] = new System.Drawing.Icon(Path.Combine(resourcesPath, "disconnected.ico"));
                    _icons[ConnectionState.Pending] = new System.Drawing.Icon(Path.Combine(resourcesPath, "pending.ico"));

                    _notifyIcon = new Forms.NotifyIcon
                    {
                        Icon = _icons[ConnectionState.Pending], // Start with pending state
                        Visible = true,
                        Text = "Certificate Manager - Checking Status..."
                    };

                    // Add menu items
                    var contextMenu = new Forms.ContextMenuStrip();
                    contextMenu.Items.Add("Configure", null, (s, e) => ShowConfigurationWindow());
                    contextMenu.Items.Add("-"); // Separator
                    contextMenu.Items.Add("Exit", null, (s, e) => Shutdown());
                    _notifyIcon.ContextMenuStrip = contextMenu;

                    // Add double-click handler
                    _notifyIcon.DoubleClick += (s, e) => ShowConfigurationWindow();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        $"Error creating tray icon: {ex.Message}\nPath: {AppContext.BaseDirectory}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                // Start listening for show window commands
                await _ipcClient.StartListening(ShowConfigurationWindow);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error starting application: {ex.Message}", 
                    "Error",
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void OnConnectionStatusChanged(ConnectionState state)
        {
            Dispatcher.Invoke(() =>
            {
                if (_notifyIcon != null && _icons.ContainsKey(state))
                {
                    _notifyIcon.Icon = _icons[state];
                    _notifyIcon.Text = $"Certificate Manager - {state}";
                }
            });
        }

        private void ShowConfigurationWindow()
        {
            Dispatcher.Invoke(() =>
            {
                if (_mainWindow == null)
                {
                    _mainWindow = new MainWindow(_ipcClient);
                }

                _mainWindow.Show();
                _mainWindow.Activate();
            });
        }

        private void App_Exit(object sender, ExitEventArgs e)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }

            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }

            _ipcClient?.Dispose();
        }
    }
} 