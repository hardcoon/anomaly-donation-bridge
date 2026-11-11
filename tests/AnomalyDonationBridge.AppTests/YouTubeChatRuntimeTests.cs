using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using AnomalyDonationBridge.App.Services;
using AnomalyDonationBridge.App.ViewModels;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.YouTubeChat;

namespace AnomalyDonationBridge.AppTests;

internal static class YouTubeChatRuntimeTests
{
    internal static async Task RunAsync()
    {
        using var directory=new Program.TemporaryDirectory();
        var paths=new AppPaths(Path.Combine(directory.Path,"data"));
        var transport=new FakeTransport();
        var runtime=new BridgeRuntime(paths){YouTubeTransportFactory=()=>transport};
        var entries=0;runtime.HistoryAdded+=_=>entries++;
        const string key="LocalFakeYouTubeKey_ForTests_12345";
        try {await runtime.ConnectYouTubeChatAsync("abc123_-XYZ");throw new InvalidOperationException("Connected without a saved key");}
        catch(YouTubeChatException e){Check(e.Code=="key_missing","Wrong missing-key error");}

        var setup=new SettingsViewModel(runtime);
        Check(setup.YouTubeVideoUrl.Length==0,"Test must start without a broadcast");
        await setup.SaveYouTubeApiKeyAsync(key);
        var secrets=await new DpapiSecretStore(paths).LoadAsync();
        Check(setup.YouTubeHasApiKey && secrets.YouTubeApiKey==key && !runtime.Settings.YouTubeChatEnabled &&
              runtime.Settings.YouTubeChatVideoUrl.Length==0 && transport.ResolveCalls==0,
            "Saving a key requires a broadcast or starts a connection");
        Check(setup.YouTubeKeyHint.Contains("Ключ сохранён",StringComparison.Ordinal),"Saved key status missing");
        await setup.DisconnectYouTubeChatAsync();
        var keyOnlySettings=await new JsonSettingsStore(paths).LoadAsync();
        await using(var keyOnlyRestart=new BridgeRuntime(paths){YouTubeTransportFactory=()=>throw new InvalidOperationException("Key setup started API")})
        {
            typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Settings))!.SetValue(keyOnlyRestart,keyOnlySettings);
            typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Secrets))!.SetValue(keyOnlyRestart,secrets);
            await (Task)typeof(BridgeRuntime).GetMethod("ResumeYouTubeChatAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(keyOnlyRestart,null)!;
            Check(new SettingsViewModel(keyOnlyRestart).YouTubeHasApiKey,"Key-only setup did not survive restart");
        }
        try {await runtime.SaveYouTubeApiKeyAsync("bad");throw new InvalidOperationException("Invalid replacement accepted");}
        catch(YouTubeChatException){Check(runtime.Secrets.YouTubeApiKey==key,"Invalid input erased saved key");}
        try {await runtime.ConnectYouTubeChatAsync("");throw new InvalidOperationException("Empty broadcast connected");}
        catch(YouTubeChatException){Check(runtime.Secrets.YouTubeApiKey==key,"Empty broadcast erased saved key");}

        setup.YouTubeVideoUrl="https://youtu.be/abc123_-XYZ";
        await setup.ConnectYouTubeChatAsync();
        await Until(()=>runtime.YouTubeChat.State==YouTubeChatState.Connected);
        Check(runtime.YouTubeChat.ReceivedCount==1 && entries==0 && runtime.CanArmDonationProcessing && !runtime.Settings.Armed,
            "Chat message escaped into donation processing");
        Check(runtime.Settings.YouTubeChatVideoUrl=="https://www.youtube.com/watch?v=abc123_-XYZ","Video link was not normalized");
        try {await runtime.ConnectYouTubeChatAsync("abc123_-XYZ");throw new InvalidOperationException("Repeated click bypassed reconnect guard");}
        catch(YouTubeChatException e){Check(e.Code=="reconnect_wait","Wrong repeated-connect error");}
        await Task.Delay(TimeSpan.FromSeconds(5.1));
        setup.YouTubeVideoUrl="https://www.youtube.com/live/ZYX-_321cba";
        await setup.ConnectYouTubeChatAsync();
        await Until(()=>runtime.YouTubeChat.State==YouTubeChatState.Connected);
        Check(transport.ResolveCalls==2 && transport.Video=="ZYX-_321cba" && transport.Key==key,
            "Switching broadcasts did not reuse saved key");
        const string replacement="ReplacementFakeYouTubeKey_ForTests_12345";
        await setup.SaveYouTubeApiKeyAsync(replacement);
        Check(runtime.YouTubeChat.State==YouTubeChatState.Connected && transport.ResolveCalls==2 && transport.Key==key,
            "Saving a replacement unexpectedly interrupted the running stream");
        await setup.SaveYouTubeApiKeyAsync(key);
        await runtime.DisposeAsync();
        Check(transport.Cancelled && transport.Disposed,"Closing app left a chat connection running");
        var settings=await new JsonSettingsStore(paths).LoadAsync();
        secrets=await new DpapiSecretStore(paths).LoadAsync();
        Check(settings.YouTubeChatEnabled && secrets.YouTubeApiKey==key,"Shutdown erased reconnect preferences");

        var resumedTransport=new FakeTransport();
        await using var restored=new BridgeRuntime(paths){YouTubeTransportFactory=()=>resumedTransport};
        // Load only the chat state; don't start the game's named pipe or donation providers in this test.
        typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Settings))!.SetValue(restored,settings);
        typeof(BridgeRuntime).GetProperty(nameof(BridgeRuntime.Secrets))!.SetValue(restored,secrets);
        await (Task)typeof(BridgeRuntime).GetMethod("ResumeYouTubeChatAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(restored,null)!;
        await Until(()=>restored.YouTubeChat.State==YouTubeChatState.Connected);
        Check(resumedTransport.Key==key,"Restart did not reuse protected key");
        await restored.DisconnectYouTubeChatAsync();
        Check(resumedTransport.Cancelled && !restored.Settings.YouTubeChatEnabled && restored.Secrets.HasYouTubeApiKey,
            "Disconnect did not cancel or forgot key");
        restored.Secrets.DonateXExternalToken="other-provider-test-key";
        await restored.DisconnectYouTubeChatAsync(forgetKey:true);
        secrets=await new DpapiSecretStore(paths).LoadAsync();
        settings=await new JsonSettingsStore(paths).LoadAsync();
        Check(!settings.YouTubeChatEnabled && !secrets.HasYouTubeApiKey && secrets.DonateXExternalToken=="other-provider-test-key",
            "Forget-key did not persist or damaged DonateX");
        Check(entries==0,"YouTube changed donation history");
        await YouTubeVotingRuntimeTests.RunAsync();
    }
    private static async Task Until(Func<bool> predicate)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!predicate())await Task.Delay(10,timeout.Token);}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class FakeTransport:IYouTubeChatTransport,IDisposable
    {
        public bool Cancelled;public bool Disposed;public string? Key;public string? Video;public int ResolveCalls;
        public Task<YouTubeBroadcast> ResolveAsync(string key,string video,CancellationToken token)
        {ResolveCalls++;Key=key;Video=video;return Task.FromResult(new YouTubeBroadcast(video,"chat","Stream","Channel"));}
        public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string key,string chat,string? cursor,[EnumeratorCancellation]CancellationToken token)
        {
            yield return new([new("message","channel","Donor","!spawn_btr",DateTimeOffset.Now,1)],"cursor");
            try{await Task.Delay(Timeout.Infinite,token);}finally{Cancelled=token.IsCancellationRequested;}
        }
        public void Dispose()=>Disposed=true;
    }
}
