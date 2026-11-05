using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MdExplorer.Domain;
using MdExplorer.Infrastructure;
using MdExplorer.Platform.Abstractions;
using MdExplorer.UI.Preview;
using MdExplorer.UI.Services;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace MdExplorer.Tests;

/// <summary>
/// 合成 KDBX 3.1 / 4.x 向量。写库只出现在测试里——产品侧只有只读解析器，
/// 所以这里刻意用与解析器完全独立的代码路径（.NET AES + BouncyCastle 流密码）造文件。
/// </summary>
internal static class KdbxVectorBuilder
{
    private static readonly byte[] AesCipher =
        [0x31, 0xC1, 0xF2, 0xE6, 0xBF, 0x71, 0x43, 0x50, 0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF];

    private static readonly byte[] Sha512Iv = [0xE8, 0x30, 0x09, 0x4B, 0x97, 0x20, 0x5D, 0x2A];

    private static readonly byte[] Argon2dUuid =
        [0xEF, 0x63, 0x6D, 0xDF, 0x8C, 0x29, 0x44, 0x4B, 0x91, 0xF7, 0xA9, 0xA4, 0x03, 0xE3, 0x0A, 0x0C];

    /// <summary>KDBX 4.1，AES-KDF + AES-256-CBC，内层流 ChaCha20（KeePass 的默认组合）。</summary>
    public static byte[] BuildV4(
        string password,
        string xmlWithSecrets,
        params string[] secrets) =>
        BuildV4(password, xmlWithSecrets, secrets, useArgon2: false, rounds: 600, compress: true);

    public static byte[] BuildV4(
        string password,
        string xmlWithSecrets,
        string[] secrets,
        bool useArgon2,
        ulong rounds,
        bool compress)
    {
        var masterSeed = Random(32);
        var iv = Random(16);
        var protectedKey = Random(32);

        var salt = Random(32);
        var composite = Composite(password);
        var derived = useArgon2 ? Argon2(composite, salt, rounds) : AesTransform(composite, salt, rounds);

        var key = SHA256.HashData(Concat(masterSeed, derived));
        var hmacKey = SHA512.HashData(Concat(masterSeed, derived, [0x01]));

        var xml = ProtectXml(xmlWithSecrets, secrets, ProtectedStream.ChaCha20(protectedKey));
        var inner = InnerHeader(3, protectedKey);
        var payload = Concat(inner, Encoding.UTF8.GetBytes(xml));
        if (compress)
            payload = Gzip(payload);

        var cipherText = AesCbcEncrypt(key, iv, payload);

        var header = new List<byte>();
        header.AddRange(U32(0x9AA2D903));
        header.AddRange(U32(0xB54BFB67));
        header.AddRange(U32(0x00040001));
        header.AddRange(Field(2, AesCipher));
        header.AddRange(Field(3, U32(compress ? 1u : 0u)));
        header.AddRange(Field(4, masterSeed));
        header.AddRange(Field(7, iv));
        header.AddRange(Field(11, useArgon2
            ? Argon2Parameters(salt, rounds)
            : AesParameters(salt, rounds)));
        header.AddRange(Field(0, []));
        var headerBytes = header.ToArray();

        var file = new List<byte>(headerBytes);
        file.AddRange(SHA256.HashData(headerBytes));
        file.AddRange(Hmac(BlockKey(ulong.MaxValue, hmacKey), headerBytes));

        var blockIndex = 0ul;
        file.AddRange(HmacBlock(blockIndex++, cipherText, hmacKey));
        file.AddRange(HmacBlock(blockIndex, [], hmacKey));
        return [.. file];
    }

    /// <summary>KDBX 3.1：AES-KDF + AES-256-CBC，内层流 Salsa20（KeePass 2.x 的默认组合）。</summary>
    public static byte[] BuildV3(string password, string xmlWithSecrets, params string[] secrets) =>
        BuildV3(password, xmlWithSecrets, secrets, rounds: 400, compress: true);

