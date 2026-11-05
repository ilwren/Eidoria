using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Formatting = MdExplorer.Domain.Formatting;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace MdExplorer.UI.Preview;

/// <summary>KeePass 条目里的一个字段。受保护字段（密码、TOTP 等）默认遮罩显示。</summary>
public sealed record KeePassField(string Key, string Value, bool IsProtected);

/// <summary>扁平化后的条目行：预览面板只读展示，不含任何写回能力。</summary>
public sealed record KeePassEntryNode(
    string GroupPath,
    string Title,
    string UserName,
    string Url,
    string Notes,
    IReadOnlyList<KeePassField> Fields);

/// <summary>分组节点（含子分组累计条目数），用于预览面板的分组列表。</summary>
public sealed record KeePassGroupNode(
    string Path,
    string Name,
    int EntryCount,
    IReadOnlyList<KeePassGroupNode> Children);

/// <summary>只读解析结果。主密码与派生的加密密钥都不保留在这里。</summary>
public sealed record KeePassDatabase(
    KeePassGroupNode Root,
    IReadOnlyList<KeePassEntryNode> Entries,
    uint FileVersion,
    string? DatabaseName,
    bool Truncated);

/// <summary>预览面板的解锁请求：提供者只认出「这是 KeePass 库」，主密码由用户当场输入。</summary>
public sealed record KeePassUnlockRequest(string FileName, byte[] Data);

/// <summary>解锁失败（密码错、文件被改、需要密钥文件）。文案不含任何库内容。</summary>
public sealed class KeePassUnlockException(string message) : Exception(message);

/// <summary>
/// KDBX 3.1 / 4.x 只读解析器（自写实现，只用 BouncyCastle 的密码学原语：Argon2、ChaCha20、Salsa20、Twofish；
/// AES 与哈希走 .NET 内置实现）。
/// <para>
/// 刻意不引入 KeePassLib / ModernKeePassLib / KeePassXC（均为 GPL），也不引入 Pidamg.KeePass.Kdbx
/// （MIT，但接口面向读写一体）。这里只做单向链路：读头 → 派生密钥 → 校验 HMAC → 解密 → 解压 →
/// 解析 XML。不写盘、不回写、不缓存主密码。
/// </para>
/// <para>
/// 安全边界：主密码只在本方法栈上以 UTF-8 字节存在，用完即清零；异常文案一律不含库内容
/// （分组名、标题、字段值都不进异常）；条目数、字段数、字符串长度、XML 总长都有上限，
/// 避免恶意库把内存吃满。
/// </para>
/// </summary>
public static class KeePassReader
{
    /// <summary>解密前就拒绝的密文上限；KDBX 通常只有几百 KB。</summary>
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>条目上限：正常库远小于此，超过只会是构造出来的恶意文件。</summary>
    public const int MaxEntries = 5000;

    public const int MaxFieldsPerEntry = 64;

    public const int MaxStringLength = 16 * 1024;

    private const int MaxXmlChars = 8 * 1024 * 1024;

    private const uint Signature1 = 0x9AA2D903;
    private const uint Signature2 = 0xB54BFB67;

    /// <summary>KDBX 3.1 的 AES-KDF 标识（也在部分 4.x 写入器里出现）。</summary>
    private static readonly byte[] AesKdfKdbx3Uuid =
        [0xC9, 0xD9, 0xF3, 0x9A, 0x62, 0x8A, 0x44, 0x60, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA];

    private static readonly byte[] AesKdfKdbx4Uuid =
        [0x7C, 0x02, 0xBB, 0x82, 0x79, 0xA7, 0x4A, 0xC0, 0x92, 0x7D, 0x11, 0x4A, 0x00, 0x64, 0x82, 0x38];

    private static readonly byte[] Argon2dUuid =
        [0xEF, 0x63, 0x6D, 0xDF, 0x8C, 0x29, 0x44, 0x4B, 0x91, 0xF7, 0xA9, 0xA4, 0x03, 0xE3, 0x0A, 0x0C];

    private static readonly byte[] Argon2idUuid =
        [0x9E, 0x29, 0x8B, 0x19, 0x6D, 0xB4, 0x48, 0x30, 0xBD, 0xA5, 0x57, 0xF0, 0xF7, 0xCA, 0x20, 0xC7];

    private static readonly byte[] AesCipherUuid =
        [0x31, 0xC1, 0xF2, 0xE6, 0xBF, 0x71, 0x43, 0x50, 0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF];

