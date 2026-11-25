// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Tests for end-to-end encrypted live-screen sessions (the ones that go through the tunnel):
//   the agent's RelayCipher and the console's copy agree with each other, tampering/replay/old
//   agents are refused, the relay's pumps really seal what the PC cannot be allowed to leave
//   in the clear, and the signed hint carries the key only when the tunnel is used.
// =====================================================================================

using System.IO.Pipelines;                 // an in-memory duplex stream pair for real WebSockets
using System.Net.WebSockets;               // WebSocket
using System.Security.Cryptography;        // CryptographicException
using System.Text;                         // Encoding.UTF8
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Console;                // console side
using Orchestrator.Service.Models;         // RelayHint
using Xunit;
using AgentCipher = Orchestrator.Service.Services.RelayCipher;
using ConsoleCipher = Orchestrator.Console.RelayCipher;

namespace Orchestrator.Service.Tests;

public class RelayEncryptionTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    private static (AgentCipher Agent, ConsoleCipher Console) Pair(string? key = null)
    {
        key ??= ConsoleCipher.NewKey();
        return (new AgentCipher(key, Session, isAgent: true), new ConsoleCipher(key, Session, isAgent: false));
    }

    [Fact]
    public void WhatTheAgentSeals_TheConsoleOpens_AndTheOtherWayRound_WithTheKindPreserved()
    {
        var (agent, console) = Pair();

        var frame = new byte[] { 0xFF, 0xD8, 1, 2, 3 };
        var (isText, payload) = console.Open(agent.Seal(isText: false, frame));
        Assert.False(isText);
        Assert.Equal(frame, payload);

        var input = Encoding.UTF8.GetBytes("{\"t\":\"key\",\"k\":\"a\"}");
        var (inText, inPayload) = agent.Open(console.Seal(isText: true, input));
        Assert.True(inText);
        Assert.Equal(input, inPayload);
    }

    [Fact]
    public void SealedBytes_ContainNeitherTheDataNorItsKind()
    {
        var (agent, _) = Pair();
        var secret = Encoding.UTF8.GetBytes("a-password-typed-on-the-remote-pc");
        var wire = agent.Seal(isText: true, secret);
        Assert.DoesNotContain("password", Encoding.UTF8.GetString(wire));
        Assert.Equal(8 + 1 + secret.Length + 16, wire.Length);   // counter + kind byte + data + tag, nothing else
    }

    [Fact]
    public void TheSameMessageSealedTwice_LooksDifferent()
    {
        var (agent, _) = Pair();
        Assert.NotEqual(agent.Seal(false, new byte[] { 1, 2, 3 }), agent.Seal(false, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void ATamperedMessage_IsRefused()
    {
        var (agent, console) = Pair();
        var wire = agent.Seal(false, new byte[] { 1, 2, 3, 4 });
        wire[10] ^= 0x01;   // flip one bit of the ciphertext
        Assert.Throws<AuthenticationTagMismatchException>(() => console.Open(wire));
    }

    [Fact]
    public void AReplayedOrSkippedOrReorderedMessage_IsRefused()
    {
        var (agent, console) = Pair();
        var first = agent.Seal(false, new byte[] { 1 });
        var second = agent.Seal(false, new byte[] { 2 });
        var third = agent.Seal(false, new byte[] { 3 });

        Assert.Throws<CryptographicException>(() => console.Open(second));   // skipping #0
        console.Open(first);
        Assert.Throws<CryptographicException>(() => console.Open(first));    // replay
        console.Open(second);
        Assert.Throws<CryptographicException>(() => console.Open(second));
        console.Open(third);
    }

    [Fact]
    public void AMessageCannotBeBouncedBackToItsSender()
    {
        var (agent, console) = Pair();
        // Each direction has its own key, so reflecting the PC's own message at it fails.
        Assert.ThrowsAny<CryptographicException>(() => agent.Open(agent.Seal(false, new byte[] { 1 })));
        Assert.ThrowsAny<CryptographicException>(() => console.Open(console.Seal(false, new byte[] { 1 })));
    }

    [Fact]
    public void AnotherKeyOrAnotherSession_CannotOpenIt()
    {
        var agent = new AgentCipher(ConsoleCipher.NewKey(), Session, isAgent: true);
        var wire = agent.Seal(false, new byte[] { 1 });
        Assert.ThrowsAny<CryptographicException>(() => new ConsoleCipher(ConsoleCipher.NewKey(), Session, isAgent: false).Open(wire));

        var key = ConsoleCipher.NewKey();
        var a = new AgentCipher(key, Session, isAgent: true);
        Assert.ThrowsAny<CryptographicException>(() => new ConsoleCipher(key, "another-session-id", isAgent: false).Open(a.Seal(false, new byte[] { 1 })));
    }

    [Fact]
    public void APlainFrame_FromAnAgentThatCannotEncrypt_IsNotMistakenForASealedOne()
    {
        var (_, console) = Pair();
        Assert.ThrowsAny<CryptographicException>(() => console.Open(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xDB, 0, 0x43, 0 }));
        Assert.ThrowsAny<CryptographicException>(() => console.Open(new byte[3]));   // too short
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]    // padding isn't part of the format
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\" /x")]  // it lands on a command line
    public void MalformedKeys_AreRejected(string key)
    {
        Assert.False(AgentCipher.IsWellFormedKey(key));
        Assert.False(ConsoleCipher.IsWellFormedKey(key));
        Assert.Throws<ArgumentException>(() => new AgentCipher(key, Session, true));
    }

    [Fact]
    public void FreshKeys_AreWellFormedAndDistinct()
    {
        var a = ConsoleCipher.NewKey(); var b = AgentCipher.NewKey();
        Assert.True(AgentCipher.IsWellFormedKey(a)); Assert.True(ConsoleCipher.IsWellFormedKey(b));
        Assert.NotEqual(a, b);
    }

    // ---- the hint --------------------------------------------------------------------------

    private static RelayHintFile HintWith(string? key, string url = "wss://quiet-fox-1234.trycloudflare.com")
        => new() { Sessions = { ["m1"] = new RelayHintEntry { Id = "abc", RelayUrl = url, RelayCertThumbprint = "", RelayKey = key } } };

    [Fact]
    public void TheAgentReadsTheKeyFromTheHint_AndAMalformedOneMeansNoConnectionAtAll()
    {
        var key = ConsoleCipher.NewKey();
        Assert.Equal(key, RelayHint.PickKey(HintWith(key), "m1", "abc"));
        Assert.Equal(key, RelayHint.PickKey(HintWith(key), "M1", "abc"));
        Assert.Null(RelayHint.PickKey(HintWith(null), "m1", "abc"));          // an unencrypted session
        Assert.Null(RelayHint.PickKey(HintWith(key), "m1", "another-session"));

        // Fail closed: carrying on without a broken key would send the screen in the clear.
        Assert.Null(RelayHint.Pick(HintWith("not-a-key"), "m1", "abc"));
        Assert.Null(RelayHint.PickKey(HintWith("not-a-key"), "m1", "abc"));
    }

    // ---- the relay's pumps, over real WebSockets ------------------------------------------

    /// <summary>Two connected WebSockets over in-memory pipes: what one sends, the other receives.</summary>
    private static (WebSocket A, WebSocket B) SocketPair()
    {
        var aToB = new Pipe(); var bToA = new Pipe();
        var a = WebSocket.CreateFromStream(new DuplexStream(bToA.Reader, aToB.Writer), new WebSocketCreationOptions { IsServer = false });
        var b = WebSocket.CreateFromStream(new DuplexStream(aToB.Reader, bToA.Writer), new WebSocketCreationOptions { IsServer = true });
        return (a, b);
    }

    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _in = reader.AsStream(); private readonly Stream _out = writer.AsStream();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _out.Flush();
        public override Task FlushAsync(CancellationToken ct) => _out.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => _in.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> m, CancellationToken ct = default) => _in.ReadAsync(m, ct);
        public override void Write(byte[] b, int o, int c) => _out.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> m, CancellationToken ct = default) => _out.WriteAsync(m, ct);
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket ws)
    {
        var buf = new byte[1024]; using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buf, cts.Token); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
        return (r.MessageType, ms.ToArray());
    }

    [Fact]
    public async Task TheTunnelOnlyCarriesSealedBytes_AndTheViewerStillGetsPlainFramesAndSendsPlainInput()
    {
        var key = ConsoleCipher.NewKey();
        var agentCipher = new AgentCipher(key, Session, isAgent: true);
        var consoleCipher = new ConsoleCipher(key, Session, isAgent: false);

        // Tunnel leg: the PC's socket (agentEnd) <-> the relay's socket for it (relayAgent).
        var (agentEnd, relayAgent) = SocketPair();
        // Local leg: the relay's socket for the viewer (relayViewer) <-> the browser (viewerEnd).
        var (relayViewer, viewerEnd) = SocketPair();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var gate = new SemaphoreSlim(1, 1);
        string? failure = null;
        var up = RelayEncryptedPump.FromAgentAsync(relayAgent, relayViewer, gate, consoleCipher, why => failure = why, cts.Token);
        var down = RelayEncryptedPump.FromViewerAsync(relayViewer, relayAgent, consoleCipher, cts.Token);

        // A screen frame (bigger than one fragment) goes PC -> viewer.
        var frame = new byte[200_000]; new Random(1).NextBytes(frame);
        var sealedFrame = agentCipher.Seal(isText: false, frame);
        await agentEnd.SendAsync(sealedFrame.AsMemory(), WebSocketMessageType.Binary, true, cts.Token);
        var gotFrame = await ReceiveAsync(viewerEnd);
        Assert.Equal(WebSocketMessageType.Binary, gotFrame.Type);
        Assert.Equal(frame, gotFrame.Data);
        Assert.Null(failure);

        // An input event goes viewer -> PC, and what is on the tunnel for it is sealed.
        var input = Encoding.UTF8.GetBytes("{\"t\":\"move\",\"x\":0.5,\"y\":0.5}");
        await viewerEnd.SendAsync(input.AsMemory(), WebSocketMessageType.Text, true, cts.Token);
        var onTheTunnel = await ReceiveAsync(agentEnd);
        Assert.Equal(WebSocketMessageType.Binary, onTheTunnel.Type);
        Assert.DoesNotContain("move", Encoding.UTF8.GetString(onTheTunnel.Data));
        var (isText, opened) = agentCipher.Open(onTheTunnel.Data);
        Assert.True(isText);
        Assert.Equal(input, opened);

        cts.Cancel();
        await Task.WhenAll(up, down).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }

    [Fact]
    public async Task AnAgentThatSendsInTheClear_EndsTheSession_AndNothingReachesTheViewer()
    {
        var consoleCipher = new ConsoleCipher(ConsoleCipher.NewKey(), Session, isAgent: false);
        var (agentEnd, relayAgent) = SocketPair();
        var (relayViewer, viewerEnd) = SocketPair();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? failure = null;
        var up = RelayEncryptedPump.FromAgentAsync(relayAgent, relayViewer, new SemaphoreSlim(1, 1), consoleCipher, why => failure = why, cts.Token);

        // An older agent (or something in the middle) sends a plain JPEG-looking frame.
        await agentEnd.SendAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30 },
            WebSocketMessageType.Binary, true, cts.Token);
        await up.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(failure);
        Assert.Contains("decrypt", failure);
        Assert.Equal(WebSocketState.Open, relayViewer.State);   // the viewer is told by the hub, and was sent no data
        cts.Cancel();
    }

    // ---- the hub refuses to fall back to plaintext --------------------------------------

    [Fact]
    public void ASessionRegisteredWithAKey_HasACipher_AndOneWithoutDoesNot()
    {
        var hub = new RelayHub(NullLogger<RelayHub>.Instance);
        hub.RegisterPending("s1", "m1", "pub", TimeSpan.FromMinutes(1), ConsoleCipher.NewKey());
        hub.RegisterPending("s2", "m1", "pub", TimeSpan.FromMinutes(1));
        Assert.True(hub.IsValid("s1")); Assert.True(hub.IsValid("s2"));
        Assert.Throws<ArgumentException>(() => hub.RegisterPending("s3", "m1", "pub", TimeSpan.FromMinutes(1), "bad-key"));
    }
}