    public static byte[] BuildV3(string password, string xmlWithSecrets, string[] secrets, ulong rounds, bool compress)
    {
        var masterSeed = Random(32);
        var iv = Random(16);
        var transformSeed = Random(32);
        var protectedKey = Random(32);
        var startBytes = Random(32);

        var composite = Composite(password);
        var derived = AesTransform(composite, transformSeed, rounds);
        var key = SHA256.HashData(Concat(masterSeed, derived));

        var xml = ProtectXml(xmlWithSecrets, secrets, ProtectedStream.Salsa20(protectedKey));
        var payload = Encoding.UTF8.GetBytes(xml);
        if (compress)
            payload = Gzip(payload);

        var blocks = new List<byte>();
        var index = 0u;
        foreach (var chunk in Chunk(payload, 1024))
        {
            blocks.AddRange(U32(index++));
            blocks.AddRange(SHA256.HashData(chunk));
            blocks.AddRange(U32((uint)chunk.Length));
            blocks.AddRange(chunk);
        }

        blocks.AddRange(U32(index));
        blocks.AddRange(new byte[32]);
        blocks.AddRange(U32(0));

        var plain = Concat(startBytes, [.. blocks]);
        var cipherText = AesCbcEncrypt(key, iv, plain);

        var file = new List<byte>();
        file.AddRange(U32(0x9AA2D903));
        file.AddRange(U32(0xB54BFB67));
        file.AddRange(U32(0x00030001));
        file.AddRange(V3Field(1, []));
        file.AddRange(V3Field(2, AesCipher));
        file.AddRange(V3Field(3, U32(compress ? 1u : 0u)));
        file.AddRange(V3Field(4, masterSeed));
        file.AddRange(V3Field(5, transformSeed));
        file.AddRange(V3Field(6, U64(rounds)));
        file.AddRange(V3Field(7, iv));
        file.AddRange(V3Field(8, protectedKey));
        file.AddRange(V3Field(9, startBytes));
        file.AddRange(V3Field(10, U32(2)));
        file.AddRange(cipherText);
        return [.. file];
    }

    /// <summary>测试用的密码库 XML；受保护字段用 {{0}}、{{1}} 占位，按文档顺序换成密文。</summary>
    public const string SampleXml =
        """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <KeePassFile>
          <Meta><DatabaseName>测试库</DatabaseName></Meta>
          <Root>
            <Group>
              <Name>Root</Name>
              <Entry>
                <String><Key>Title</Key><Value>GitHub</Value></String>
                <String><Key>UserName</Key><Value>ilwren</Value></String>
                <String><Key>Password</Key><Value Protected="True">{{0}}</Value></String>
                <String><Key>URL</Key><Value>https://github.com</Value></String>
                <String><Key>Notes</Key><Value>工作账号</Value></String>
              </Entry>
              <Group>
                <Name>Internet</Name>
                <Entry>
                  <String><Key>Title</Key><Value>邮箱</Value></String>
                  <String><Key>UserName</Key><Value>me@example.com</Value></String>
                  <String><Key>Password</Key><Value Protected="True">{{1}}</Value></String>
                </Entry>
              </Group>
            </Group>
          </Root>
        </KeePassFile>
        """;

    public static string Scramble(string xml, params string[] secrets) =>
        ProtectXml(xml, secrets, ProtectedStream.ChaCha20(Random(32)));

