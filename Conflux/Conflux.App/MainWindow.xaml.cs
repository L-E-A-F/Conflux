
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Conflux.Models;
using Conflux.Services;

namespace Conflux;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly AiRouter _router = new();
    private readonly ChatGptAuthService _chatGptAuth = new();

    private AppSettings _settings = new();

    private AiProvider? _selectedProvider;
    private ConversationEntry? _activeConversation;

    private List<ChatLine> _currentMessages = new();
    private readonly List<ConversationEntry> _conversations = new();

    private bool _initializing = true;
    private bool _busy;
    private bool _loadingConversation;
    private bool _chatGptAuthBusy;

    private sealed class ChatLine
    {
        public string Speaker { get; set; } = "";
        public string Content { get; set; } = "";
        public string Provider { get; set; } = "";
    }

    private sealed class ConversationEntry
    {
        public string Title { get; set; } = "New conversation";
        public List<ChatLine> Messages { get; set; } = new();
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(
        object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _settingsService.LoadAsync();

            RefreshProviderList();
            RefreshRoutingControls();
            RefreshRouteDisplay();

            GeneralReviewBox.IsChecked =
                GetCurrentRoute().EnableReview;

            AddMessage(
                "System",
                "Conflux is ready. Configure an API key and model " +
                "in AI Providers to start chatting. You can also " +
                "connect a ChatGPT account in the provider settings.",
                "System");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Settings error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _initializing = false;
        }

        await RefreshChatGptStatusAsync();
    }

    private string CurrentTask =>
        (TaskTypeBox.SelectedItem as ComboBoxItem)
            ?.Content?.ToString() ?? "General";

    private RoutingProfile GetCurrentRoute()
    {
        var route = _settings.Routes.FirstOrDefault(r =>
            r.TaskType.Equals(
                CurrentTask,
                StringComparison.OrdinalIgnoreCase));

        if (route is not null)
            return route;

        route = new RoutingProfile { TaskType = CurrentTask };
        _settings.Routes.Add(route);
        return route;
    }

    private void AddMessage(
        string speaker,
        string content,
        string provider,
        bool record = true)
    {
        var card = new Border
        {
            Background = speaker == "You"
                ? Brushes.Transparent
                : (Brush)FindResource("CardBackground"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(3, 5, 3, 10)
        };

        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(provider)
                ? speaker
                : $"{speaker} · {provider}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Foreground = (Brush)FindResource("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 8)
        });

        stack.Children.Add(new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 21
        });

        card.Child = stack;
        ChatMessages.Children.Add(card);

        if (record)
        {
            var line = new ChatLine
            {
                Speaker = speaker,
                Content = content,
                Provider = provider
            };

            _currentMessages.Add(line);

            if (_activeConversation is not null)
            {
                _activeConversation.Messages =
                    new List<ChatLine>(_currentMessages);

                UpdateConversationList();
            }
        }

        ChatScroll.ScrollToEnd();
    }

    private async void Send_Click(
        object sender, RoutedEventArgs e)
    {
        await SendPromptAsync();
    }

    private async void PromptBox_KeyDown(
        object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter &&
            Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SendPromptAsync();
        }
    }

    private async Task SendPromptAsync()
    {
        if (_busy)
            return;

        var prompt = PromptBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var route = GetCurrentRoute();

        if (string.IsNullOrWhiteSpace(route.PrimaryProvider))
        {
            MessageBox.Show(
                "Choose a primary provider in AI Providers first.",
                "Provider required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _busy = true;
        PromptBox.Clear();
        PromptBox.IsEnabled = false;

        AddMessage("You", prompt, "You");

        try
        {
            SetStatus("Working...");
            RefreshRouteDisplay();

            var result = await _router.SendAsync(
                _settings,
                route,
                prompt,
                message => SetStatus(message));

            AddMessage(
                "AI Team",
                result.Answer,
                result.Provider);

            SetStatus($"Completed using {result.Provider}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Request cancelled.");
        }
        catch (Exception ex)
        {
            AddMessage("Error", ex.Message, "Conflux");
            SetStatus("Request failed.");
        }
        finally
        {
            _busy = false;
            PromptBox.IsEnabled = true;
            PromptBox.Focus();
        }
    }

    private void SetStatus(string message)
    {
        StatusLabel.Text = message;
    }

    // CHATGPT ACCOUNT CONNECTION

    private async Task RefreshChatGptStatusAsync()
    {
        try
        {
            var status = await _chatGptAuth.GetStatusAsync();

            ChatGptStatusLabel.Text = status.Message;

            if (status.IsConnected)
            {
                ChatGptAccountLabel.Text =
                    string.IsNullOrWhiteSpace(status.Email)
                        ? "A ChatGPT account is connected."
                        : $"Signed in as {status.Email}";

                if (status.CanUseChatGptPlan)
                {
                    ChatGptAccountLabel.Text +=
                        Environment.NewLine +
                        "ChatGPT-plan permission: granted";
                }
                else
                {
                    ChatGptAccountLabel.Text +=
                        Environment.NewLine +
                        "ChatGPT-plan permission: not granted";
                }
            }
            else
            {
                ChatGptAccountLabel.Text =
                    "No saved ChatGPT account connection.";
            }

            ConnectChatGptButton.IsEnabled = !_chatGptAuthBusy;
            DisconnectChatGptButton.IsEnabled =
                status.IsConnected && !_chatGptAuthBusy;
        }
        catch (Exception ex)
        {
            ChatGptStatusLabel.Text =
                "Unable to read the saved connection.";

            ChatGptAccountLabel.Text = ex.Message;

            ConnectChatGptButton.IsEnabled = !_chatGptAuthBusy;
            DisconnectChatGptButton.IsEnabled = false;
        }
    }

    private async void ConnectChatGpt_Click(
        object sender, RoutedEventArgs e)
    {
        if (_chatGptAuthBusy)
            return;

        _chatGptAuthBusy = true;
        ConnectChatGptButton.IsEnabled = false;
        DisconnectChatGptButton.IsEnabled = false;

        ChatGptStatusLabel.Text =
            "Opening your browser for ChatGPT sign-in...";

        ChatGptAccountLabel.Text =
            "Complete authorization in your browser. Return here when finished.";

        SetStatus("Waiting for ChatGPT authorization...");

        try
        {
            var status = await _chatGptAuth.ConnectAsync();

            ChatGptStatusLabel.Text = status.Message;

            ChatGptAccountLabel.Text =
                string.IsNullOrWhiteSpace(status.Email)
                    ? "ChatGPT account connected."
                    : $"Signed in as {status.Email}";

            if (status.CanUseChatGptPlan)
            {
                ChatGptAccountLabel.Text +=
                    Environment.NewLine +
                    "ChatGPT-plan permission: granted";

                SetStatus("ChatGPT account connected.");
            }
            else
            {
                ChatGptAccountLabel.Text +=
                    Environment.NewLine +
                    "ChatGPT-plan permission: not granted";

                SetStatus(
                    "Signed in, but ChatGPT-plan access is not enabled.");
            }

            DisconnectChatGptButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            ChatGptStatusLabel.Text = "Sign-in was cancelled.";
            ChatGptAccountLabel.Text =
                "You can try connecting again whenever you're ready.";

            SetStatus("ChatGPT sign-in cancelled.");
        }
        catch (Exception ex)
        {
            ChatGptStatusLabel.Text = "ChatGPT sign-in failed.";
            ChatGptAccountLabel.Text = ex.Message;

            SetStatus("ChatGPT sign-in failed.");

            MessageBox.Show(
                ex.Message,
                "ChatGPT connection failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _chatGptAuthBusy = false;
            ConnectChatGptButton.IsEnabled = true;

            await RefreshChatGptStatusAsync();
        }
    }

    private async void DisconnectChatGpt_Click(
        object sender, RoutedEventArgs e)
    {
        if (_chatGptAuthBusy)
            return;

        var answer = MessageBox.Show(
            "Disconnect the saved ChatGPT account from Conflux? " +
            "This removes the locally stored credentials. It does not " +
            "delete your ChatGPT account.",
            "Disconnect ChatGPT",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await _chatGptAuth.DisconnectAsync();
            await RefreshChatGptStatusAsync();

            SetStatus("ChatGPT account disconnected.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Disconnect failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // NAVIGATION

    private void ShowView(
        Grid? chat = null,
        Grid? conversations = null,
        ScrollViewer? providers = null,
        ScrollViewer? appSettings = null)
    {
        ChatView.Visibility =
            chat is null ? Visibility.Collapsed : Visibility.Visible;

        ConversationsView.Visibility =
            conversations is null
                ? Visibility.Collapsed
                : Visibility.Visible;

        SettingsView.Visibility =
            providers is null
                ? Visibility.Collapsed
                : Visibility.Visible;

        AppSettingsView.Visibility =
            appSettings is null
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private void Workspace_Click(
        object sender, RoutedEventArgs e)
    {
        ShowView(chat: ChatView);
        RefreshRouteDisplay();
    }

    private void Conversations_Click(
        object sender, RoutedEventArgs e)
    {
        UpdateConversationList();
        ShowView(conversations: ConversationsView);
    }

    private void Settings_Click(
        object sender, RoutedEventArgs e)
    {
        RefreshProviderList();
        RefreshRoutingControls();
        ShowView(providers: SettingsView);
    }

    private void AppSettings_Click(
        object sender, RoutedEventArgs e)
    {
        GeneralReviewBox.IsChecked =
            GetCurrentRoute().EnableReview;

        ShowView(appSettings: AppSettingsView);
    }

    // NEW CHAT AND CONVERSATION HISTORY

    private void NewTask_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            MessageBox.Show(
                "Wait for the current AI request to finish before starting a new chat.",
                "Request in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ArchiveCurrentConversation();

        _activeConversation = null;
        _currentMessages = new List<ChatLine>();

        ChatMessages.Children.Clear();
        PromptBox.Clear();

        AddMessage(
            "System",
            "New conversation started. What would you like to work on?",
            "Conflux");

        ShowView(chat: ChatView);
        SetStatus("New conversation started.");
    }

    private void ArchiveCurrentConversation()
    {
        if (!_currentMessages.Any(m => m.Speaker == "You"))
            return;

        var firstPrompt = _currentMessages
            .First(m => m.Speaker == "You")
            .Content;

        var title = firstPrompt.Length > 48
            ? firstPrompt[..48] + "…"
            : firstPrompt;

        if (_activeConversation is null)
        {
            _activeConversation = new ConversationEntry();
            _conversations.Add(_activeConversation);
        }

        _activeConversation.Title = title;
        _activeConversation.Messages =
            new List<ChatLine>(_currentMessages);

        UpdateConversationList();
    }

    private void UpdateConversationList()
    {
        if (ConversationList is null)
            return;

        var selected = ConversationList.SelectedItem
            as ConversationEntry;

        _loadingConversation = true;

        ConversationList.ItemsSource = null;
        ConversationList.ItemsSource = _conversations
            .AsEnumerable()
            .Reverse()
            .ToList();

        if (selected is not null)
        {
            ConversationList.SelectedItem =
                ConversationList.Items
                    .Cast<ConversationEntry>()
                    .FirstOrDefault(c => ReferenceEquals(c, selected));
        }

        _loadingConversation = false;
    }

    private void ConversationList_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_loadingConversation ||
            ConversationList.SelectedItem is not ConversationEntry entry)
            return;

        if (_busy)
        {
            MessageBox.Show(
                "Wait for the current AI request to finish before switching conversations.",
                "Request in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _activeConversation = entry;
        _currentMessages = new List<ChatLine>(entry.Messages);

        ChatMessages.Children.Clear();

        foreach (var message in _currentMessages)
        {
            AddMessage(
                message.Speaker,
                message.Content,
                message.Provider,
                record: false);
        }

        PromptBox.Clear();
        ShowView(chat: ChatView);
        SetStatus($"Opened conversation: {entry.Title}");
    }

    // TASK TYPE AND COLLABORATION

    private void TaskType_Changed(
        object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TaskTypeBox is null)
            return;

        RefreshRouteDisplay();
    }

    private void ReviewQuick_Changed(
        object sender, RoutedEventArgs e)
    {
        if (_initializing || ReviewQuickBox is null)
            return;

        var enabled = ReviewQuickBox.IsChecked == true;

        GetCurrentRoute().EnableReview = enabled;

        if (ReviewBox is not null)
            ReviewBox.IsChecked = enabled;

        if (GeneralReviewBox is not null)
            GeneralReviewBox.IsChecked = enabled;
    }

    private void GeneralReview_Changed(
        object sender, RoutedEventArgs e)
    {
        if (_initializing || GeneralReviewBox is null)
            return;

        var enabled = GeneralReviewBox.IsChecked == true;

        GetCurrentRoute().EnableReview = enabled;

        if (ReviewBox is not null)
            ReviewBox.IsChecked = enabled;

        if (ReviewQuickBox is not null)
            ReviewQuickBox.IsChecked = enabled;
    }

    private void RefreshRouteDisplay()
    {
        if (TaskTypeBox is null ||
            PrimaryStatusLabel is null)
            return;

        var route = GetCurrentRoute();

        ActiveTaskLabel.Text = route.TaskType;

        PrimaryStatusLabel.Text =
            string.IsNullOrWhiteSpace(route.PrimaryProvider)
                ? "Not configured"
                : route.PrimaryProvider;

        SecondaryStatusLabel.Text =
            string.IsNullOrWhiteSpace(route.SecondaryProvider)
                ? "No backup selected"
                : route.SecondaryProvider;

        ReviewQuickBox.IsChecked = route.EnableReview;

        if (GeneralReviewBox is not null)
            GeneralReviewBox.IsChecked = route.EnableReview;
    }

    // PROVIDER CONFIGURATION

    private void RefreshProviderList()
    {
        ProviderList.ItemsSource = null;
        ProviderList.ItemsSource = _settings.Providers;

        RefreshRoutingControls();
    }

    private void RefreshRoutingControls()
    {
        if (PrimaryProviderBox is null)
            return;

        var names = _settings.Providers
            .Where(p => p.Enabled)
            .Select(p => p.Name)
            .ToList();

        PrimaryProviderBox.ItemsSource = names;
        SecondaryProviderBox.ItemsSource = names;

        if (RouteTaskBox.SelectedItem is null)
            RouteTaskBox.SelectedIndex = 0;

        LoadRouteIntoControls();
    }

    private void LoadRouteIntoControls()
    {
        if (RouteTaskBox.SelectedItem is null)
            return;

        var task = (RouteTaskBox.SelectedItem as ComboBoxItem)
            ?.Content?.ToString() ?? "General";

        var route = _settings.Routes.FirstOrDefault(r =>
            r.TaskType.Equals(
                task,
                StringComparison.OrdinalIgnoreCase));

        if (route is null)
        {
            PrimaryProviderBox.SelectedItem = null;
            SecondaryProviderBox.SelectedItem = null;
            ReviewBox.IsChecked = false;
            return;
        }

        PrimaryProviderBox.SelectedItem = route.PrimaryProvider;

        SecondaryProviderBox.SelectedItem =
            string.IsNullOrWhiteSpace(route.SecondaryProvider)
                ? null
                : route.SecondaryProvider;

        ReviewBox.IsChecked = route.EnableReview;
    }

    private void RouteTask_Changed(
        object sender, SelectionChangedEventArgs e)
    {
        if (_initializing ||
            PrimaryProviderBox is null)
            return;

        LoadRouteIntoControls();
    }

    private void ProviderList_Changed(
        object sender, SelectionChangedEventArgs e)
    {
        if (ProviderList.SelectedItem is not AiProvider provider)
            return;

        _selectedProvider = provider;

        ProviderNameBox.Text = provider.Name;
        ProviderUrlBox.Text = provider.BaseUrl;
        ProviderModelBox.Text = provider.Model;
        ProviderKeyBox.Password = provider.ApiKey;
        ProviderEnabledBox.IsChecked = provider.Enabled;
    }

    private void AddProvider_Click(
        object sender, RoutedEventArgs e)
    {
        var provider = new AiProvider
        {
            Name = "New Provider",
            BaseUrl = "",
            Model = "",
            ApiKey = "",
            Enabled = true
        };

        _settings.Providers.Add(provider);

        RefreshProviderList();
        ProviderList.SelectedItem = provider;
    }

    private async void SaveProvider_Click(
        object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
        {
            MessageBox.Show("Select a provider first.");
            return;
        }

        var name = ProviderNameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Provider name cannot be empty.");
            return;
        }

        var duplicate = _settings.Providers.Any(p =>
            !ReferenceEquals(p, _selectedProvider) &&
            p.Name.Equals(
                name,
                StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            MessageBox.Show(
                "Each provider needs a unique name.");
            return;
        }

        var oldName = _selectedProvider.Name;

        _selectedProvider.Name = name;
        _selectedProvider.BaseUrl = ProviderUrlBox.Text.Trim();
        _selectedProvider.Model = ProviderModelBox.Text.Trim();
        _selectedProvider.ApiKey = ProviderKeyBox.Password;
        _selectedProvider.Enabled =
            ProviderEnabledBox.IsChecked == true;

        foreach (var route in _settings.Routes)
        {
            if (route.PrimaryProvider == oldName)
                route.PrimaryProvider = name;

            if (route.SecondaryProvider == oldName)
                route.SecondaryProvider = name;
        }

        RefreshProviderList();
        ProviderList.SelectedItem = _selectedProvider;
        RefreshRouteDisplay();

        try
        {
            await _settingsService.SaveAsync(_settings);
            SetStatus("Provider saved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RemoveProvider_Click(
        object sender, RoutedEventArgs e)
    {
        if (_selectedProvider is null)
            return;

        var name = _selectedProvider.Name;

        if (MessageBox.Show(
            $"Remove provider '{name}'?",
            "Remove provider",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _settings.Providers.Remove(_selectedProvider);

        foreach (var route in _settings.Routes)
        {
            if (route.PrimaryProvider == name)
                route.PrimaryProvider = "";

            if (route.SecondaryProvider == name)
                route.SecondaryProvider = "";
        }

        _selectedProvider = null;

        ProviderNameBox.Clear();
        ProviderUrlBox.Clear();
        ProviderModelBox.Clear();
        ProviderKeyBox.Clear();

        RefreshProviderList();
        RefreshRouteDisplay();

        try
        {
            await _settingsService.SaveAsync(_settings);
            SetStatus("Provider removed.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void SaveRoute_Click(
        object sender, RoutedEventArgs e)
    {
        var task = (RouteTaskBox.SelectedItem as ComboBoxItem)
            ?.Content?.ToString() ?? "General";

        var primary = PrimaryProviderBox.SelectedItem?.ToString();

        if (string.IsNullOrWhiteSpace(primary))
        {
            MessageBox.Show("Choose a primary provider.");
            return;
        }

        var route = _settings.Routes.FirstOrDefault(r =>
            r.TaskType.Equals(
                task,
                StringComparison.OrdinalIgnoreCase));

        if (route is null)
        {
            route = new RoutingProfile { TaskType = task };
            _settings.Routes.Add(route);
        }

        route.PrimaryProvider = primary;
        route.SecondaryProvider =
            SecondaryProviderBox.SelectedItem?.ToString() ?? "";
        route.EnableReview = ReviewBox.IsChecked == true;

        try
        {
            await _settingsService.SaveAsync(_settings);

            RefreshRouteDisplay();
            SetStatus("Routing settings saved.");

            MessageBox.Show("Routing settings saved.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}