using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SteamShortcutsImporter;

public class PluginSettingsView : UserControl
{
    private readonly IPlayniteAPI? _playniteApi;
    private TextBox? _pathBox;
    private TextBlock? _validationText;
    private TextBlock? _validationInfo;
    private CheckBox? _launchCheck;
    private CheckBox? _steamDefaultCheckBox;
    private ComboBox? _userComboBox;
    private StackPanel? _autoSyncSourcesPanel;
    private TextBlock? _autoSyncStatusText;
    private string? _currentSteamRootPath;
    private bool _isRefreshingUsers;

    public PluginSettingsView() : this(null) { }

    public PluginSettingsView(IPlayniteAPI? playniteApi)
    {
        _playniteApi = playniteApi;
        BuildUI();
    }

    private void BuildUI()
    {
        var panel = new StackPanel { Margin = new Thickness(12) };

        // Label
        panel.Children.Add(new TextBlock
        {
            Text = Constants.SteamPathLabel,
            FontWeight = FontWeights.Bold,
            FontSize = 11
        });

        // Path input row: [TextBox] [Browse] [Auto-detect]
        var pathRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };

        _pathBox = new TextBox
        {
            Name = Constants.SteamRootPathBoxName,
            MinWidth = 350
        };
        _pathBox.SetBinding(TextBox.TextProperty,
            new System.Windows.Data.Binding("SteamRootPath") { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        _pathBox.TextChanged += (_, _) => UpdateValidation();
        pathRow.Children.Add(_pathBox);

        var browseBtn = new Button
        {
            Content = Constants.BrowseButtonLabel,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2)
        };
        browseBtn.Click += BrowseButton_Click;
        pathRow.Children.Add(browseBtn);

        var autoDetectBtn = new Button
        {
            Content = Constants.AutoDetectButtonLabel,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2)
        };
        autoDetectBtn.Click += AutoDetectButton_Click;
        pathRow.Children.Add(autoDetectBtn);

        panel.Children.Add(pathRow);

