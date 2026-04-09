using System;
using System.IO;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices; // Required for DllImport
using System.Windows.Interop;         // Required for WindowInteropHelper

namespace QuickSpell
{
    public class AppSettings
    {
        public bool RunOnStartup { get; set; } = false;
        public bool IsDarkTheme { get; set; } = true;
        public string MagicCommand { get; set; } = "//settings";
        public string KeybindString { get; set; } = "Alt + Space";
        public uint HotkeyModifiers { get; set; } = 0x0001; // Default: Alt
        public uint HotkeyKey { get; set; } = 0x20;         // Default: Space
    }
    public partial class MainWindow : Window
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        // --- 1. IMPORT WINDOWS API FUNCTIONS ---
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // NEW: Import Kernel32 for the Memory Flush (using IntPtr for 64-bit safety)
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        // --- 2. DEFINE HOTKEY CONSTANTS ---
        private const int HOTKEY_ID = 9000;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_SPACE = 0x20;
        private const int WM_HOTKEY = 0x0312;

        // --- SETTINGS STATE VARIABLES ---
        private bool _inSettingsMode = false;
        private bool _isListeningForKeybind = false;
        private bool _isListeningForCommand = false;

        // NEW: The permanent settings object and save path
        private AppSettings _settings = new AppSettings();
        private readonly string _settingsFilePath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "QuickSpell", "settings.json");

        public MainWindow()
        {
            InitializeComponent();
        }

        private void RenderSettingsMenu()
        {
            if (_isListeningForKeybind)
            {
                SuggestionList.ItemsSource = new List<string> { "Press new hotkey now... (Esc to cancel)" };
            }
            else if (_isListeningForCommand)
            {
                SuggestionList.ItemsSource = new List<string> { "Type your new command above and press Enter..." };
            }
            else
            {
                SuggestionList.ItemsSource = new List<string>
        {
            $"1. Run on Startup: {(_settings.RunOnStartup ? "ON" : "OFF")}",
            $"2. Keybind: {_settings.KeybindString}",
            $"3. Theme: {(_settings.IsDarkTheme? "Dark" : "Light")}",
            $"4. Menu Command: {_settings.MagicCommand}", // NEW 4th Option
            "",
            "(Enter to select, Esc to exit)"
        };
            }
            SuggestionList.SelectedIndex = 0;
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { /* If file is corrupted, it just uses default settings */ }

            ApplyTheme(); // Apply the saved theme immediately
        }

        private void SaveSettings()
        {
            try
            {
                // 1. Save to JSON file
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath));
                string json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);

                // 2. Apply to Windows Startup Registry
                RegistryKey rk = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                if (_settings.RunOnStartup)
                {
                    // Gets the exact location of your QuickSpell.exe and tells Windows to run it
                    rk.SetValue("QuickSpell", System.Reflection.Assembly.GetExecutingAssembly().Location);
                }
                else
                {
                    rk.DeleteValue("QuickSpell", false);
                }
            }
            catch { /* Silently fail if they don't have registry permissions */ }
        }

        private void ApplyTheme()
        {
            var mainBorder = (System.Windows.Controls.Border)this.Content;

            if (_settings.IsDarkTheme)
            {
                mainBorder.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#2D2D30"));
                mainBorder.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3E3E42"));
                SearchBox.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E1E1E"));
                SearchBox.Foreground = System.Windows.Media.Brushes.White;
                SuggestionList.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC"));
            }
            else
            {
                mainBorder.Background = System.Windows.Media.Brushes.White;
                mainBorder.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CCCCCC"));
                SearchBox.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F0F0F0"));
                SearchBox.Foreground = System.Windows.Media.Brushes.Black;
                SuggestionList.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        // --- 3. REGISTER THE HOTKEY WHEN APP STARTS ---
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // LOAD SAVED SETTINGS FIRST!
            LoadSettings();

            IntPtr handle = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(handle);
            source.AddHook(HwndHook);

            // Register the custom saved hotkey!
            RegisterHotKey(handle, HOTKEY_ID, _settings.HotkeyModifiers, _settings.HotkeyKey);
        }

        // --- 4. CATCH THE HOTKEY PRESS ---
        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                // Network Warm-up
                _ = _httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Head, "http://suggestqueries.google.com/"));

                // Reset all settings states so it doesn't open stuck in a menu
                _inSettingsMode = false;
                _isListeningForKeybind = false;
                _isListeningForCommand = false;

                // NOW show the window (it will already be blank from when we hid it!)
                this.Show();
                this.Activate();
                SearchBox.Focus();

                handled = true;
            }
            return IntPtr.Zero;
        }

        // --- 5. CLEAN UP WHEN CLOSED ---
        protected override void OnClosed(EventArgs e)
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HOTKEY_ID);
            base.OnClosed(e);
        }

        // --- 6. NEW: THE GAMER-LEVEL MEMORY FLUSH ---
        private void HideAndFlushMemory()
        {
            // 1. Wipe the UI clean
            SearchBox.Text = "";
            SuggestionList.ItemsSource = null;

            // 2. FORCE WPF to instantly draw the blank screen before hiding
            this.UpdateLayout();

            // 3. Now hide the window (Windows will cache a completely blank image)
            this.Hide();

            // 4. Clean up unused objects
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 5. Tell Windows to dump all idle memory to disk
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // If the user clicks outside the window, treat it like pressing Escape
            HideAndFlushMemory();
        }

        // ---------------------------------------------------------
        // GOOGLE API AND KEYBOARD LOGIC
        // ---------------------------------------------------------

        private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string query = SearchBox.Text.ToLower().Trim();

            // 1. Check for the dynamic magic command
            if (query == _settings.MagicCommand)
            {
                _inSettingsMode = true;
                RenderSettingsMenu();
                return;
            }

            // 2. Take us out of settings if they delete it
            if (_inSettingsMode && query != _settings.MagicCommand && !_isListeningForCommand)
            {
                _inSettingsMode = false;
                _isListeningForKeybind = false;
            }

            // 3. NEW: If they are typing a new command, stop here! Don't ask Google.
            if (_isListeningForCommand) return;

            // --- EXISTING GOOGLE LOGIC ---
            if (string.IsNullOrWhiteSpace(query) || _inSettingsMode)
            {
                SuggestionList.ItemsSource = null;
                return;
            }

            try
            {
                string url = $"http://suggestqueries.google.com/complete/search?client=chrome&q={Uri.EscapeDataString(query)}";
                string response = await _httpClient.GetStringAsync(url);

                using (JsonDocument doc = JsonDocument.Parse(response))
                {
                    var suggestions = doc.RootElement[1];
                    var list = new List<string>();

                    foreach (var item in suggestions.EnumerateArray())
                    {
                        list.Add(item.GetString());
                    }

                    SuggestionList.ItemsSource = list;
                    if (list.Count > 0) SuggestionList.SelectedIndex = 0;
                }
            }
            catch { }
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // --- FEATURE 1: CAPTURE NEW KEYBIND ---
            if (_isListeningForKeybind)
            {
                e.Handled = true; // Stop the key from typing into the box

                if (e.Key == Key.Escape)
                {
                    _isListeningForKeybind = false;
                    RenderSettingsMenu();
                    return;
                }

                // Ignore pure modifier presses (wait for them to press the final letter/key)
                if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl || e.Key == Key.LeftAlt ||
                    e.Key == Key.RightAlt || e.Key == Key.LeftShift || e.Key == Key.RightShift ||
                    (e.Key == Key.System && (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt)))
                    return;

                // Figure out which modifiers they are holding
                uint modifiers = 0;
                string keyStr = "";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { modifiers |= 0x0002; keyStr += "Ctrl + "; }
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { modifiers |= 0x0001; keyStr += "Alt + "; }
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { modifiers |= 0x0004; keyStr += "Shift + "; }

                // Get the actual main key pressed
                Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(actualKey);
                keyStr += actualKey.ToString();

                // Unregister the old hotkey and register the new one!
                IntPtr handle = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(handle, HOTKEY_ID);
                RegisterHotKey(handle, HOTKEY_ID, modifiers, vk);

                // Save the new values!
                _settings.HotkeyModifiers = modifiers;
                _settings.HotkeyKey = vk;
                _settings.KeybindString = keyStr;
                SaveSettings(); // <-- ADD THIS

                _isListeningForKeybind = false;
                RenderSettingsMenu();
                return;
            }

            // --- FEATURE 2: CAPTURE NEW MENU COMMAND ---
            if (_isListeningForCommand)
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                    {
                        _settings.MagicCommand = SearchBox.Text.ToLower().Trim();
                        SaveSettings();
                    }
                    _isListeningForCommand = false;
                    SearchBox.Text = "";
                    RenderSettingsMenu();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _isListeningForCommand = false;
                    SearchBox.Text = "";
                    RenderSettingsMenu();
                }
                return;
            }

            // --- NORMAL APP LOGIC ---
            if (e.Key == Key.Down)
            {
                if (SuggestionList.SelectedIndex < SuggestionList.Items.Count - 1)
                    SuggestionList.SelectedIndex++;
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (SuggestionList.SelectedIndex > 0)
                    SuggestionList.SelectedIndex--;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                if (_inSettingsMode)
                {
                    if (SuggestionList.SelectedIndex == 0) // Startup
                    {
                        _settings.RunOnStartup = !_settings.RunOnStartup;
                        SaveSettings();
                        RenderSettingsMenu();
                    }
                    else if (SuggestionList.SelectedIndex == 1) // Keybind
                    {
                        _isListeningForKeybind = true;
                        RenderSettingsMenu();
                    }
                    else if (SuggestionList.SelectedIndex == 2) // Theme
                    {
                        _settings.IsDarkTheme = !_settings.IsDarkTheme;
                        SaveSettings();
                        ApplyTheme();
                        RenderSettingsMenu();
                    }
                    else if (SuggestionList.SelectedIndex == 3) // Menu Command
                    {
                        _isListeningForCommand = true;
                        SearchBox.Text = ""; // Clear box so they can type the new command
                        RenderSettingsMenu();
                    }
                    e.Handled = true;
                }
                else if (SuggestionList.SelectedItem != null)
                {
                    Clipboard.SetText(SuggestionList.SelectedItem.ToString());
                    HideAndFlushMemory();
                }
            }
            else if (e.Key == Key.Escape)
            {
                HideAndFlushMemory();
            }
        }
    }
}