    private static readonly byte[] TwofishCipherUuid =
        [0xAD, 0x68, 0xF2, 0x9F, 0x57, 0x6F, 0x4B, 0xB9, 0xA3, 0x6A, 0xD4, 0x7A, 0xF9, 0x65, 0x34, 0x6C];

    private static readonly byte[] ChaChaCipherUuid =
        [0xD6, 0x03, 0x8A, 0x2B, 0x8B, 0x6F, 0x4C, 0xB5, 0xA5, 0x24, 0x33, 0x9A, 0x31, 0xDB, 0xB5, 0x9A];

    /// <summary>KeePass 给 Salsa20 内层流用的固定 IV（KeePassLib 的 CryptoRandomStream 常量）。</summary>
    private static readonly byte[] Salsa20Iv = [0xE8, 0x30, 0x09, 0x4B, 0x97, 0x20, 0x5D, 0x2A];

    public static bool IsKeePassDatabase(ReadOnlySpan<byte> data) =>
        data.Length >= 12 &&
        BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) == Signature1 &&
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) == Signature2;

    /// <summary>解析；密码不正确、文件被篡改或格式不支持时抛出对应异常（文案已脱敏）。</summary>
    public static KeePassDatabase Read(byte[] data, string? password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.LongLength > MaxBytes)
            throw new InvalidDataException(
                $"此密码库为 {Formatting.Bytes(data.LongLength)}，超过内置预览的 {Formatting.Bytes(MaxBytes)} 上限。");
        if (!IsKeePassDatabase(data))
            throw new InvalidDataException("这不是 KeePass 2 的 KDBX 文件（文件头签名不对）。");

        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (version is < 0x00030000u or >= 0x00050000u)
            throw new NotSupportedException("内置只读预览只支持 KDBX 3.1 与 4.x 密码库。");

        var header = ParseHeader(data);
        var composite = HashPassword(password);
        byte[] derived;
        try
        {
            derived = DeriveKey(header, composite, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(composite);
        }

        var key = Sha256(header.MasterSeed, derived);
        var hmacKey = Sha512(header.MasterSeed, derived, [0x01]);
        CryptographicOperations.ZeroMemory(derived);

        try
        {
            return header.Version >= 0x00040000u
                ? ReadV4(data, header, key, hmacKey, cancellationToken)
                : ReadV3(data, header, key, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(hmacKey);
        }
    }

    // ── 头部 ────────────────────────────────────────────────────────────────

    private sealed record Header(
        uint Version,
        byte[]? CipherId,
        uint Compression,
        byte[] MasterSeed,
        byte[] EncryptionIv,
        byte[]? TransformSeed,
        ulong TransformRounds,
        byte[]? KdfParameters,
        byte[]? ProtectedStreamKey,
        byte[]? StreamStartBytes,
        uint InnerStreamId,
        int PayloadOffset);

    private static Header ParseHeader(byte[] data)
    {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        using var stream = new MemoryStream(data, writable: false);
        stream.Position = 12;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        byte[]? cipher = null, seed = null, iv = null, transformSeed = null, kdf = null,
            protectedKey = null, startBytes = null;
        uint compression = 0, innerStreamId = 0;
        ulong rounds = 0;

        if (version >= 0x00040000u)
        {
            while (true)
            {
                if (stream.Position >= stream.Length)
                    throw new InvalidDataException("密码库在头部结束标记前就断了。");
                var id = reader.ReadByte();
                var size = reader.ReadInt32();
                if (size < 0 || size > 16 * 1024 * 1024 || stream.Position + size > stream.Length)
                    throw new InvalidDataException(
                        $"密码库头的字段长度异常（字段 {id}，长度 {size}，位置 {stream.Position}，总长 {stream.Length}）。");
                if (id == 0)
                    break;
                var value = reader.ReadBytes(size);
                switch (id)
                {
                    case 2: cipher = value; break;
                    case 3: compression = ReadUInt32(value); break;
                    case 4: seed = value; break;
                    case 7: iv = value; break;
                    case 11: kdf = value; break;
                }
            }
        }
        else
        {
            // KDBX 3.1：字段顺序固定、长度 uint16、没有结束标记。
            for (var id = 1; id <= 10; id++)
            {
                if (stream.Position + 2 > stream.Length)
                    throw new InvalidDataException("密码库头不完整。");
                var size = reader.ReadUInt16();
                if (stream.Position + size > stream.Length)
                    throw new InvalidDataException(
                        $"密码库头的字段长度异常（字段 {id}，长度 {size}，位置 {stream.Position}，总长 {stream.Length}）。");
                var value = reader.ReadBytes(size);
                switch (id)
                {
                    case 2: cipher = value; break;
                    case 3: compression = ReadUInt32(value); break;
                    case 4: seed = value; break;
                    case 5: transformSeed = value; break;
                    case 6: rounds = ReadUInt64(value); break;
                    case 7: iv = value; break;
                    case 8: protectedKey = value; break;
                    case 9: startBytes = value; break;
                    case 10: innerStreamId = ReadUInt32(value); break;
                }
            }
        }

        if (seed is not { Length: 32 } || iv is null || iv.Length is 0)
            throw new InvalidDataException("密码库头缺少主种子或加密 IV。");

        return new Header(version, cipher, compression, seed, iv, transformSeed, rounds, kdf,
            protectedKey, startBytes, innerStreamId, (int)stream.Position);
    }

    // ── 主密钥派生 ──────────────────────────────────────────────────────────

    private static byte[] HashPassword(string? password)
    {
        // 复合密钥 = SHA-256(SHA-256(password))；空密码等价于空字符串的哈希。
        var bytes = Encoding.UTF8.GetBytes(password ?? string.Empty);
        var inner = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        var outer = SHA256.HashData(inner);
        CryptographicOperations.ZeroMemory(inner);
        return outer;
    }

    private static byte[] DeriveKey(Header header, byte[] composite, CancellationToken cancellationToken)
    {
        if (header.KdfParameters is { } kdfBytes)
        {
            var map = VariantMap.Parse(kdfBytes);
            if (map.TryGetValue("$UUID", out var uuid) && uuid is { Length: 16 })
            {
                if (uuid.AsSpan().SequenceEqual(AesKdfKdbx3Uuid) || uuid.AsSpan().SequenceEqual(AesKdfKdbx4Uuid))
                {
                    if (!map.TryGetValue("S", out var seed) || seed is not { Length: 32 })
                        throw new InvalidDataException("AES-KDF 参数缺少变换种子。");
                    return AesTransform(composite, seed, map.GetUInt64("R"), cancellationToken);
                }

                if (uuid.AsSpan().SequenceEqual(Argon2dUuid) || uuid.AsSpan().SequenceEqual(Argon2idUuid))
                {
                    var isId = uuid.AsSpan().SequenceEqual(Argon2idUuid);
                    return Argon2Transform(map, composite, isId, cancellationToken);
                }
            }

            throw new NotSupportedException("此密码库使用的密钥派生算法暂不支持，可先用系统应用打开。");
        }

        // KDBX 3.1：只有 AES-KDF，参数直接写在头部。
        if (header.TransformSeed is not { Length: 32 })
            throw new InvalidDataException("密码库头缺少 AES-KDF 变换种子。");
        return AesTransform(composite, header.TransformSeed, header.TransformRounds, cancellationToken);
    }

    private static byte[] AesTransform(byte[] composite, byte[] seed, ulong rounds, CancellationToken cancellationToken)
    {
        // 上限只是防止恶意库用天文数字的轮数把预览钉死；KeePass 默认 600 万轮远低于此。
        if (rounds is 0 or > 200_000_000ul)
            throw new InvalidDataException("AES-KDF 轮数超出内置预览可接受的范围。");

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
            if ((i & 0xFFFFFul) == 0xFFFFFul)
                cancellationToken.ThrowIfCancellationRequested();
        }

        var combined = new byte[32];
        left.CopyTo(combined, 0);
        right.CopyTo(combined, 16);
        // KDBX 的 AES-KDF 输出是变换结果的 SHA-256，这才是「派生密钥」。
        return SHA256.HashData(combined);
    }

    private static byte[] Argon2Transform(
        VariantMap map, byte[] composite, bool isArgon2id, CancellationToken cancellationToken)
    {
        var memoryBytes = map.GetUInt64("M");
        var iterations = map.GetUInt64("I");
        var parallelism = map.GetUInt32("P");
        var version = map.GetUInt32("V");
        if (!map.TryGetValue("S", out var salt) || salt is null || salt.Length is < 8 or > 64)
            throw new InvalidDataException("Argon2 参数缺少盐值。");

        // 上限防止恶意库申请几百 GB 内存（KeePass 默认 64 MiB / 2 轮 / 2 并行）。
        if (memoryBytes is < 8 * 1024 or > 1024L * 1024 * 1024 ||
            iterations is 0 or > 256 ||
            parallelism is 0 or > 64)
            throw new InvalidDataException("Argon2 参数超出内置预览可接受的范围。");

        var builder = new Argon2Parameters.Builder(
                isArgon2id ? Argon2Parameters.Argon2id : Argon2Parameters.Argon2d)
            .WithVersion(version == 0 ? Argon2Parameters.Version13 : (int)version)
            .WithSalt(salt)
            .WithParallelism((int)parallelism)
            .WithMemoryAsKB((int)Math.Min(int.MaxValue, memoryBytes / 1024))
            .WithIterations((int)iterations);
        if (map.TryGetValue("K", out var secret) && secret is { Length: > 0 })
            builder = builder.WithSecret(secret);
        if (map.TryGetValue("A", out var associated) && associated is { Length: > 0 })
            builder = builder.WithAdditional(associated);

        cancellationToken.ThrowIfCancellationRequested();
        var generator = new Argon2BytesGenerator();
        generator.Init(builder.Build());
        var output = new byte[32];
        generator.GenerateBytes(composite, output);
        return output;
    }

    // ── KDBX 4.x ────────────────────────────────────────────────────────────

    private static KeePassDatabase ReadV4(
        byte[] data, Header header, byte[] key, byte[] hmacKey, CancellationToken cancellationToken)
    {
        var offset = header.PayloadOffset;
        if (offset + 64 > data.Length)
            throw new InvalidDataException("密码库头校验字段缺失。");

        // header 的 SHA-256 与 HMAC（HMAC 的块索引固定为 ulong.MaxValue）。
        var headerBytes = data.AsSpan(0, offset);
        var headerSha = SHA256.HashData(headerBytes);
        if (!CryptographicOperations.FixedTimeEquals(headerSha, data.AsSpan(offset, 32)))
            throw new InvalidDataException("密码库头校验失败（文件可能已损坏或被改动）。");

        var headerBlockKey = BlockKey(ulong.MaxValue, hmacKey);
        var expectedHeaderMac = HmacSha256(headerBlockKey, headerBytes, []);
        if (!CryptographicOperations.FixedTimeEquals(expectedHeaderMac, data.AsSpan(offset + 32, 32)))
            throw new KeePassUnlockException(
                "主密码不正确，或此密码库还需要密钥文件（内置预览暂不支持密钥文件）。");
        offset += 64;

        var cipherText = ReadHmacBlocks(data, ref offset, hmacKey);
        byte[] plain;
        try
        {
            plain = Decrypt(header, key, cipherText);
        }
        catch (Exception ex) when (ex is InvalidCipherTextException or CryptographicException)
        {
            throw new InvalidDataException("密码库内容无法解密（文件可能已损坏）。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherText);
        }

        var payload = header.Compression switch
        {
            0 => plain,
            1 => Decompress(plain),
            _ => throw new NotSupportedException("此密码库使用了不认识的压缩方式。"),
        };
        if (!ReferenceEquals(payload, plain))
            CryptographicOperations.ZeroMemory(plain);

        var cursor = 0;
        var (inner, innerKey) = ReadInnerHeader(payload, ref cursor);
        var innerStream = CreateInnerStream(inner, innerKey, header.Version);
        return ParseXml(payload.AsSpan(cursor).ToArray(), header.Version, innerStream);
    }

    /// <summary>KDBX 4 的 HMAC 块流：每块 [HMAC-SHA256(32)][size(int32)][data]，size=0 为结束块。</summary>
    private static byte[] ReadHmacBlocks(byte[] data, ref int offset, byte[] hmacKey)
    {
        using var result = new MemoryStream();
        ulong blockIndex = 0;
        while (true)
        {
            if (offset + 36 > data.Length)
                throw new InvalidDataException("密码库的数据块不完整。");
            var mac = data.AsSpan(offset, 32);
            var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset + 32, 4));
            offset += 36;
            if (size < 0 || offset + size > data.Length)
                throw new InvalidDataException("密码库的数据块长度异常。");

            var blockKey = BlockKey(blockIndex, hmacKey);
            var message = new byte[12 + size];
            BinaryPrimitives.WriteUInt64LittleEndian(message, blockIndex);
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(8), size);
            data.AsSpan(offset, size).CopyTo(message.AsSpan(12));
            if (!CryptographicOperations.FixedTimeEquals(HmacSha256(blockKey, message, []), mac))
                throw new InvalidDataException("密码库的数据块校验失败（文件可能已损坏）。");

            if (size == 0)
                break;
            result.Write(data, offset, size);
            offset += size;
            blockIndex++;
        }

        return result.ToArray();
    }

    /// <summary>KDBX 4 的内层头（在解密+解压之后、XML 之前）：[id(1)][len(4)][data]，0 为结束。</summary>
    private static (uint Algorithm, byte[]? Key) ReadInnerHeader(byte[] payload, ref int cursor)
    {
        uint algorithm = 3; // 缺省 ChaCha20（KeePass 4.x 的默认内层流）
        byte[]? key = null;
        while (true)
        {
            if (cursor + 5 > payload.Length)
                throw new InvalidDataException("密码库的内层头不完整。");
            var id = payload[cursor];
            var size = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(cursor + 1, 4));
            cursor += 5;
            if (size < 0 || cursor + size > payload.Length)
                throw new InvalidDataException("密码库内层头的字段长度异常。");
            if (id == 0)
                break;

            var value = payload.AsSpan(cursor, size);
            cursor += size;
            switch (id)
            {
                case 1 when size == 4:
                    algorithm = BinaryPrimitives.ReadUInt32LittleEndian(value);
                    break;
                case 2:
                    key = value.ToArray();
                    break;
                // 3 = 内层二进制池（图标/附件）；预览不展示，跳过即可。
            }
        }

        return (algorithm, key);
    }

    // ── KDBX 3.1 ────────────────────────────────────────────────────────────

    private static KeePassDatabase ReadV3(byte[] data, Header header, byte[] key, CancellationToken cancellationToken)
    {
        var cipherText = data.AsSpan(header.PayloadOffset).ToArray();
        byte[] plain;
        try
        {
            plain = Decrypt(header, key, cipherText);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidCipherTextException)
        {
            throw new KeePassUnlockException(
                "主密码不正确，或此密码库还需要密钥文件（内置预览暂不支持密钥文件）。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cipherText);
        }

        var startBytes = header.StreamStartBytes ?? [];
        if (startBytes.Length != 32 || plain.Length < 32)
            throw new InvalidDataException("密码库头缺少 stream start bytes。");
        if (!CryptographicOperations.FixedTimeEquals(plain.AsSpan(0, 32), startBytes))
            throw new KeePassUnlockException(
                "主密码不正确，或此密码库还需要密钥文件（内置预览暂不支持密钥文件）。");

        var cursor = 32;
        var payload = ReadHashedBlocks(plain, ref cursor, cancellationToken);
        CryptographicOperations.ZeroMemory(plain);

        var xml = header.Compression switch
        {
            0 => payload,
            1 => Decompress(payload),
            _ => throw new NotSupportedException("此密码库使用了不认识的压缩方式。"),
        };
        if (!ReferenceEquals(xml, payload))
            CryptographicOperations.ZeroMemory(payload);

        var inner = CreateInnerStream(header.InnerStreamId, header.ProtectedStreamKey, header.Version);
        return ParseXml(xml, header.Version, inner);
    }

    /// <summary>KDBX 3.1 的哈希块流：每块 [index(4)][SHA-256(32)][size(4)][data]，size=0 为结束块。</summary>
    private static byte[] ReadHashedBlocks(byte[] plain, ref int cursor, CancellationToken cancellationToken)
    {
        using var result = new MemoryStream();
        while (true)
        {
            if (cursor + 40 > plain.Length)
                throw new InvalidDataException("密码库的数据块不完整。");
            var hash = plain.AsSpan(cursor + 4, 32);
            var size = BinaryPrimitives.ReadInt32LittleEndian(plain.AsSpan(cursor + 36, 4));
            cursor += 40;
            if (size < 0 || cursor + size > plain.Length)
                throw new InvalidDataException("密码库的数据块长度异常。");
            if (size == 0)
                break;

            var block = plain.AsSpan(cursor, size);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(block), hash))
                throw new InvalidDataException("密码库的数据块校验失败（文件可能已损坏）。");
            result.Write(block);
            cursor += size;
            cancellationToken.ThrowIfCancellationRequested();
        }

        return result.ToArray();
    }

    // ── 对称解密 ────────────────────────────────────────────────────────────

    private static byte[] Decrypt(Header header, byte[] key, byte[] cipherText)
    {
        if (header.CipherId is not { Length: 16 } cipherId)
            throw new InvalidDataException("密码库头缺少加密算法标识。");

        if (cipherId.AsSpan().SequenceEqual(ChaChaCipherUuid))
        {
            var chacha = new ChaCha7539Engine();
            chacha.Init(false, new ParametersWithIV(new KeyParameter(key), header.EncryptionIv));
            var output = new byte[cipherText.Length];
            chacha.ProcessBytes(cipherText, 0, cipherText.Length, output, 0);
            return output;
        }

        if (cipherId.AsSpan().SequenceEqual(AesCipherUuid))
            return DecryptBlockCipher(new AesEngine(), key, header.EncryptionIv, cipherText);

        if (cipherId.AsSpan().SequenceEqual(TwofishCipherUuid))
            return DecryptBlockCipher(new TwofishEngine(), key, header.EncryptionIv, cipherText);

        throw new NotSupportedException("此密码库使用的加密算法暂不支持，可先用系统应用打开。");
    }

    private static byte[] DecryptBlockCipher(
        IBlockCipher engine, byte[] key, byte[] iv, byte[] cipherText)
    {
        var padding = new PaddedBufferedBlockCipher(new CbcBlockCipher(engine));
        padding.Init(false, new ParametersWithIV(new KeyParameter(key), iv));
        var output = new byte[padding.GetOutputSize(cipherText.Length)];
        var length = padding.ProcessBytes(cipherText, 0, cipherText.Length, output, 0);
        length += padding.DoFinal(output, length);
        return output[..length];
    }

    // ── 内层流密码 ──────────────────────────────────────────────────────────

    private static IStreamCipher? CreateInnerStream(uint algorithm, byte[]? protectedKey, uint version)
    {
        if (algorithm == 0)
            return null;
        if (protectedKey is not { Length: 32 })
        {
            // KDBX 4 的内层密钥来自加密内层头，缺失时说明没有受保护字段。
            if (version >= 0x00040000u)
                return null;
            throw new InvalidDataException("密码库头缺少受保护字段的流密钥。");
        }

        return algorithm switch
        {
            2 => CreateSalsa20(protectedKey),
            3 => CreateChaCha20(protectedKey),
            _ => throw new NotSupportedException("此密码库的内层流密码算法暂不支持。"),
        };
    }

    private static IStreamCipher CreateSalsa20(byte[] protectedKey)
    {
        var salsa = new Salsa20Engine();
        salsa.Init(false, new ParametersWithIV(new KeyParameter(SHA256.HashData(protectedKey)), Salsa20Iv));
        return salsa;
    }

    private static IStreamCipher CreateChaCha20(byte[] protectedKey)
    {
        var hash = SHA512.HashData(protectedKey);
        var chacha = new ChaCha7539Engine();
        chacha.Init(false, new ParametersWithIV(
            new KeyParameter(hash.AsSpan(0, 32).ToArray()), hash.AsSpan(32, 12).ToArray()));
        return chacha;
    }

    // ── XML ─────────────────────────────────────────────────────────────────

    private static KeePassDatabase ParseXml(byte[] xml, uint version, IStreamCipher? inner)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlChars,
            IgnoreComments = true,
        };

        XDocument document;
        using (var stream = new MemoryStream(xml, writable: false))
        using (var reader = XmlReader.Create(stream, settings))
        {
            try
            {
                document = XDocument.Load(reader);
            }
            catch (XmlException)
            {
                throw new InvalidDataException("密码库内容不是有效的 XML（文件可能已损坏）。");
            }
        }

        var protectedValues = ResolveProtectedValues(document, inner);

        var root = document.Root;
        if (root is null || root.Name.LocalName != "KeePassFile")
            throw new InvalidDataException("密码库内容缺少 KeePassFile 根节点。");

        var ns = root.Name.Namespace;
        var meta = root.Element(ns + "Meta");
        // KDBX 的库名在 Meta/DatabaseName；早期手写库有时只写 Meta/Name。
        var name = meta?.Element(ns + "DatabaseName")?.Value ?? meta?.Element(ns + "Name")?.Value;
        var entries = new List<KeePassEntryNode>();
        var truncated = false;
        var groupElement = root.Element(ns + "Root")?.Element(ns + "Group");
        var groupNode = groupElement is null
            ? new KeePassGroupNode(string.Empty, "密码库", 0, [])
            : ParseGroup(groupElement, string.Empty, entries, protectedValues, ref truncated);

        if (name is { Length: > 256 })
            name = name[..256];

        return new KeePassDatabase(
            groupNode, entries, version, string.IsNullOrWhiteSpace(name) ? null : name, truncated);
    }

    /// <summary>
    /// 把 Protected="True" 的字段就地解密。文档顺序必须与 KeePass 写入时的顺序一致，
    /// 所以这里只做一次全文档顺序遍历（含历史条目），而不是按结构分头处理。
    /// </summary>
    private static HashSet<XElement> ResolveProtectedValues(XDocument document, IStreamCipher? inner)
    {
        // 解密后就地抹掉 Protected 标记（不留“这是密码”的痕迹给后续结构遍历），
        // 所以这里单独把受保护的元素记下来，条目行靠它决定是否遮罩。
        var protectedElements = new HashSet<XElement>();
        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName is not ("Value" or "Binary"))
                continue;
            if (!string.Equals(element.Attribute("Protected")?.Value, "True", StringComparison.OrdinalIgnoreCase))
                continue;
            protectedElements.Add(element);
            if (inner is null)
            {
                element.Value = string.Empty;
                continue;
            }

            byte[] encrypted;
            try
            {
                encrypted = Convert.FromBase64String(element.Value.Trim());
            }
            catch (FormatException)
            {
                throw new InvalidDataException("密码库的受保护字段不是有效的 Base64。");
            }

            if (encrypted.Length > MaxStringLength * 4)
                throw new InvalidDataException("密码库的受保护字段过长。");
            var plain = new byte[encrypted.Length];
            inner.ProcessBytes(encrypted, 0, encrypted.Length, plain, 0);
            element.Value = Encoding.UTF8.GetString(plain);
            element.SetAttributeValue("Protected", null);
            CryptographicOperations.ZeroMemory(plain);
        }

        return protectedElements;
    }

    private static KeePassGroupNode ParseGroup(
        XElement group, string parentPath, List<KeePassEntryNode> entries,
        HashSet<XElement> protectedValues, ref bool truncated)
    {
        var ns = group.Name.Namespace;
        var name = Truncate(group.Element(ns + "Name")?.Value);
        if (string.IsNullOrWhiteSpace(name))
            name = "（未命名分组）";
        var path = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath} / {name}";
        var ownEntries = 0;

        foreach (var entry in group.Elements(ns + "Entry"))
        {
            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            if (ParseEntry(entry, path, protectedValues) is { } node)
            {
                entries.Add(node);
                ownEntries++;
            }
        }

        var children = new List<KeePassGroupNode>();
        foreach (var child in group.Elements(ns + "Group"))
            children.Add(ParseGroup(child, path, entries, protectedValues, ref truncated));

        return new KeePassGroupNode(path, name, ownEntries + children.Sum(c => c.EntryCount), children);
    }

    private static KeePassEntryNode? ParseEntry(
        XElement entry, string groupPath, HashSet<XElement> protectedValues)
    {
        var ns = entry.Name.Namespace;
        var fields = new List<KeePassField>();
        foreach (var pair in entry.Elements(ns + "String"))
        {
            if (fields.Count >= MaxFieldsPerEntry)
                break;
            var key = Truncate(pair.Element(ns + "Key")?.Value);
            if (string.IsNullOrEmpty(key))
                continue;
            var valueElement = pair.Element(ns + "Value");
            fields.Add(new KeePassField(
                key,
                Truncate(valueElement?.Value),
                valueElement is not null && protectedValues.Contains(valueElement)));
        }

        if (fields.Count == 0)
            return null;

        string Field(string key) =>
            fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))?.Value ?? string.Empty;

        var title = Field("Title");
        return new KeePassEntryNode(
            groupPath,
            string.IsNullOrWhiteSpace(title) ? "（未命名条目）" : title,
            Field("UserName"),
            Field("URL"),
            Field("Notes"),
            fields);
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= MaxStringLength ? value : value[..MaxStringLength];
    }

    // ── 变体字典（KDBX 4 的 KDF 参数） ─────────────────────────────────────

    private sealed class VariantMap : Dictionary<string, byte[]>
    {
        public static VariantMap Parse(byte[] data)
        {
            var map = new VariantMap();
            if (data.Length < 2)
                throw new InvalidDataException("KDF 参数为空。");
            var version = BinaryPrimitives.ReadUInt16LittleEndian(data);
            if (version != 0x0100)
                throw new NotSupportedException("不认识的 KDF 参数版本。");

            var offset = 2;
            while (offset < data.Length)
            {
                var type = data[offset];
                offset++;
                if (type == 0)
                    break;
                if (offset + 4 > data.Length)
                    throw new InvalidDataException("KDF 参数的条目被截断。");
                var keyLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (keyLength > 256 || offset + keyLength > data.Length)
                    throw new InvalidDataException("KDF 参数的键名长度异常。");
                var keyStart = offset;
                var key = Encoding.UTF8.GetString(data, offset, (int)keyLength);
                offset += (int)keyLength;
                if (offset + 4 > data.Length)
                    throw new InvalidDataException("KDF 参数的条目被截断。");
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
                offset += 4;
                if (valueLength > 1024 * 1024 || offset + valueLength > data.Length)
                    throw new InvalidDataException("KDF 参数的值长度异常。");

                map[key] = type switch
                {
                    0x04 or 0x05 or 0x06 or 0x07 or 0x08 or 0x09 or 0x0C or 0x0D or 0x18 or 0x42 =>
                        data.AsSpan(offset, (int)valueLength).ToArray(),
                    _ => throw new InvalidDataException(
                        $"KDF 参数里出现未知类型 0x{type:X2}（版本 {version:X4}，位置 {keyStart}，键长 {keyLength}，总长 {data.Length}）。"),
                };
                offset += (int)valueLength;
            }

            return map;
        }

        public ulong GetUInt64(string key) =>
            TryGetValue(key, out var raw) && raw.Length == 8
                ? BinaryPrimitives.ReadUInt64LittleEndian(raw)
                : throw new InvalidDataException("KDF 参数缺少 64 位整数项。");

        public uint GetUInt32(string key) =>
            TryGetValue(key, out var raw) && raw.Length == 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(raw)
                : throw new InvalidDataException("KDF 参数缺少 32 位整数项。");
    }

    // ── 杂项 ────────────────────────────────────────────────────────────────

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxXmlChars)
                throw new InvalidDataException("密码库解压后的内容超过内置预览上限。");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static uint ReadUInt32(byte[] value) =>
        value.Length == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(value)
            : throw new InvalidDataException("密码库头的整数字段长度异常。");

    private static ulong ReadUInt64(byte[] value) =>
        value.Length == 8
            ? BinaryPrimitives.ReadUInt64LittleEndian(value)
            : throw new InvalidDataException("密码库头的整数字段长度异常。");

    /// <summary>KDBX 4 的块密钥：SHA-512(块号小端 || HMAC 密钥)。</summary>
    private static byte[] BlockKey(ulong blockIndex, byte[] hmacKey)
    {
        var buffer = new byte[8 + hmacKey.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, blockIndex);
        hmacKey.CopyTo(buffer, 8);
        return SHA512.HashData(buffer);
    }

    private static byte[] Sha256(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts)
            hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    private static byte[] Sha512(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (var part in parts)
            hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    private static byte[] HmacSha256(byte[] key, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(first);
        if (!second.IsEmpty)
            hmac.AppendData(second);
        return hmac.GetHashAndReset();
    }
}

