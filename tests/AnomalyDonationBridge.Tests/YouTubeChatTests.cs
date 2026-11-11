using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AnomalyDonationBridge.Persistence;
using AnomalyDonationBridge.YouTubeChat;
using AnomalyDonationBridge.YouTubeChat.Protocol;
using Google.Protobuf;
using Grpc.Core;

namespace AnomalyDonationBridge.Tests;

internal static class YouTubeChatTests
{
    private const string Key = "TestKey_not_a_real_credential_12345";
    private const string Video = "abc123_-XYZ";
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task RunAsync()
    {
        foreach (var url in new[] { Video, "https://www.youtube.com/watch?v="+Video+"&si=tracking", "youtube.com/live/"+Video,
                     "https://youtu.be/"+Video+"?t=1", "https://m.youtube.com/watch?v="+Video })
            Check(YouTubeVideoLink.Parse(url)==Video, "Valid video URL rejected: "+url);
        foreach (var url in new[] { "", "https://youtube.com/@streamer/live", "https://youtube.com.attacker.test/watch?v="+Video,
                     "https://youtube.com@attacker.test/watch?v="+Video, "http://youtube.com/watch?v="+Video,
                     "https://youtube.com:444/watch?v="+Video, "file:///test", "https://youtube.com/watch?v="+Video+"&v="+Video })
            await Error(() => Task.FromResult(YouTubeVideoLink.Parse(url)), "video_link");
        await Error(() => Task.FromResult(YouTubeVideoLink.NormalizeKey("bad\r\nkey")), "api_key");
        await TransportAsync();
        await SessionsAsync();
        await CompletedStreamsAsync();
        await PersistenceAsync();
    }
    private static async Task CompletedStreamsAsync()
    {
        Check(YouTubeChatSession.ContinuationDelay(TimeSpan.Zero) == TimeSpan.FromSeconds(5) &&
              YouTubeChatSession.ContinuationDelay(TimeSpan.FromSeconds(3)) == TimeSpan.FromSeconds(2) &&
              YouTubeChatSession.ContinuationDelay(TimeSpan.FromMinutes(1)) == TimeSpan.Zero,
            "Successful stream renewal must bound call cadence without delaying a long-lived stream");

        var fixture = new SessionFixture
        {
            Attempts = [([new([Message("one")], "cursor-1")], null),
                ([], null), // A quiet successful RPC can retain the previous cursor.
                ([new([], "cursor-2")], null), // Quiet chat still sends a continuation.
                ([new([Message("one"), Message("two")], "cursor-3")], null),
                ([new([], "cursor-4", true)], null)],
            CompleteReads = [1, 2, 3, 4]
        };
        var waits = new List<TimeSpan>();
        var states = new List<YouTubeChatState>();
        YouTubeChatSession? observed = null;
        await using (var session = new YouTubeChatSession(fixture, Key, Video, (delay, _) =>
        {
            waits.Add(delay);
            states.Add(observed!.Snapshot.State);
            Check(observed.Snapshot.ReceivedCount > 0, "Stream renewal cleared messages");
            return Task.CompletedTask;
        }))
        {
            observed = session;
            session.Start();
            await Until(() => session.Snapshot.State == YouTubeChatState.Ended);
            Check(fixture.ResolveCount == 1 && fixture.ReadCount == 5 &&
                  fixture.Tokens.SequenceEqual(new string?[] { null, "cursor-1", "cursor-1", "cursor-2", "cursor-3" }),
                "Successful EOF must continue the cursor, without re-resolving the video");
            Check(waits.Count == 4 && waits.All(x => x > TimeSpan.Zero && x <= TimeSpan.FromSeconds(5)) &&
                  states.All(x => x == YouTubeChatState.Connecting),
                "Successful/empty-message responses were incorrectly treated as escalating errors");
            Check(session.Snapshot.ReceivedCount == 2 && session.Snapshot.Messages.Count == 2,
                "Successful EOF replayed or lost messages");
        }

        // No response/cursor has ever been received: there is no safe continuation.
        fixture = new SessionFixture
        {
            Attempts = [([], null), ([new([], "")], null), ([new([], "end", true)], null)],
            CompleteReads = [1, 2]
        };
        waits.Clear();
        states.Clear();
        await using (var session = new YouTubeChatSession(fixture, Key, Video, (delay, _) =>
        { waits.Add(delay); states.Add(observed!.Snapshot.State); return Task.CompletedTask; }))
        {
            observed = session;
            session.Start();
            await Until(() => session.Snapshot.State == YouTubeChatState.Ended);
            Check(states.SequenceEqual(new[] { YouTubeChatState.Retrying, YouTubeChatState.Retrying }) &&
                  waits[0] >= TimeSpan.FromSeconds(5) && waits[1] >= TimeSpan.FromSeconds(10) &&
                  fixture.Tokens.All(x => x is null),
                "EOF before receiving a cursor must back off");
        }

        fixture = new SessionFixture
        {
            Attempts = [([], new YouTubeChatException("network", "Offline", true)),
                ([], new YouTubeChatException("network", "Offline", true)),
                ([new([], "healthy")], null),
                ([], new YouTubeChatException("network", "Offline", true)),
                ([new([], "end", true)], null)],
            CompleteReads = [3]
        };
        waits.Clear();
        await using (var session = new YouTubeChatSession(fixture, Key, Video, (delay, _) =>
        { waits.Add(delay); return Task.CompletedTask; }))
        {
            session.Start();
            await Until(() => session.Snapshot.State == YouTubeChatState.Ended);
            Check(waits.Count == 4 && waits[1] >= TimeSpan.FromSeconds(10) &&
                  waits[2] <= TimeSpan.FromSeconds(5) && waits[3] >= TimeSpan.FromSeconds(5) && waits[3] < TimeSpan.FromSeconds(7),
                "Successful renewal did not reset the previous network backoff");
        }

        fixture = new SessionFixture { Attempts = [([new([], "cursor")], null)], CompleteReads = [1] };
        var enteredWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new YouTubeChatSession(fixture, Key, Video, async (_, token) =>
        { enteredWait.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        stopped.Start();
        await enteredWait.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stopped.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(fixture.ReadCount == 1 && stopped.Snapshot.State == YouTubeChatState.Disconnected,
            "Disconnect during normal renewal opened another stream");
    }

    private static async Task TransportAsync()
    {
        var handler = new HttpFixture();
        using var http = new HttpClient(handler);
        var invoker = new ChatInvoker();
        using var transport = new YouTubeChatTransport(http, invoker);
        handler.Json = """{"items":[{"id":"abc123_-XYZ","snippet":{"title":"Тест эфира","channelTitle":"Канал"},"liveStreamingDetails":{"activeLiveChatId":"live-chat-id"}}]}""";
        var broadcast = await transport.ResolveAsync(Key, Video, default);
        Check(broadcast.ChatId=="live-chat-id" && broadcast.Title=="Тест эфира", "REST resolution failed");
        Check(handler.Url!.Host=="www.googleapis.com" && !handler.Url.ToString().Contains(Key) && handler.Key==Key,
            "REST key leaked to URL or host is not fixed");
        handler.Json="""{"items":[]}""";
        await Error(()=>transport.ResolveAsync(Key,Video,default),"not_found");
        handler.Json="""{"items":[{"liveStreamingDetails":{}}]}""";
        await Error(()=>transport.ResolveAsync(Key,Video,default),"no_chat");
        handler.Json="""{"items":[{"liveStreamingDetails":{"actualEndTime":"2026-01-01T12:00:00Z"}}]}""";
        await Error(()=>transport.ResolveAsync(Key,Video,default),"ended");
        handler.Status=HttpStatusCode.Forbidden;
        handler.Json="""{"error":{"message":"DO NOT DISPLAY API SECRET","errors":[{"reason":"quotaExceeded"}]}}""";
        var quota=await Error(()=>transport.ResolveAsync(Key,Video,default),"quota");
        Check(!quota.Retryable && !quota.Message.Contains("SECRET"),"Quota loops or reflects server message");
        handler.Status=HttpStatusCode.TooManyRequests; handler.Json="<html>gateway error</html>";
        handler.RetryAfter=TimeSpan.FromSeconds(180);
        var limited=await Error(()=>transport.ResolveAsync(Key,Video,default),"rate_limit");
        Check(limited.Retryable && limited.RetryAfter==handler.RetryAfter,"Non-JSON 429 lost Retry-After");

        // Encode using published tag numbers, independently of our generated setters.
        var snippet=Wire(w=> { w.WriteTag(1,WireFormat.WireType.Varint); w.WriteInt32(1);
            w.WriteTag(4,WireFormat.WireType.LengthDelimited); w.WriteString("2026-11-07T12:00:00Z");
            w.WriteTag(16,WireFormat.WireType.LengthDelimited); w.WriteString("Привет, чат!");
            w.WriteTag(17,WireFormat.WireType.Varint); w.WriteBool(true); });
        var author=Wire(w=> { w.WriteTag(10101,WireFormat.WireType.LengthDelimited); w.WriteString("channel-id");
            w.WriteTag(103,WireFormat.WireType.LengthDelimited); w.WriteString("Зритель");
            w.WriteTag(7,WireFormat.WireType.Varint); w.WriteBool(true); });
        var item=Wire(w=>{ w.WriteTag(101,WireFormat.WireType.LengthDelimited); w.WriteString("message-1");
            w.WriteTag(2,WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(snippet));
            w.WriteTag(3,WireFormat.WireType.LengthDelimited); w.WriteBytes(ByteString.CopyFrom(author)); });
        var response=Wire(w=>{w.WriteTag(100602,WireFormat.WireType.LengthDelimited);w.WriteString("next-token");
            w.WriteTag(1007,WireFormat.WireType.LengthDelimited);w.WriteBytes(ByteString.CopyFrom(item));});
        invoker.Responses=[LiveChatMessageListResponse.Parser.ParseFrom(response), new(){OfflineAt="2026-11-07T12:01:00Z"}];
        var batches=new List<YouTubeChatBatch>();
        await foreach(var batch in transport.ReadAsync(Key,"live-chat-id","resume-token",default)) batches.Add(batch);
        Check(invoker.Path=="/youtube.api.v3.V3DataLiveChatMessageService/StreamList" && invoker.Key==Key,"gRPC endpoint/auth mismatch");
        Check(invoker.Request!.PageToken=="resume-token" && invoker.Request.LiveChatId=="live-chat-id" &&
            invoker.Request.Part.SequenceEqual(new[]{"id","snippet","authorDetails"}),"gRPC request lost cursor or parts");
        Check(batches[0].Messages is [{Id:"message-1", AuthorChannelId:"channel-id", Author:"Зритель", Text:"Привет, чат!",IsModerator:true}]
              && batches[0].NextPageToken=="next-token" && batches[1].Ended,"Wire response mapping failed");
        invoker.Failure=new RpcException(new Status(StatusCode.ResourceExhausted,"DO NOT DISPLAY API SECRET"));
        var rpcError=await Error(async()=>{ await foreach(var _ in transport.ReadAsync(Key,"chat",null,default)){} },"quota");
        Check(!rpcError.Retryable && !rpcError.Message.Contains("SECRET"),"gRPC quota leaked or retries indefinitely");
        Check(YouTubeChatTransport.RpcError(new RpcException(new Status(StatusCode.Unavailable,"offline"))).Retryable,
            "Network error must reconnect");
    }

    private static byte[] Wire(Action<CodedOutputStream> write)
    { using var ms=new MemoryStream(); using(var w=new CodedOutputStream(ms,true)){write(w);w.Flush();} return ms.ToArray(); }

    private static async Task SessionsAsync()
    {
        var old=Message("old") with {PublishedAt=DateTimeOffset.UtcNow.AddMinutes(-5)};
        var fresh=Message("new") with {PublishedAt=DateTimeOffset.UtcNow.AddMinutes(1)};
        var fixture=new SessionFixture
        {
            Attempts=[([new([old],"cursor-1")], new YouTubeChatException("network","Offline",true)),
                ([new([old,fresh],"cursor-2",true)],null)]
        };
        var waits=new List<TimeSpan>();
        await using(var session=new YouTubeChatSession(fixture,Key,Video,(delay,_)=>{waits.Add(delay);return Task.CompletedTask;}))
        {
            session.Start(); await Until(()=>session.Snapshot.State==YouTubeChatState.Ended);
            Check(fixture.ResolveCount==1 && fixture.Tokens.SequenceEqual(new string?[]{null,"cursor-1"}),"Reconnect failed to resume");
            Check(waits.Count==1 && waits[0]>=TimeSpan.FromSeconds(5),"Reconnect busy-loop");
            Check(session.Snapshot.ReceivedCount==2 && session.Snapshot.Messages[0].Id=="new" &&
                  !session.Snapshot.Messages[0].IsHistorical && session.Snapshot.Messages[1].IsHistorical,"Dedup/history attribution failed");
        }
        fixture=new SessionFixture { Attempts=[([],new YouTubeChatException("rate_limit","Slow",true,TimeSpan.FromSeconds(180))),
            ([],new YouTubeChatException("network","Offline",true)),([],new YouTubeChatException("quota","Quota",retryAfter:TimeSpan.FromMinutes(1)))] };
        waits.Clear();
        await using(var session=new YouTubeChatSession(fixture,Key,Video,(delay,_)=>{waits.Add(delay);return Task.CompletedTask;}))
        {
            session.Start();await Until(()=>session.Snapshot.State==YouTubeChatState.Error);
            Check(fixture.ReadCount==3 && waits.Count==2 && waits[0]>=TimeSpan.FromSeconds(180) && waits[1]>=TimeSpan.FromSeconds(10),"Backoff/Retry-After not honoured");
            Check(session.RetryNotBefore>DateTimeOffset.UtcNow,"Manual quota reconnect not throttled");
        }
        fixture=new SessionFixture { Attempts=[([new(Enumerable.Range(0,200).Select(i=>Message(i.ToString())).ToArray(),"bounded")],null)] };
        var liveSession=new YouTubeChatSession(fixture,Key,Video);
        liveSession.Start();await Until(()=>liveSession.Snapshot.State==YouTubeChatState.Connected);
        Check(liveSession.Snapshot.Messages.Count==50 && liveSession.Snapshot.ReceivedCount==200,"Preview is unbounded");
        await liveSession.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Check(fixture.Cancelled && liveSession.Snapshot.State==YouTubeChatState.Disconnected,"Disconnect left stream running");
    }
    private static YouTubeChatMessage Message(string id)=>new(id,"channel","User","text",DateTimeOffset.UtcNow,1);
    private static async Task Until(Func<bool> predicate)
    { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)); while(!predicate()) await Task.Delay(10,timeout.Token); }
    private static async Task<YouTubeChatException> Error(Func<Task> action,string code)
    { try{await action();}catch(YouTubeChatException e){Check(e.Code==code,$"Expected {code}; got {e.Code}");return e;} throw new InvalidOperationException("Expected "+code); }

