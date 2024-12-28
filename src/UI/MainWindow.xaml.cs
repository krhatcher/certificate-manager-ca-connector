using System;
using System.Windows;
using UI.Services;
using System.IO;
using System.ComponentModel;

namespace UI
{
    public partial class MainWindow : Window
    {
        private readonly IpcClient? _ipcClient;
        private readonly DebugWriter _debugWriter;

        // Default constructor for XAML designer
        public MainWindow()
        {
            _debugWriter = new DebugWriter();
            InitializeComponent();
            
            // Handle window closing
            Closing += MainWindow_Closing;
        }

        // Runtime constructor
        public MainWindow(IpcClient ipcClient) : this()
        {
            _ipcClient = ipcClient;
            
            Console.SetOut(_debugWriter);

            _debugWriter.TextWritten += (s, text) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (DebugText != null)
                    {
                        DebugText.AppendText(text);
                        DebugText.ScrollToEnd();
                    }
                });
            };

            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_ipcClient == null) return; // Design-time or no IPC client

                var config = await _ipcClient.GetConfiguration();
                if (config != null)
                {
                    UrlTextBox.Text = config.Url;
                    ApiKeyBox.Password = config.ApiKey;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error loading configuration: {ex.Message}", 
                    "Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_ipcClient == null)
                {
                    System.Windows.MessageBox.Show(
                        "IPC client not initialized", 
                        "Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                    return;
                }

                var url = UrlTextBox.Text.Trim();
                var apiKey = ApiKeyBox.Password.Trim();

                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(apiKey))
                {
                    System.Windows.MessageBox.Show(
                        "Please enter both URL and API Key", 
                        "Validation Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Warning);
                    return;
                }

                // Validate URL format
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || 
                    (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    System.Windows.MessageBox.Show(
                        "Please enter a valid URL (e.g., https://example.com)", 
                        "Validation Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Warning);
                    return;
                }

                if (await _ipcClient.SaveConfiguration(url, apiKey))
                {
                    System.Windows.MessageBox.Show(
                        "Configuration saved successfully", 
                        "Success", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Information);
                    Hide();
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        "Failed to save configuration", 
                        "Error", 
                        MessageBoxButton.OK, 
                        MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error saving configuration: {ex.Message}", 
                    "Error", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Error);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Cancel the close and just hide the window instead
            e.Cancel = true;
            Hide();
        }
    }

    public class DebugWriter : TextWriter
    {
        public event EventHandler<string>? TextWritten;
        
        public override void Write(char value)
        {
            TextWritten?.Invoke(this, value.ToString());
        }

        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                TextWritten?.Invoke(this, value);
            }
        }

        public override void WriteLine(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                TextWritten?.Invoke(this, value + Environment.NewLine);
            }
        }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
} 