        // Validation feedback - main status
        _validationText = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11
        };
        panel.Children.Add(_validationText);

        // Validation feedback - info note (for missing VDF)
        _validationInfo = new TextBlock
        {
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_validationInfo);

        // Launch via Steam checkbox
        _launchCheck = new CheckBox
        {
            Content = "Launch via Steam (rungameid) when possible",
            Margin = new Thickness(0, 12, 0, 0)
        };
        _launchCheck.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("LaunchViaSteam") { Mode = System.Windows.Data.BindingMode.TwoWay });
        _launchCheck.Checked += LaunchCheck_IsEnabledChanged;
        _launchCheck.Unchecked += LaunchCheck_IsEnabledChanged;
        panel.Children.Add(_launchCheck);

        // Steam default action checkbox (indented under Launch via Steam)
        _steamDefaultCheckBox = new CheckBox
        {
            Content = "Make Steam action the default play action",
            Margin = new Thickness(24, 4, 0, 0),
            IsEnabled = false
        };
        _steamDefaultCheckBox.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("SteamActionIsDefault") { Mode = System.Windows.Data.BindingMode.TwoWay });
        panel.Children.Add(_steamDefaultCheckBox);

        // Set initial enabled state for default checkbox
        Loaded += InitializeDefaultCheckBoxState;

        var syncHeader = new TextBlock
        {
            Text = "Automatic Playnite → Steam sync",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Margin = new Thickness(0, 14, 0, 0)
        };
        panel.Children.Add(syncHeader);

        var autoSyncCheck = new CheckBox
        {
            Content = "Automatically add eligible new games to Steam",
            Margin = new Thickness(0, 5, 0, 0)
        };
        autoSyncCheck.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("AutoSyncEnabled") { Mode = System.Windows.Data.BindingMode.TwoWay });
        panel.Children.Add(autoSyncCheck);

        panel.Children.Add(new TextBlock
        {
            Text = "Choose sources and filters before the first Playnite → Steam export. That first run adds or updates all matching supported games in one batch and establishes the baseline. Enable automatic sync afterward; normal launches of Playnite do not scan the full library.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            Margin = new Thickness(18, 2, 0, 0),
            MaxWidth = 510
        });

        AddSyncFilterCheckBox(panel, "AutoSyncOnlyInstalled", "Only installed games", true);
        AddSyncFilterCheckBox(panel, "AutoSyncIncludeEmulators", "Include supported emulator actions", true);
        AddSyncFilterCheckBox(panel, "AutoSyncIncludeUrls", "Include URL launch actions", true);

        panel.Children.Add(new TextBlock
        {
            Text = "Library sources to watch:",
            Margin = new Thickness(18, 6, 0, 0),
            FontSize = 10
        });
        _autoSyncSourcesPanel = new StackPanel { Margin = new Thickness(28, 2, 0, 0) };
        panel.Children.Add(_autoSyncSourcesPanel);

        _autoSyncStatusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10,
            Margin = new Thickness(18, 8, 0, 0),
            MaxWidth = 510
        };
        panel.Children.Add(_autoSyncStatusText);

        var syncActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(18, 4, 0, 0)
        };
        var refreshStatusButton = new Button { Content = "Refresh status", Padding = new Thickness(8, 2, 8, 2) };
        refreshStatusButton.Click += (_, _) => RefreshAutoSyncStatus();
        syncActions.Children.Add(refreshStatusButton);

        var retryButton = new Button
        {
            Content = "Retry queued items",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(5, 0, 0, 0)
        };
        retryButton.Click += (_, _) =>
        {
            ShortcutsLibrary.RetryAutomaticSync();
            RefreshAutoSyncStatus();
        };
        syncActions.Children.Add(retryButton);

        var reconcileButton = new Button
        {
            Content = "Reconcile library now",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(5, 0, 0, 0)
        };
        reconcileButton.SetBinding(IsEnabledProperty,
            new System.Windows.Data.Binding("AutoSyncEnabled") { Mode = System.Windows.Data.BindingMode.OneWay });
        reconcileButton.Click += (_, _) =>
        {
            ShortcutsLibrary.ReconcileAutomaticSync();
            RefreshAutoSyncStatus();
        };
        syncActions.Children.Add(reconcileButton);
        panel.Children.Add(syncActions);

        // Steam user selection
        panel.Children.Add(new TextBlock
        {
            Text = Constants.SteamUserLabel,
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0)
        });

        _userComboBox = new ComboBox
        {
            MinWidth = 250,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _userComboBox.SelectionChanged += UserComboBox_SelectionChanged;
        panel.Children.Add(_userComboBox);

        // Restore Backup button
        var restoreBtn = new Button
        {
            Content = Constants.RestoreBackupLabel,
            Margin = new Thickness(0, 12, 0, 0),
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        restoreBtn.Click += RestoreBackupButton_Click;
        panel.Children.Add(restoreBtn);

        Content = panel;

        // Initial validation after load
        Loaded += (_, _) =>
        {
            UpdateValidation();
            RefreshUserList();
            RefreshAutoSyncSources();
            RefreshAutoSyncStatus();
        };
    }

    private void AddSyncFilterCheckBox(Panel panel, string settingPath, string label, bool indent)
    {
        var check = new CheckBox
        {
            Content = label,
            Margin = new Thickness(indent ? 18 : 0, 3, 0, 0)
        };
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(settingPath) { Mode = System.Windows.Data.BindingMode.TwoWay });
        panel.Children.Add(check);
    }

    private void RefreshAutoSyncSources()
    {
        if (_autoSyncSourcesPanel == null || _playniteApi?.Database?.Games == null) return;
        _autoSyncSourcesPanel.Children.Clear();

        var settings = DataContext as PluginSettings;
        if (settings == null) return;

        var games = _playniteApi.Database.Games.Where(g => g != null).ToList();
        AutoSyncSourceSelector.MigrateLegacyPluginSelections(settings, games, Guid.Parse(Constants.PluginId));
        var sources = AutoSyncSourceSelector.GetSources(games, Guid.Parse(Constants.PluginId));

        foreach (var source in sources)
        {
            var sourceId = source.Id;
            var check = new CheckBox
            {
                Content = source.Name,
                Tag = sourceId,
                IsChecked = settings.AutoSyncSourceIds.Any(x => string.Equals(x, sourceId, StringComparison.OrdinalIgnoreCase)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            check.Checked += AutoSyncSource_CheckedChanged;
            check.Unchecked += AutoSyncSource_CheckedChanged;
            _autoSyncSourcesPanel.Children.Add(check);
        }

        if (sources.Count == 0)
        {
            _autoSyncSourcesPanel.Children.Add(new TextBlock
            {
                Text = "Sources appear here after Playnite has imported games from them.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128))
            });
        }
    }

    private void AutoSyncSource_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check || check.Tag is not string sourceId || DataContext is not PluginSettings settings) return;

        settings.AutoSyncSourceIds ??= new List<string>();
        settings.AutoSyncSourceIds.RemoveAll(x => string.Equals(x, sourceId, StringComparison.OrdinalIgnoreCase));
        if (check.IsChecked == true)
        {
            settings.AutoSyncSourceIds.Add(sourceId);
        }
    }

    private void RefreshAutoSyncStatus()
    {
        if (_autoSyncStatusText != null)
        {
            _autoSyncStatusText.Text = ShortcutsLibrary.GetAutomaticSyncStatus();
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playniteApi == null || _pathBox == null) return;

        try
        {
            var selected = _playniteApi.Dialogs.SelectFolder();

            if (!string.IsNullOrEmpty(selected))
            {
                _pathBox.Text = selected;
            }
        }
        catch (Exception ex)
        {
            LogManager.GetLogger().Warn(ex, "Browse folder dialog failed.");
        }
    }

    private void AutoDetectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var detected = PluginSettings.GuessSteamRootPath();
            if (!string.IsNullOrEmpty(detected) && _pathBox != null)
            {
                _pathBox.Text = detected;
            }
            else
            {
                _playniteApi?.Dialogs.ShowMessage(Constants.AutoDetectFailedMessage, Constants.PluginName);
            }
        }
        catch (Exception ex)
        {
            LogManager.GetLogger().Warn(ex, "Auto-detect Steam path failed.");
            _playniteApi?.Dialogs.ShowErrorMessage($"Auto-detect failed: {ex.Message}", Constants.PluginName);
        }
    }

    private void UpdateValidation()
    {
        if (_validationText == null || _validationInfo == null || _pathBox == null) return;

        var path = _pathBox.Text;

        // State 1: Path empty or doesn't exist
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _validationText.Text = $"\u2717 {Constants.PathInvalidMessage}";
            _validationText.Foreground = new SolidColorBrush(Colors.Red);
            _validationInfo.Visibility = Visibility.Collapsed;
            return;
        }

        // State 2: Path exists but no userdata folder
        var userdataPath = Path.Combine(path, Constants.UserDataDirectory);
        if (!Directory.Exists(userdataPath))
        {
            _validationText.Text = $"\u26A0 {Constants.PathNoUserdataMessage}";
            _validationText.Foreground = new SolidColorBrush(Colors.Orange);
            _validationInfo.Visibility = Visibility.Collapsed;
            return;
        }

        // State 3: Valid Steam path - check for shortcuts.vdf
        bool hasVdf = false;
        try
        {
            hasVdf = Directory.EnumerateDirectories(userdataPath)
                .Any(userDir => File.Exists(Path.Combine(userDir, Constants.ConfigDirectory, "shortcuts.vdf")));
        }
        catch (Exception ex)
        {
            LogManager.GetLogger().Warn(ex, "Failed to check for shortcuts.vdf.");
        }

        _validationText.Text = $"\u2713 {Constants.PathValidMessage}";
        _validationText.Foreground = new SolidColorBrush(Colors.Green);

        if (!hasVdf)
        {
            _validationInfo.Text = $"\u2139 {Constants.PathNoVdfInfoMessage}";
            _validationInfo.Visibility = Visibility.Visible;
        }
        else
        {
            _validationInfo.Visibility = Visibility.Collapsed;
        }

        // Refresh user list if path changed
        var newSteamPath = _pathBox.Text;
        if (newSteamPath != _currentSteamRootPath)
        {
            _currentSteamRootPath = newSteamPath;
            RefreshUserList();
        }
    }

    private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var api = ShortcutsLibrary.GetPlayniteApiStatic();
            if (api == null)
            {
                LogManager.GetLogger().Warn("PlayniteApi not available for restore.");
                return;
            }

            // Get list of Steam users
            var userIds = ShortcutsLibrary.GetSteamUserIdsStatic();
            if (userIds.Count == 0)
            {
                api.Dialogs.ShowMessage("No Steam users found. Check your Steam path in settings.", Constants.RestoreBackupDialogTitle);
                return;
            }

            string selectedUserId;

            // If multiple users, let user pick
            if (userIds.Count > 1)
            {
                var selection = api.Dialogs.SelectString(
                    Constants.SelectSteamUserMessage,
                    Constants.SelectSteamUserTitle,
                    userIds.First()
                );

                if (!selection.Result || string.IsNullOrEmpty(selection.SelectedString))
                {
                    return; // User cancelled
                }

                // Show a selection dialog with the user IDs
                var selectedItem = api.Dialogs.ChooseItemWithSearch(
                    new List<GenericItemOption>(userIds.Select(u => new GenericItemOption(u, $"Steam User {u}"))),
                    (s) => new List<GenericItemOption>(userIds
                        .Where(u => string.IsNullOrEmpty(s) || u.Contains(s))
                        .Select(u => new GenericItemOption(u, $"Steam User {u}"))),
                    null,
                    Constants.SelectSteamUserTitle
                );

                if (selectedItem == null)
                {
                    return; // User cancelled
                }

                selectedUserId = selectedItem.Name;
            }
            else
            {
                selectedUserId = userIds.First();
            }

            // Get backup folder for this user
            var backupFolder = ShortcutsLibrary.TryGetBackupFolderForUserStatic(selectedUserId);
            if (string.IsNullOrEmpty(backupFolder))
            {
                api.Dialogs.ShowMessage(Constants.NoBackupsFoundMessage, Constants.RestoreBackupDialogTitle);
                return;
            }

            // Ensure folder exists
            Directory.CreateDirectory(backupFolder);

            // Check if there are any backups
            var backupFiles = Directory.GetFiles(backupFolder, Constants.BackupFileSearchPattern);
            if (backupFiles.Length == 0)
            {
                // Also check old backup location for this user
                var oldBackupFolder = ShortcutsLibrary.TryGetBackupRootStatic();
                if (!string.IsNullOrEmpty(oldBackupFolder))
                {
                    var oldShortcutsFolder = Path.Combine(oldBackupFolder, Constants.ShortcutsKind);
                    if (Directory.Exists(oldShortcutsFolder))
                    {
                        var oldBackups = Directory.GetFiles(oldShortcutsFolder, $"*-{selectedUserId}-*{Constants.BackupFileExtension}");
                        if (oldBackups.Length > 0)
                        {
                            // Open old folder instead
                            backupFolder = oldShortcutsFolder;
                            backupFiles = oldBackups;
                        }
                    }
                }

                if (backupFiles.Length == 0)
                {
                    api.Dialogs.ShowMessage(Constants.NoBackupsFoundMessage, Constants.RestoreBackupDialogTitle);
                    return;
                }
            }

            // Open file dialog in the backup folder
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Constants.RestoreBackupDialogTitle,
                Filter = Constants.BackupFileFilter + "|All Files (*.*)|*.*",
                InitialDirectory = backupFolder
            };

            if (dialog.ShowDialog() != true)
            {
                return; // User cancelled
            }

            var selectedFile = dialog.FileName;
            if (string.IsNullOrEmpty(selectedFile))
            {
                return;
            }

            // Verify the selected file is a backup file
            if (!selectedFile.EndsWith(Constants.BackupFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                api.Dialogs.ShowMessage("Please select a valid backup file (*.bak.vdf).", Constants.RestoreBackupDialogTitle);
                return;
            }

            // Restore only when Steam is verifiably closed.
            if (!SteamProcessHelper.TryIsSteamRunning(out var steamRunning) || steamRunning)
            {
                api.Dialogs.ShowMessage(Constants.SteamMustBeClosedMessage, Constants.RestoreBackupDialogTitle);
                return;
            }

            // Confirm restore
            var confirmResult = api.Dialogs.ShowMessage(
                Constants.RestoreConfirmMessage,
                Constants.RestoreBackupDialogTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            // Perform restore
            var success = ShortcutsLibrary.RestoreBackupStatic(selectedFile, selectedUserId);
            if (success)
            {
                api.Dialogs.ShowMessage(Constants.RestoreSuccessMessage, Constants.RestoreBackupDialogTitle);
            }
            else
            {
                api.Dialogs.ShowMessage(Constants.RestoreFailedMessage, Constants.RestoreBackupDialogTitle);
            }
        }
        catch (Exception ex)
        {
            LogManager.GetLogger().Error(ex, "Failed to restore backup.");
        }
    }

    private void RefreshUserList()
    {
        if (_userComboBox == null || _isRefreshingUsers) return;

        _isRefreshingUsers = true;
        try
        {
            var steamPath = _pathBox?.Text;
            var users = SteamUsersReader.GetValidUsers(steamPath, ShortcutsLibrary.GetSteamUserIdsForPath(steamPath));

            _userComboBox.Items.Clear();

            // Add "Auto-detect" option first
            var autoItem = new ComboBoxItem { Content = Constants.AutoDetectUserLabel, Tag = null };
            _userComboBox.Items.Add(autoItem);

            // Add users
            foreach (var user in users)
            {
                var item = new ComboBoxItem { Content = user.DisplayName, Tag = user.UserId };
                _userComboBox.Items.Add(item);
            }

            // Select current setting or auto-detect
            var settings = DataContext as PluginSettings;
            var selectedUserId = settings?.SelectedSteamUserId;

            if (string.IsNullOrEmpty(selectedUserId))
            {
                _userComboBox.SelectedItem = autoItem;
            }
            else
            {
                var matchingItem = _userComboBox.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag as string == selectedUserId);

                if (matchingItem != null)
                {
                    _userComboBox.SelectedItem = matchingItem;
                }
                else
                {
                    // Selected user no longer exists, clear and save
                    _userComboBox.SelectedItem = autoItem;
                    if (settings != null)
                    {
                        settings.SelectedSteamUserId = null;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogManager.GetLogger().Warn(ex, "Failed to refresh user list.");
        }
        finally
        {
            _isRefreshingUsers = false;
        }
    }

    private void UserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_userComboBox == null || _isRefreshingUsers) return;

        var selectedItem = _userComboBox.SelectedItem as ComboBoxItem;
        var settings = DataContext as PluginSettings;

        if (settings != null)
        {
            settings.SelectedSteamUserId = selectedItem?.Tag as string;
        }
    }

    private void LaunchCheck_IsEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_steamDefaultCheckBox != null && _launchCheck != null)
        {
            _steamDefaultCheckBox.IsEnabled = _launchCheck.IsChecked == true;
        }
    }

    private void InitializeDefaultCheckBoxState(object sender, RoutedEventArgs e)
    {
        if (_steamDefaultCheckBox != null && _launchCheck != null)
        {
            _steamDefaultCheckBox.IsEnabled = _launchCheck.IsChecked == true;
        }
    }
}