/// <summary>
/// KeePass 只读预览提供者。它只认出文件、把密文交给预览面板，主密码由用户当场输入；
/// 提供者自身不接触密码，也不保留解密结果。
/// </summary>
internal sealed class KeePassPreviewProvider(IFileSystemService fileSystem) : IPreviewProvider
{
    public string Id => "builtin.keepass";

    /// <summary>排在 Office（118）之前：.kdbx 不和文档类扩展名重叠，但语义上更专门。</summary>
    public int Priority => 119;

    public bool CanHandle(FileItem item) =>
        !item.IsFolder && string.Equals(item.Extension, ".kdbx", StringComparison.OrdinalIgnoreCase);

    public async Task<PreviewContent> LoadAsync(FileItem item, CancellationToken cancellationToken)
    {
        var limit = KeePassReader.MaxBytes;
        if (item.Length is { } known && known > limit)
            return new PreviewContent(Message:
                $"此密码库为 {Formatting.Bytes(known)}，超过内置预览的 {Formatting.Bytes(limit)} 上限；可先用系统应用打开。");

        try
        {
            await using var source = await fileSystem.OpenReadAsync(item.FullPath, cancellationToken).ConfigureAwait(true);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(true)) > 0)
            {
                if (memory.Length + read > limit)
                    return new PreviewContent(Message:
                        $"此密码库超过内置预览的 {Formatting.Bytes(limit)} 上限；可先用系统应用打开。");
                memory.Write(buffer, 0, read);
            }

            var data = memory.ToArray();
            if (!KeePassReader.IsKeePassDatabase(data))
                return new PreviewContent(Message: "此文件不是 KeePass 2 的 KDBX 密码库；可先用系统应用打开。");
            return new PreviewContent(KeePassUnlock: new KeePassUnlockRequest(item.Name, data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreviewContent(Message: $"无法读取此密码库：{ex.Message}");
        }
    }
}
