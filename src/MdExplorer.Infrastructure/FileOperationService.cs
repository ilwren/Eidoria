using MdExplorer.Application;
using MdExplorer.Domain;
using MdExplorer.Platform.Abstractions;

namespace MdExplorer.Infrastructure;

/// <summary>
/// 复制/移动/永久删除执行器（串行调用语义由上层保证）。
/// 规则对齐 DEVELOPMENT.md §5.5：同卷移动优先 Move、跨卷移动=复制成功后删源、
/// KeepBoth 自动改名、单项失败不中断其余、文件夹递归。
/// </summary>
public sealed class FileOperationService : IFileOperationService
{
    public async Task<OperationOutcome> CopyAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<OperationError>();
        var produced = new List<string>();
        var done = 0;
        foreach (var source in sources)
        {
            if (cancellationToken.IsCancellationRequested)
                return new OperationOutcome(done, errors, produced, Cancelled: true);
            try
            {
                var dest = ResolveDestination(source, destinationFolder, policy);
                if (dest is null) { done++; continue; } // Skip
                await CopyOneAsync(source, dest, policy, cancellationToken).ConfigureAwait(false);
                produced.Add(dest);
            }
            catch (OperationCanceledException)
            {
                return new OperationOutcome(done, errors, produced, Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new OperationError(Path.GetFileName(source), ex.Message, source));
            }

            done++;
            Report(progress, sources.Count, done, source);
        }

        return new OperationOutcome(done - errors.Count, errors, produced);
    }

    public async Task<OperationOutcome> MoveAsync(
        IReadOnlyList<string> sources, string destinationFolder, ConflictPolicy policy,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<OperationError>();
        var produced = new List<string>();
        var done = 0;
        foreach (var source in sources)
        {
            if (cancellationToken.IsCancellationRequested)
                return new OperationOutcome(done, errors, produced, Cancelled: true);
            try
            {
                // 移到自身所在目录视为无操作
                if (PathsEqual(Path.GetDirectoryName(source), destinationFolder))
                {
                    done++;
                    continue;
                }

                var dest = ResolveDestination(source, destinationFolder, policy);
                if (dest is null) { done++; continue; }
                await MoveOneAsync(source, dest, cancellationToken).ConfigureAwait(false);
                produced.Add(dest);
            }
            catch (OperationCanceledException)
            {
                return new OperationOutcome(done, errors, produced, Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new OperationError(Path.GetFileName(source), ex.Message, source));
            }

            done++;
            Report(progress, sources.Count, done, source);
        }

        return new OperationOutcome(done - errors.Count, errors, produced);
    }

    public async Task<OperationOutcome> DeletePermanentlyAsync(
        IReadOnlyList<string> sources,
        IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<OperationError>();
        var done = 0;
        foreach (var source in sources)
        {
            if (cancellationToken.IsCancellationRequested)
                return new OperationOutcome(done, errors, [], Cancelled: true);
            try
            {
                await Task.Run(() => DeletePath(source, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new OperationOutcome(done, errors, [], Cancelled: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new OperationError(Path.GetFileName(source), ex.Message, source));
            }

            done++;
            Report(progress, sources.Count, done, source);
        }

        return new OperationOutcome(done - errors.Count, errors, []);
    }

    internal static void DeletePath(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(path) && !IsSymlink(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path) || IsSymlink(path))
            File.Delete(path);
    }

    private static bool IsSymlink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string? ResolveDestination(string source, string destinationFolder, ConflictPolicy policy)
    {
        var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = Path.Combine(destinationFolder, name);
        if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
        // 复制到原地必然冲突：Overwrite 语义无法对自身执行时也降为 KeepBoth，避免自毁
        var sameAsSource = PathsEqual(dest, source);
        switch (policy)
        {
            case ConflictPolicy.Skip:
                return null;
            case ConflictPolicy.Overwrite when !sameAsSource:
                DeletePath(dest, CancellationToken.None);
                return dest;
            default:
                for (int i = 2; i < 1000; i++)
                {
                    var candidate = Path.Combine(destinationFolder, PathText.KeepBothName(name, i));
                    if (!File.Exists(candidate) && !Directory.Exists(candidate))
                        return candidate;
                }
                throw new IOException($"目标位置同名文件过多：{name}");
        }
    }

    private static async Task CopyOneAsync(string source, string dest, ConflictPolicy policy, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(source) && !IsSymlink(source))
        {
            Directory.CreateDirectory(dest);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                ct.ThrowIfCancellationRequested();
                var childDest = ResolveDestination(entry, dest, policy);
                if (childDest is null) continue;
                await CopyOneAsync(entry, childDest, policy, ct).ConfigureAwait(false);
            }
            return;
        }

        const int bufferSize = 1024 * 128;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
        try { File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(source)); }
        catch (IOException) { /* 时间戳回写失败不致命 */ }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task MoveOneAsync(string source, string dest, CancellationToken ct)
    {
        // 禁止把目录移入自身子目录
        var sourceFull = Path.GetFullPath(source);
        var destFull = Path.GetFullPath(dest);
        if (destFull.StartsWith(sourceFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("不能把文件夹移动到它自己的子文件夹中");

        try
        {
            if (Directory.Exists(source) && !IsSymlink(source))
                Directory.Move(source, dest);
            else
                File.Move(source, dest);
            return;
        }
        catch (IOException) when (!PathsOnSameRoot(source, dest))
        {
            // 跨卷：复制后删源（删源失败只报错，不丢目标）
            await CopyOneAsync(source, dest, ConflictPolicy.Skip, ct).ConfigureAwait(false);
            DeletePath(source, ct);
        }
    }

    private static bool PathsOnSameRoot(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool PathsEqual(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), cmp);
    }

    private static void Report(IProgress<OperationProgress>? progress, int total, int done, string current)
    {
        if (progress is null || total == 0) return;
        progress.Report(new OperationProgress((double)done / total, Path.GetFileName(current), done, total));
    }
}