    private static async Task PersistenceAsync()
    {
        var directory=Path.Combine(Environment.GetEnvironmentVariable("ADB_TEST_ROOT")??Path.GetTempPath(),"youtube-"+Guid.NewGuid().ToString("N"));
        try
        {
            var paths=new AppPaths(directory);var store=new DpapiSecretStore(paths);
            var bundle=new SecretBundle {YouTubeApiKey=Key, DonateXExternalToken="donatex-test", AccessToken="oauth-test"};
            await Task.WhenAll(Enumerable.Range(0,8).Select(_=>store.SaveAsync(bundle)));
            Check((await store.LoadAsync()).YouTubeApiKey==Key && !File.ReadAllText(paths.SecretsFile).Contains(Key),"Protected key persistence failed");
            bundle.YouTubeApiKey=""; await store.SaveAsync(bundle);
            var remaining=await store.LoadAsync();
            Check(!remaining.HasYouTubeApiKey && remaining.DonateXExternalToken=="donatex-test" && remaining.AccessToken=="oauth-test",
                "Forgetting YouTube key damaged other integrations");
            var settings=new AppSettings {YouTubeChatEnabled=true,YouTubeChatVideoUrl=YouTubeVideoLink.Canonical(Video)};
            var projected=SettingsNormalizer.CreatePersistenceSnapshot(settings);
            Check(projected.YouTubeChatEnabled && projected.YouTubeChatVideoUrl==settings.YouTubeChatVideoUrl,"Settings snapshot lost connection");
            Check(!JsonSerializer.Serialize(projected).Contains(Key) && !new AppSettings().YouTubeChatEnabled,"Secret exported or chat enabled by default");
        }
        finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }

