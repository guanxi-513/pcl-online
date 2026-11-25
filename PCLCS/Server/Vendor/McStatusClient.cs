// Altered .NET Framework port of MCStatus.NET by Stefan Koch (2023).
// Source: https://github.com/Saalvage/MCStatus.NET. See LICENSE.MCStatus.txt.
// Uses Newtonsoft.Json, bounded synchronous reads, network-order ports and socket timeouts.
using System.Net.Sockets;

namespace PCLCS.Server;

internal static class McStatusClient {
    private static TcpClient Connect(string host, int port) {
        var resolution = Dns.GetHostAddressesAsync(host);
        if (!resolution.Wait(3000)) throw new TimeoutException("服务器 DNS 查询超时。");
        var clients = resolution.Result.Distinct().Take(32).Select(address => (Socket: new TcpClient(address.AddressFamily), Address: address)).ToList();
        if (clients.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        var pending = clients.Select(client => (Socket: client.Socket, Task: client.Socket.ConnectAsync(client.Address, port))).ToList();
        TcpClient? selected = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        try {
            while (pending.Count > 0) {
                int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remaining <= 0) break;
                int index = Task.WaitAny(pending.Select(client => client.Task).ToArray(), remaining);
                if (index < 0) break;
                var candidate = pending[index]; pending.RemoveAt(index);
                if (candidate.Task.Status == TaskStatus.RanToCompletion && candidate.Socket.Connected) { selected = candidate.Socket; return selected; }
                _ = candidate.Task.Exception;
            }
            throw new TimeoutException("服务器连接未成功，可能超时或端口拒绝连接。");
        } finally {
            foreach (var client in clients) if (!ReferenceEquals(client.Socket, selected)) client.Socket.Dispose();
            foreach (var item in pending) _ = item.Task.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
    public static JObject GetStatus(string host, int port, string? handshakeHost = null, int protocol = 767, Action? connected = null) {
        using var tcp = Connect(host, port);
        tcp.ReceiveTimeout = 5000;
        connected?.Invoke();
        tcp.SendTimeout = 5000;
        using var stream = tcp.GetStream();
        using var handshake = new MemoryStream();
        WriteVarInt(handshake, 0);
        WriteVarInt(handshake, protocol);
        var address = Encoding.UTF8.GetBytes(handshakeHost ?? host);
        WriteVarInt(handshake, address.Length);
        handshake.Write(address, 0, address.Length);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)port);
        WriteVarInt(handshake, 1);
        WriteVarInt(stream, (int)handshake.Length);
        var bytes = handshake.ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Write(new byte[] { 1, 0 }, 0, 2);
        var length = ReadVarInt(stream);
        if (length is < 3 or > 1048576) throw new InvalidDataException("服务器状态响应过大或无效。");
        var packet = new byte[length];
        var offset = 0;
        while (offset < length) {
            var read = stream.Read(packet, offset, length - offset);
            if (read == 0) throw new EndOfStreamException("服务器提前关闭连接。");
            offset += read;
        }
        using var input = new MemoryStream(packet);
        if (ReadVarInt(input) != 0) throw new InvalidDataException("无效的 Minecraft 状态响应。");
        var jsonLength = ReadVarInt(input);
        if (jsonLength < 0 || jsonLength != input.Length - input.Position) throw new InvalidDataException("服务器状态长度无效。");
        return JObject.Parse(Encoding.UTF8.GetString(packet, (int)input.Position, jsonLength));
    }

    private static void WriteVarInt(Stream stream, int value) {
        while (true) {
            if ((value & ~0x7F) == 0) { stream.WriteByte((byte)value); return; }
            stream.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>>= 7;
        }
    }

    private static int ReadVarInt(Stream stream) {
        var value = 0;
        for (var position = 0; position < 35; position += 7) {
            var next = stream.ReadByte();
            if (next < 0) throw new EndOfStreamException();
            value |= (next & 0x7F) << position;
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("无效的 VarInt。");
    }
}