    private static string ProtectXml(string xml, string[] secrets, Func<byte[], byte[]> protect)
    {
        for (var i = 0; i < secrets.Length; i++)
            xml = xml.Replace($"{{{{{i}}}}}", Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(secrets[i]))));
        return xml;
    }

    private static byte[] InnerHeader(uint algorithm, byte[] key)
    {
        var buffer = new List<byte>();
        buffer.Add(1);
        buffer.AddRange(U32(4));
        buffer.AddRange(U32(algorithm));
        buffer.Add(2);
        buffer.AddRange(U32((uint)key.Length));
        buffer.AddRange(key);
        buffer.Add(0);
        buffer.AddRange(U32(0));
        return [.. buffer];
    }

    private static byte[] Field(byte id, byte[] value)
    {
        var buffer = new List<byte> { id };
        buffer.AddRange(U32((uint)value.Length));
        buffer.AddRange(value);
        return [.. buffer];
    }

    /// <summary>KDBX 3.1 的头部字段没有 id 字节（顺序固定），只有 uint16 长度前缀。</summary>
    private static byte[] V3Field(byte id, byte[] value)
    {
        _ = id;
        var buffer = new List<byte>();
        var size = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)value.Length);
        buffer.AddRange(size);
        buffer.AddRange(value);
        return [.. buffer];
    }

    private static byte[] AesParameters(byte[] seed, ulong rounds)
    {
        var buffer = new List<byte>();
        buffer.AddRange(U16(0x0100));
        buffer.AddRange(Entry(0x42, "$UUID", [0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60,
            0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA]));
        buffer.AddRange(Entry(0x42, "S", seed));
        buffer.AddRange(Entry(0x05, "R", U64(rounds)));
        buffer.Add(0);
        return [.. buffer];
    }

    private static byte[] Argon2Parameters(byte[] salt, ulong iterations)
    {
        var buffer = new List<byte>();
        buffer.AddRange(U16(0x0100));
        buffer.AddRange(Entry(0x42, "$UUID", Argon2dUuid));
        buffer.AddRange(Entry(0x42, "S", salt));
        buffer.AddRange(Entry(0x04, "P", U32(2)));
        buffer.AddRange(Entry(0x05, "M", U64(64L * 1024 * 1024))); // 单位是字节，与 Argon2 的 64 MiB 一致
        buffer.AddRange(Entry(0x05, "I", U64(iterations)));
        buffer.AddRange(Entry(0x04, "V", U32(0x13)));
        buffer.Add(0);
        return [.. buffer];
    }

    private static byte[] Entry(byte type, string key, byte[] value)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var buffer = new List<byte> { type };
        buffer.AddRange(U32((uint)keyBytes.Length));
        buffer.AddRange(keyBytes);
        buffer.AddRange(U32((uint)value.Length));
        buffer.AddRange(value);
        return [.. buffer];
    }

    private static byte[] HmacBlock(ulong index, byte[] data, byte[] hmacKey)
    {
        var message = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(message, index);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), data.Length);
        data.CopyTo(message, 12);
        var buffer = new List<byte>(32);
        buffer.AddRange(Hmac(BlockKey(index, hmacKey), message));
        buffer.AddRange(U32((uint)data.Length));
        buffer.AddRange(data);
        return [.. buffer];
    }

    private static byte[] BlockKey(ulong index, byte[] hmacKey) =>
        SHA512.HashData(Concat(U64(index), hmacKey));

    private static byte[] Hmac(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static byte[] Composite(string password)
    {
        var inner = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return SHA256.HashData(inner);
    }

    private static byte[] AesTransform(byte[] composite, byte[] seed, ulong rounds)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = seed;
        using var encryptor = aes.CreateEncryptor();
        var left = composite[..16];
        var right = composite[16..];
        var leftBuffer = new byte[16];
        var rightBuffer = new byte[16];
        for (ulong i = 0; i < rounds; i++)
        {
            encryptor.TransformBlock(left, 0, 16, leftBuffer, 0);
            encryptor.TransformBlock(right, 0, 16, rightBuffer, 0);
            (left, leftBuffer) = (leftBuffer, left);
            (right, rightBuffer) = (rightBuffer, right);
        }

        return SHA256.HashData(Concat(left, right));
    }

    private static byte[] Argon2(byte[] composite, byte[] salt, ulong iterations)
    {
        var builder = new Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Builder(
                Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Argon2d)
            .WithVersion(Org.BouncyCastle.Crypto.Parameters.Argon2Parameters.Version13)
            .WithSalt(salt)
            .WithParallelism(2)
            .WithMemoryAsKB(64 * 1024)
            .WithIterations((int)iterations);
        var generator = new Org.BouncyCastle.Crypto.Generators.Argon2BytesGenerator();
        generator.Init(builder.Build());
        var output = new byte[32];
        generator.GenerateBytes(composite, output);
        return output;
    }

    private static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(plain, 0, plain.Length);
    }

    private static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(data);
        return output.ToArray();
    }

    private static IEnumerable<byte[]> Chunk(byte[] data, int size)
    {
        for (var offset = 0; offset < data.Length; offset += size)
            yield return data[offset..Math.Min(offset + size, data.Length)];
    }

    private static byte[] Random(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var buffer = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(buffer, offset);
            offset += part.Length;
        }

        return buffer;
    }

    private static byte[] U16(ushort value) => [(byte)(value & 0xFF), (byte)(value >> 8)];

    private static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return buffer;
    }

    private static byte[] U64(ulong value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        return buffer;
    }

    /// <summary>内层流密码（与解析器实现互不相干的独立实现，正好互为验证）。</summary>
    private static class ProtectedStream
    {
        public static Func<byte[], byte[]> ChaCha20(byte[] key)
        {
            var hash = SHA512.HashData(key);
            var engine = new ChaCha7539Engine();
            engine.Init(true, new ParametersWithIV(
                new KeyParameter(hash[..32]), hash[32..44]));
            return data =>
            {
                var output = new byte[data.Length];
                engine.ProcessBytes(data, 0, data.Length, output, 0);
                return output;
            };
        }

        public static Func<byte[], byte[]> Salsa20(byte[] key)
        {
            var engine = new Salsa20Engine();
            engine.Init(true, new ParametersWithIV(
                new KeyParameter(SHA256.HashData(key)), Sha512Iv));
            return data =>
            {
                var output = new byte[data.Length];
                engine.ProcessBytes(data, 0, data.Length, output, 0);
                return output;
            };
        }
    }
}

