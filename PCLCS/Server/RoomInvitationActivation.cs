using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCLCS.Server;

public sealed class RoomInvitationActivation : IDisposable {
    private static readonly Regex Invitation = new(@"\Acpt://join/?#([A-Fa-f0-9]{16})\.([a-f0-9]{64})\z", RegexOptions.CultureInvariant);
    private readonly string pipeName;
    private readonly SecurityIdentifier user;
    private volatile bool stopped;
    private NamedPipeServerStream? server;
    private readonly object gate = new();

    public RoomInvitationActivation(string? name = null) {
        user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Missing Windows user.");
        using var sha = SHA256.Create();
        pipeName = name ?? "CPT.RoomInvitation." + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(user.Value))).Replace("-", "");
    }

    public static string Parse(string value, string website) {
        if (value == null || value.Length > 128) throw new ArgumentException("邀请链接格式无效。");
        var match = Invitation.Match(value);
        if (!match.Success) throw new ArgumentException("邀请链接格式无效。");
        return CommunityClient.ValidateBaseUrl(website) + "/room#" + match.Groups[1].Value.ToUpperInvariant() + "." + match.Groups[2].Value;
    }

    public static void Register(string executable) {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable) || executable.Contains('"')) throw new ArgumentException("Invalid executable.");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\cpt");
        key.SetValue("", "URL:CPT Room Invitation");
        key.SetValue("URL Protocol", "");
        using var icon = key.CreateSubKey("DefaultIcon");
        icon.SetValue("", "\"" + executable + "\",0");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", "\"" + executable + "\" \"%1\"");
    }

    public bool Send(string value, string website) {
        Parse(value, website);
        try {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            client.Connect(5000);
            var bytes = Encoding.ASCII.GetBytes(value + "\n");
            client.Write(bytes, 0, bytes.Length); client.Flush();
            var read = Task.Run(() => client.ReadByte());
            return read.Wait(5000) && read.Result == 1;
        } catch { return false; }
    }

    public void Start(string website, Action<string> receive) {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        var thread = new Thread(() => {
            while (!stopped) {
                try {
                    using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 256, 256, security);
                    lock (gate) { if (stopped) return; server = pipe; }
                    pipe.WaitForConnection();
                    // Bound an untrusted local client's read time and message size.
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var close = timeout.Token.Register(pipe.Dispose);
                    var bytes = new List<byte>();
                    while (bytes.Count <= 128) {
                        int next = pipe.ReadByte();
                        if (next < 0) throw new IOException("Incomplete activation.");
                        if (next == '\n') break;
                        bytes.Add((byte)next);
                    }
                    var link = Parse(Encoding.ASCII.GetString(bytes.ToArray()), website);
                    receive(link);
                    pipe.WriteByte(1); pipe.Flush();
                } catch { if (stopped) return; Thread.Sleep(100); }
                finally { lock (gate) server = null; }
            }
        }) { IsBackground = true, Name = "CPT room invitations" };
        thread.Start();
    }

    public void Dispose() { lock (gate) { stopped = true; server?.Dispose(); } }
}
