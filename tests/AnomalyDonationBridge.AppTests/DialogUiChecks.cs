using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;

namespace AnomalyDonationBridge.AppTests;

// Detached WPF rendering only: no user profile, window display or real actions.
internal static class DialogUiChecks
{
    internal static int Run(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var app = new AnomalyDonationBridge.App.App(startRuntime: false);
            app.InitializeComponent();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            foreach (var light in new[] { false, true })
            {
                ThemeManager.Apply(light);
                foreach (var (id, title, message, text, confirmation, danger) in new[]
                {
                    ("preset", "Удалить пользовательский шаблон", "Удалить пользовательский шаблон «Телепортация на случайную локацию»?", "Удалить", true, true),
                    ("queue", "Очистить очередь", "Удалить все ещё не отправленные эффекты из очереди? Уже переданный игре эффект отменён не будет, но если игра его отложит, он не вернётся в очередь.", "Очистить", true, true),
                    ("update", "Доступно обновление", "Доступна новая версия.\n\nОткрыть страницу релиза в браузере?", "Открыть", true, false),
                    ("notice", "Anomaly Donation Bridge", "Отметьте хотя бы одну секцию.", "ОК", false, false),
                    ("long", "Сообщение", string.Join("\n", Enumerable.Repeat("Длинный текст с подробностями: окно должно оставаться в пределах экрана и прокручиваться.", 40)), "ОК", false, false)
                })
                {
                    var window = new AppDialogWindow(title, message, text, confirmation, danger);
                    var root = (FrameworkElement)window.Content;
                    root.Measure(new Size(520, double.PositiveInfinity));
                    root.Arrange(new Rect(0, 0, 520, Math.Ceiling(root.DesiredSize.Height)));
                    root.UpdateLayout();
                    var buttons = Find<Button>(root).ToArray();
                    Check(buttons.Length == (confirmation ? 2 : 1), "Unexpected dialog button count");
                    var primary = buttons.Single(button => Equals(button.Content, text));
                    Check(primary.IsDefault == !confirmation, "Destructive action cannot be the Enter default");
                    if (confirmation)
                        Check(buttons.Single(button => Equals(button.Content, "Отмена")) is { IsDefault: true, IsCancel: true }, "Cancel must be the safe default and Escape action");
                    Check(root.ActualHeight < 700 && root.ActualWidth == 520, "Long message overflowed dialog bounds");
                    Check(window.Background == (Brush)app.FindResource("BackgroundBrush"), "Background ignores current theme");
                    Check((primary.Style == (Style)window.FindResource("DangerButtonStyle")) == danger, "Wrong destructive/neutral button style");
                    var bitmap = new RenderTargetBitmap(520, (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(output, $"dialog-{id}-{(light ? "light" : "dark")}.png"))) encoder.Save(stream);
                    window.Close();
                }
                var runtime = new BridgeRuntime(new AppPaths(Path.Combine(output, "isolated-settings-unused")));
                var viewModel = new SettingsViewModel(runtime);
                var settings = new SettingsWindow(viewModel);
                var settingsRoot = (FrameworkElement)settings.Content;
                var tabs = Find<TabControl>(settingsRoot).Single();
                tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => Equals(tab.Header, "Общие настройки"));
                settingsRoot.Measure(new Size(940, 640)); settingsRoot.Arrange(new Rect(0, 0, 940, 640)); settingsRoot.UpdateLayout();
                var retention = Find<TextBox>(settingsRoot).Single(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "EffectSaveRetentionCount");
                Check(retention.Text == "50" && viewModel.EffectSaveRetentionCount == 50, "General settings do not show the default 50 saves");
                retention.Text = "17"; retention.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                Check(viewModel.EffectSaveRetentionCount == 17 && runtime.Settings.EffectSaveRetentionCount == 50, "Retention edit is not bound or bypasses normal settings Save");
                var heading = Find<TextBlock>(settingsRoot).Single(block => block.Text == "Автоматические сохранения эффектов");
                var saveCard = (Border)((StackPanel)heading.Parent).Parent;
                // The card is below the scroll viewport in this detached window.
                // Render it in isolation instead of capturing its ancestor's clip.
                ((Panel)saveCard.Parent).Children.Remove(saveCard);
                saveCard.DataContext = viewModel;
                saveCard.Margin = new Thickness(0);
                settings.Content = null;
                var cardHost = new ContentControl
                {
                    Content = saveCard,
                    Foreground = (Brush)app.FindResource("TextBrush"),
                    FontFamily = new FontFamily("Segoe UI"), FontSize = 13
                };
                settings.Content = cardHost;
                cardHost.Measure(new Size(880, double.PositiveInfinity));
                cardHost.Arrange(new Rect(0, 0, 880, Math.Ceiling(cardHost.DesiredSize.Height)));
                cardHost.UpdateLayout();
                var cardBitmap = new RenderTargetBitmap((int)Math.Ceiling(cardHost.ActualWidth), (int)Math.Ceiling(cardHost.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                cardBitmap.Render(cardHost);
                var pixels = new byte[cardBitmap.PixelWidth * cardBitmap.PixelHeight * 4];
                cardBitmap.CopyPixels(pixels, cardBitmap.PixelWidth * 4, 0);
                Check(Enumerable.Range(0, pixels.Length / 4).Count(index => pixels[index * 4 + 3] > 0) > pixels.Length / 8,
                    "Save retention card rendered blank");
                var cardEncoder = new PngBitmapEncoder(); cardEncoder.Frames.Add(BitmapFrame.Create(cardBitmap));
                using (var stream = File.Create(Path.Combine(output, $"settings-save-retention-{(light ? "light" : "dark")}.png"))) cardEncoder.Save(stream);
                settings.Close();
            }
            Console.WriteLine("PASS themed silent dialogs and save-retention card: both themes, default 50, editable binding, no live settings mutation, nonblank render, safe cancel default and bounded long messages.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var found in Find<T>(VisualTreeHelper.GetChild(root, i))) yield return found;
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
