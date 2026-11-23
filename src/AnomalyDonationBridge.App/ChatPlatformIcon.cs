using System.Windows;
using System.Windows.Media;
using AnomalyDonationBridge.ViewerChat;

namespace AnomalyDonationBridge.App;

public sealed class ChatPlatformIcon : FrameworkElement
{
    public static readonly DependencyProperty PlatformProperty = DependencyProperty.Register(nameof(Platform),
        typeof(ChatPlatform), typeof(ChatPlatformIcon), new FrameworkPropertyMetadata(ChatPlatform.YouTube,
            FrameworkPropertyMetadataOptions.AffectsRender));
    public ChatPlatform Platform { get => (ChatPlatform)GetValue(PlatformProperty); set => SetValue(PlatformProperty, value); }
    private static readonly Brush Youtube = Frozen("#E74747"), Twitch = Frozen("#A978F0"), Vk = Frozen("#3486E8");
    private static Brush Frozen(string color) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); b.Freeze(); return b; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / 16, size / 16));
        if (Platform == ChatPlatform.YouTube)
        {
            dc.DrawRoundedRectangle(Youtube, null, new Rect(0, 2, 16, 12), 3, 3);
            dc.DrawGeometry(Brushes.White, null, Geometry.Parse("M6,5 L11,8 L6,11 Z"));
        }
        else if (Platform == ChatPlatform.Twitch)
        {
            dc.DrawGeometry(Twitch, null, Geometry.Parse("M2,0 L16,0 L16,10 L11,15 L7,15 L4,16 L4,13 L0,13 L0,3 Z"));
            dc.DrawGeometry(Brushes.White, null, Geometry.Parse("M4,2 L14,2 L14,9 L10,12 L7,12 L5,14 L5,12 L3,12 L3,3 Z"));
            dc.DrawRectangle(Twitch, null, new Rect(6, 4, 2, 5));
            dc.DrawRectangle(Twitch, null, new Rect(10, 4, 2, 5));
        }
        else
        {
            dc.DrawRoundedRectangle(Vk, null, new Rect(0, 0, 16, 16), 3.5, 3.5);
            dc.DrawGeometry(Brushes.White, null, Geometry.Parse(
                "M2,4 L4,4 L6,9 L6,4 L8,4 L8,7 L11,4 L14,4 L10,8 L14,12 L11,12 L8,9 L8,12 L6,12 C4,11 3,8 2,4 Z"));
        }
        dc.Pop(); dc.Pop();
    }
}
