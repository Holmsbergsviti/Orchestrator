// =====================================================================================
// FILE PURPOSE (in plain terms):
//   Tests for encrypted fleet data (heartbeats, logs, screenshots): the agent's
//   FleetDataCipher and the console's byte-identical copy agree with each other, an older/
//   unconfigured agent's plain bytes still come through unchanged, a blob for the wrong key
//   (or garbage) is refused rather than returning nonsense, and FleetDataEncryption only
//   encrypts when a build actually has a key baked in.
// =====================================================================================

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrator.Console;              // console side: DataEncryptionKeys, ConsoleCipher
using Xunit;
using AgentCipher = Orchestrator.Service.Services.FleetDataCipher;
using ConsoleCipher = Orchestrator.Console.FleetDataCipher;

namespace Orchestrator.Service.Tests;

public class FleetDataEncryptionTests
{
    private static ECDiffieHellman NewRecipient() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void WhatTheAgentSeals_TheConsoleOpens()
    {
        using var recipient = NewRecipient();
        var plaintext = System.Text.Encoding.UTF8.GetBytes("""{"machineId":"m1","hostname":"PC1"}""");

        var sealedBytes = AgentCipher.Encrypt(recipient.ExportParameters(false), plaintext);
        Assert.True(ConsoleCipher.IsEncrypted(sealedBytes));
        Assert.True(ConsoleCipher.TryDecrypt(recipient, sealedBytes, out var opened));
        Assert.Equal(plaintext, opened);
    }

    [Fact]
    public void TheWrongPrivateKey_CannotOpenIt()
    {
        using var recipient = NewRecipient();
        using var impostor = NewRecipient();
        var sealedBytes = AgentCipher.Encrypt(recipient.ExportParameters(false), new byte[] { 1, 2, 3 });

        Assert.False(ConsoleCipher.TryDecrypt(impostor, sealedBytes, out _));
    }

    [Fact]
    public void GarbageOrPlainBytes_AreNotMistakenForEncrypted()
    {
        var plain = System.Text.Encoding.UTF8.GetBytes("""{"machineId":"m1"}""");   // an older agent's heartbeat
        Assert.False(ConsoleCipher.IsEncrypted(plain));
        Assert.False(ConsoleCipher.IsEncrypted(new byte[] { 1, 2, 3 }));   // far too short either way
    }

    [Fact]
    public void TamperedCiphertext_FailsToOpen()
    {
        using var recipient = NewRecipient();
        var sealedBytes = AgentCipher.Encrypt(recipient.ExportParameters(false), new byte[] { 9, 9, 9 });
        sealedBytes[^1] ^= 0xFF;   // flip a byte inside the GCM tag

        Assert.False(ConsoleCipher.TryDecrypt(recipient, sealedBytes, out _));
    }

    [Fact]
    public void FleetDataEncryption_WithNoBakedInKey_PassesDataThroughUnchanged()
    {
        var enc = new Service.Services.FleetDataEncryption(publicKeyBase64: null);
        Assert.False(enc.Enabled);
        var plain = new byte[] { 1, 2, 3 };
        Assert.Same(plain, enc.EncryptIfEnabled(plain));
    }

    [Fact]
    public void FleetDataEncryption_WithABakedInKey_EncryptsForTheMatchingPrivateKey()
    {
        using var recipient = NewRecipient();
        var publicKeyBase64 = Convert.ToBase64String(recipient.ExportSubjectPublicKeyInfo());

        var enc = new Service.Services.FleetDataEncryption(publicKeyBase64);
        Assert.True(enc.Enabled);

        var plain = System.Text.Encoding.UTF8.GetBytes("hello fleet");
        var sealedBytes = enc.EncryptIfEnabled(plain);
        Assert.True(ConsoleCipher.IsEncrypted(sealedBytes));
        Assert.True(ConsoleCipher.TryDecrypt(recipient, sealedBytes, out var opened));
        Assert.Equal(plain, opened);
    }

    [Fact]
    public void DataEncryptionKeys_CreatesOnce_AndReusesTheSameKeyAfter()
    {
        var path = Path.Combine(Path.GetTempPath(), "data-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            var first = new DataEncryptionKeys(path);
            var (_, created1) = first.LoadOrCreate();
            Assert.True(created1);
            var publicKey1 = first.PublicKeyBase64;

            var second = new DataEncryptionKeys(path);
            var (_, created2) = second.LoadOrCreate();
            Assert.False(created2);
            Assert.Equal(publicKey1, second.PublicKeyBase64);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DataEncryptionKeys_DecryptIfNeeded_PassesThroughPlainDataFromOlderAgents()
    {
        var keys = new DataEncryptionKeys(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid().ToString("N") + ".pem"),
            NullLogger<DataEncryptionKeys>.Instance);
        var plain = System.Text.Encoding.UTF8.GetBytes("""{"machineId":"m1"}""");
        Assert.Equal(plain, keys.DecryptIfNeeded(plain, "state/m1.json"));
        Assert.Null(keys.DecryptIfNeeded(null, "state/m1.json"));
    }

    [Fact]
    public void DataEncryptionKeys_WithNoKeySetUp_DropsDataItCannotOpen()
    {
        using var recipient = NewRecipient();
        var sealedBytes = AgentCipher.Encrypt(recipient.ExportParameters(false), new byte[] { 1, 2, 3 });

        var keys = new DataEncryptionKeys(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".pem"),
            NullLogger<DataEncryptionKeys>.Instance);
        Assert.Null(keys.DecryptIfNeeded(sealedBytes, "state/m1.json"));
    }

    [Fact]
    public void DataEncryptionKeys_RoundTripsWhatAnAgentSealedToIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "data-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            var console = new DataEncryptionKeys(path);
            console.LoadOrCreate();
            var agentEnc = new Service.Services.FleetDataEncryption(console.PublicKeyBase64);

            var plain = System.Text.Encoding.UTF8.GetBytes("""{"machineId":"m1","antivirus":["Avast Free Antivirus"]}""");
            var uploaded = agentEnc.EncryptIfEnabled(plain);
            var read = console.DecryptIfNeeded(uploaded, "state/m1.json");

            Assert.Equal(plain, read);
        }
        finally { File.Delete(path); }
    }
}
