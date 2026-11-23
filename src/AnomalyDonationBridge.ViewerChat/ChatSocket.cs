using System.Net.WebSockets;
using System.Text;

namespace AnomalyDonationBridge.ViewerChat;

internal static class ChatSocket
{
    internal static Task SendAsync(ClientWebSocket socket, string text, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);

    internal static async Task<string> ReadAsync(ClientWebSocket socket, CancellationToken token, bool handshake = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (handshake) deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token).ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close) throw new ViewerChatException("Чат закрыл соединение.");
            if (part.MessageType != WebSocketMessageType.Text) throw new ViewerChatException("Неизвестный формат ответа чата.");
            output.Write(buffer, 0, part.Count);
            if (output.Length > 1_048_576) throw new ViewerChatException("Слишком большой ответ чата.");
        } while (!part.EndOfMessage);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}