/// <summary>KDBX 只读预览：解析器、提供者、预览面板三段各自可测。</summary>
public sealed class KeePassPreviewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mdexplorer-kdbx-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Kdbx4DecryptsGroupsEntriesAndProtectedValues()
    {
        var data = KdbxVectorBuilder.BuildV4("correct horse", KdbxVectorBuilder.SampleXml, ["s3cret-1", "s3cret-2"]);
        var database = KeePassReader.Read(data, "correct horse");

        Assert.Equal(4u, database.FileVersion >> 16);
        Assert.Equal("测试库", database.DatabaseName);
        Assert.Equal(2, database.Entries.Count);

        var github = Assert.Single(database.Entries, entry => entry.Title == "GitHub");
        Assert.Equal("ilwren", github.UserName);
        Assert.Equal("https://github.com", github.Url);
        Assert.Equal("工作账号", github.Notes);
        var password = Assert.Single(github.Fields, field => field.Key == "Password");
        Assert.True(password.IsProtected);
        Assert.Equal("s3cret-1", password.Value);

        var mail = Assert.Single(database.Entries, entry => entry.Title == "邮箱");
        Assert.Equal("Root / Internet", mail.GroupPath);
        Assert.Equal("s3cret-2", Assert.Single(mail.Fields, field => field.Key == "Password").Value);

        // 分组树：根分组累计 2 个条目，子分组 1 个。
        Assert.Equal(2, database.Root.EntryCount);
        var internet = Assert.Single(database.Root.Children);
        Assert.Equal("Internet", internet.Name);
        Assert.Equal(1, internet.EntryCount);
    }

    [Fact]
    public void Kdbx4RejectsWrongPasswordWithoutLeakingContent()
    {
        var data = KdbxVectorBuilder.BuildV4("correct horse", KdbxVectorBuilder.SampleXml, ["s3cret-1", "s3cret-2"]);
        var error = Assert.Throws<KeePassUnlockException>(() => KeePassReader.Read(data, "wrong"));

        Assert.Contains("主密码", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Kdbx4Argon2AndUncompressedPayloadsAlsoOpen()
    {
        var data = KdbxVectorBuilder.BuildV4(
            "argon", KdbxVectorBuilder.SampleXml, ["a-1", "a-2"],
            useArgon2: true, rounds: 2, compress: false);
        var database = KeePassReader.Read(data, "argon");

        Assert.Equal(2, database.Entries.Count);
        Assert.Equal("a-1", Assert.Single(database.Entries[0].Fields, field => field.Key == "Password").Value);
    }

    [Fact]
    public void Kdbx3Salsa20InnerStreamAlsoOpens()
    {
        var data = KdbxVectorBuilder.BuildV3("legacy", KdbxVectorBuilder.SampleXml, "old-1", "old-2");
        var database = KeePassReader.Read(data, "legacy");

        Assert.Equal(3u, database.FileVersion >> 16);
        Assert.Equal(2, database.Entries.Count);
        Assert.Equal("old-1", Assert.Single(database.Entries[0].Fields, field => field.Key == "Password").Value);
        Assert.Throws<KeePassUnlockException>(() => KeePassReader.Read(data, "nope"));
    }

    [Fact]
    public void ReaderRejectsForeignAndTruncatedFiles()
    {
        Assert.False(KeePassReader.IsKeePassDatabase("not a kdbx"u8));
        Assert.Throws<InvalidDataException>(() => KeePassReader.Read("not a kdbx"u8.ToArray(), "x"));

        var data = KdbxVectorBuilder.BuildV4("pw", KdbxVectorBuilder.SampleXml, ["s", "s2"]);
        Assert.Throws<InvalidDataException>(() => KeePassReader.Read(data[..^8], "pw"));
    }

    [Fact]
    public async Task ProviderOffersUnlockInsteadOfRawContent()
    {
        var path = Path.Combine(_root, "vault.kdbx");
        await File.WriteAllBytesAsync(
            path,
            KdbxVectorBuilder.BuildV4("pw", KdbxVectorBuilder.SampleXml, ["s1", "s2"]),
            TestContext.Current.CancellationToken);

        var registry = BuiltInPreviewProviders.Create(new ArchiveService(), new LocalFileSystemService());
        var item = new FileItem(
            FileLocator.FromPath(path), "vault.kdbx", FileKind.Unknown, new FileInfo(path).Length,
            DateTimeOffset.UnixEpoch, IsHidden: false, IsSymlink: false, ".kdbx");
        Assert.Equal("builtin.keepass", registry.Resolve(item)?.Id);

        var content = await registry.Resolve(item)!.LoadAsync(item, TestContext.Current.CancellationToken);
        Assert.NotNull(content.KeePassUnlock);
        Assert.Equal("vault.kdbx", content.KeePassUnlock!.FileName);
        Assert.NotEmpty(content.KeePassUnlock.Data); // 拿到的是密文，解密发生在用户输入主密码之后
        Assert.Null(content.Text);
        Assert.Null(content.Message);

        var spoof = Path.Combine(_root, "fake.kdbx");
        await File.WriteAllTextAsync(spoof, "definitely not a database", TestContext.Current.CancellationToken);
        var spoofItem = new FileItem(
            FileLocator.FromPath(spoof), "fake.kdbx", FileKind.Unknown, 24,
            DateTimeOffset.UnixEpoch, IsHidden: false, IsSymlink: false, ".kdbx");
        var spoofContent = await registry.Resolve(spoofItem)!.LoadAsync(spoofItem, TestContext.Current.CancellationToken);
        Assert.Null(spoofContent.KeePassUnlock);
        Assert.Contains("不是 KeePass", spoofContent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewPanelUnlocksMasksAndReleasesVault()
    {
        var start = Directory.CreateDirectory(Path.Combine(_root, "start")).FullName;
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        await File.WriteAllBytesAsync(
            Path.Combine(start, "vault.kdbx"),
            KdbxVectorBuilder.BuildV4("pw", KdbxVectorBuilder.SampleXml, ["s1", "s2"]),
            TestContext.Current.CancellationToken);

        var shell = AppHost.CreateShell(AppHost.Build(data, start));
        shell.Browser.SetWatchEnabled(false);
        await shell.OpenFolderAsync(start);
        var item = shell.Browser.Items.First(entry => entry.Name == "vault.kdbx");
        await shell.EnsurePreviewContentAsync(item);

        var preview = shell.Preview;
        Assert.True(preview.IsKeePassLocked);
        Assert.False(preview.HasKeePass);
        Assert.Equal("KeePass 密码库", preview.Subtitle);

        // 错误主密码：只出脱敏提示，仍停在锁定态。
        preview.KeePassPassword = "wrong";
        await preview.UnlockKeePassCommand.ExecuteAsync(null);
        Assert.True(preview.IsKeePassLocked);
        Assert.True(preview.HasKeePassError);
        Assert.DoesNotContain("GitHub", preview.KeePassErrorText, StringComparison.Ordinal);

        preview.KeePassPassword = "pw";
        await preview.UnlockKeePassCommand.ExecuteAsync(null);
        Assert.True(preview.HasKeePass);
        Assert.False(preview.IsKeePassLocked);
        Assert.Equal(string.Empty, preview.KeePassPassword); // 解锁成功立刻清掉明文密码
        Assert.Contains("2 个条目", preview.KeePassSummaryText, StringComparison.Ordinal);
        Assert.Equal(2, preview.KeePassEntries.Count);

        var github = preview.KeePassEntries.First(entry => entry.Title == "GitHub");
        preview.SelectKeePassEntry(github);
        var passwordRow = preview.KeePassFields.First(field => field.Key == "Password");
        Assert.Equal("••••••••", passwordRow.Value);
        preview.ShowKeePassSecrets = true;
        Assert.Equal("s1", preview.KeePassFields.First(field => field.Key == "Password").Value);

        // 分组过滤：选中子分组只看它自己的条目。
        preview.SelectKeePassGroup(preview.KeePassGroups.First(group => group.Name == "Internet"));
        Assert.Equal("邮箱", Assert.Single(preview.KeePassEntries).Title);

        // 关闭预览栏（Expanded+ 的关闭按钮就走这里）：条目、密文、主密码一起放掉。
        shell.ShowPreviewPane = false;
        Assert.False(preview.HasKeePass);
        Assert.Empty(preview.KeePassEntries);
        Assert.Empty(preview.KeePassFields);
        Assert.True(preview.IsPayloadReleased);
        Assert.False(preview.ShowKeePassSecrets);

        // 重新打开：按当前选集重新读库，回到锁定态（解出来的条目不会留在内存里）。
        shell.ShowPreviewPane = true;
        for (var attempt = 0; attempt < 150 && !preview.IsKeePassLocked; attempt++)
            await Task.Delay(20);
        Assert.True(
            preview.IsKeePassLocked,
            $"needs={preview.NeedsKeePassPassword} released={preview.IsPayloadReleased} " +
            $"has={preview.HasKeePass} item={preview.CurrentItem?.Name} fail={preview.FailureText} " +
            $"sel={shell.Browser.SelectedItems.Count} pane={shell.ShowPreviewPane}");
        Assert.False(preview.HasKeePass);
    }
}