    private sealed class HttpFixture:HttpMessageHandler
    {
        public string Json="{}";public HttpStatusCode Status=HttpStatusCode.OK;public TimeSpan? RetryAfter;public Uri? Url;public string? Key;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Url=request.RequestUri; Key=request.Headers.GetValues("X-Goog-Api-Key").Single();
            var response=new HttpResponseMessage(Status){Content=new StringContent(Json,Encoding.UTF8,"application/json")};
            if(RetryAfter is {} delay)response.Headers.RetryAfter=new(delay);
            return Task.FromResult(response);
        }
    }
    private sealed class ChatInvoker:CallInvoker
    {
        public LiveChatMessageListResponse[] Responses=[];public RpcException? Failure;public string? Path;public string? Key;public LiveChatMessageListRequest? Request;
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest,TResponse>(Method<TRequest,TResponse> method,string? host,CallOptions options,TRequest request)
        {
            Path=method.FullName;Key=options.Headers!.GetValue("x-goog-api-key"); Request=(LiveChatMessageListRequest)(object)request;
            return new(new Reader<TResponse>(Responses.Select(x=>(TResponse)(object)x).ToArray(),Failure),Task.FromResult(new Metadata()),()=>Status.DefaultSuccess,()=>new Metadata(),()=>{});
        }
        public override TResponse BlockingUnaryCall<TRequest,TResponse>(Method<TRequest,TResponse> m,string? h,CallOptions o,TRequest r)=>throw new NotSupportedException();
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest,TResponse>(Method<TRequest,TResponse> m,string? h,CallOptions o,TRequest r)=>throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest,TResponse> AsyncClientStreamingCall<TRequest,TResponse>(Method<TRequest,TResponse> m,string? h,CallOptions o)=>throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest,TResponse> AsyncDuplexStreamingCall<TRequest,TResponse>(Method<TRequest,TResponse> m,string? h,CallOptions o)=>throw new NotSupportedException();
    }
    private sealed class Reader<T>(T[] messages,Exception? failure):IAsyncStreamReader<T>
    {
        private int _index=-1;public T Current=>messages[_index];
        public Task<bool> MoveNext(CancellationToken token)=>failure is null?Task.FromResult(++_index<messages.Length):Task.FromException<bool>(failure);
    }
    private sealed class SessionFixture:IYouTubeChatTransport
    {
        public (YouTubeChatBatch[] Batches,YouTubeChatException? Error)[] Attempts=[];
        public HashSet<int> CompleteReads=[];
        public readonly List<string?> Tokens=[];public int ResolveCount;public int ReadCount;public bool Cancelled;
        public Task<YouTubeBroadcast> ResolveAsync(string key,string video,CancellationToken token)
        {ResolveCount++;return Task.FromResult(new YouTubeBroadcast(video,"chat","title","channel"));}
        public async IAsyncEnumerable<YouTubeChatBatch> ReadAsync(string key,string chat,string? cursor,[EnumeratorCancellation]CancellationToken token)
        {
            Tokens.Add(cursor);var attempt=Attempts[ReadCount++];
            foreach(var batch in attempt.Batches)yield return batch;
            if(attempt.Error is {} error)throw error;
            if(CompleteReads.Contains(ReadCount))yield break;
            try {await Task.Delay(Timeout.Infinite,token);} finally {Cancelled=token.IsCancellationRequested;}
        }
    }
}
