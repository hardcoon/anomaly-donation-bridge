using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AnomalyDonationBridge.App;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.AppTests;

// Render the actual settings, detached from the desktop, accounts and game.
internal static class YouTubeChatUiChecks
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var app=new AnomalyDonationBridge.App.App(startRuntime:false); app.InitializeComponent();
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        using var temporary=new Program.TemporaryDirectory();
        var runtime=new BridgeRuntime(new AppPaths(Path.Combine(temporary.Path,"data")));
        var trace=new StringWriter();var listener=new TextWriterTraceListener(trace);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level=SourceLevels.Error;
        try
        {
            foreach(var light in new[]{false,true})
            foreach(var width in new[]{900,1120})
            foreach(var state in new[]{"empty","key-saved","connected","help","error"})
            {
                ThemeManager.Apply(light);
                var keySaved=state is "key-saved" or "connected" or "error";
                runtime.Secrets.YouTubeApiKey=keySaved?"LocalFakeYouTubeKey_ForRendering_12345":"";
                var snapshot=YouTubeChatSnapshot.Empty;
                if(state=="connected") snapshot=new(YouTubeChatState.Connected,"Подключено · сообщения поступают напрямую из YouTube",
                    new("abc123_-XYZ","chat","Тестовая трансляция — Зона сегодня","Hardcoon"),3,
                    [new("3","c","ОченьДлинноеИмяЗрителя"+new string('я',100),new string('т',500),DateTimeOffset.Now,1),
                    new("2","b","Зритель","Привет! Сообщения приходят?",DateTimeOffset.Now,1),
                    new("1","a","Hardcoon","Проверка подключения YouTube 👋",DateTimeOffset.Now,1,true,false,true)],1);
                if(state=="error") snapshot=snapshot with{State=YouTubeChatState.Error,
                    Status="Нет доступа к чату. Проверьте ключ, включение YouTube Data API v3 и ограничения ключа в Google Cloud."};
                typeof(BridgeRuntime).GetField("_youTubeIdle",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(runtime,snapshot);
                var vm=new SettingsViewModel(runtime);
                var window=new SettingsWindow(vm);
                var root=(FrameworkElement)window.Content;
                root.Margin=new Thickness(0);
                var outer=(TabControl)window.FindName("SettingsTabs");
                outer.SelectedItem=outer.Items.OfType<TabItem>().Single(tab=>Equals(tab.Header,"Интеграция"));
                Layout(root,width);
                var youtube=(TabItem)window.FindName("YouTubeChatTab");
                var inner=ItemsControl.ItemsControlFromItemContainer(youtube) as TabControl;
                Check(inner is not null,"Missing integration tabs");
                Check(inner!.Items.Count==3 && ReferenceEquals(inner.Items[2],youtube),"YouTube is not the third integration");
                inner.SelectedItem=youtube;
                if(state=="help")((ToggleButton)window.FindName("YouTubeHelpToggle")).IsChecked=true;
                Layout(root,width);
                Check(((PasswordBox)window.FindName("YouTubeKeyBox")).Password=="","Stored key populated a UI text field");
                Check(((Button)window.FindName("YouTubeSaveKeyButton")).IsEnabled,"Key save depends on a broadcast");
                Check(((Button)window.FindName("YouTubeConnectButton")).IsEnabled==keySaved,"Broadcast connection ignores saved key state");
                Check(((Button)window.FindName("YouTubeForgetKeyButton")).IsEnabled==keySaved,"Forget-key state is stale");
                Check(((Button)window.FindName("YouTubeSaveKeyButton")).Content?.ToString()==(keySaved?"Заменить ключ":"Сохранить ключ"),"Key action label is stale");
                foreach(var button in Find<Button>((DependencyObject)youtube.Content))
                {
                    if(!button.IsVisible && button.ActualWidth==0) continue;
                    Check(button.ActualWidth>0,"Zero-width YouTube action");
                    var bounds=button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                    Check(bounds.Left>=0 && bounds.Right<=root.ActualWidth+1,"YouTube action overflow: "+button.Content);
                }
                Check(vm.YouTubeMessages.Count==snapshot.Messages.Count,"Preview binding dropped messages");
                var bitmap=new RenderTargetBitmap(width,740,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(var file=File.Create(Path.Combine(output,$"youtube-{(light?"light":"dark")}-{width}-{state}.png")))encoder.Save(file);
                window.Close();
            }
            listener.Flush();Check(trace.ToString().Length==0,"WPF binding errors: "+trace);
            Console.WriteLine("YouTube WPF: 20 detached layouts passed; no network or game commands.");
            return 0;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);listener.Dispose();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();app.Shutdown();
        }
    }
    private static void Layout(FrameworkElement root,int width)
    {root.Measure(new Size(width,740));root.Arrange(new Rect(0,0,width,740));root.UpdateLayout();}
    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T t)yield return t;foreach(var nested in Find<T>(child))yield return nested;}}
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
