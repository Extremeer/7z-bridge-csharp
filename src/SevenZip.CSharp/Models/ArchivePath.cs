using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace SevenZip.CSharp;

/// <summary>
/// 表示压缩包内部路径，使用统一的标准化格式（"/" 作为分隔符，根目录为空字符串）。
/// </summary>
[DebuggerDisplay("{Value}")]
public sealed class ArchivePath : IEquatable<ArchivePath>, IComparable<ArchivePath>
{
    // Archive entry names are not Windows file names. Keep cross-platform names
    // representable here and apply the destination file-system policy at extraction.
    private static readonly char[] InvalidCharacters = ['\0'];

    private readonly ImmutableArray<string> _segments;

    private ArchivePath(ImmutableArray<string> segments)
    {
        _segments = segments;
    }

    /// <summary>
    /// 根路径。
    /// </summary>
    public static ArchivePath Root { get; } = new(ImmutableArray<string>.Empty);

    /// <summary>
    /// 是否为根目录。
    /// </summary>
    public bool IsRoot => _segments.IsDefaultOrEmpty || _segments.Length == 0;

    /// <summary>
    /// 标准化后的字符串表示。根目录返回空字符串。
    /// </summary>
    public string Value => IsRoot ? string.Empty : string.Join('/', _segments);

    /// <summary>
    /// 路径的最后一段名称（根目录返回空字符串）。
    /// </summary>
    public string Name => IsRoot ? string.Empty : _segments[^1];

    /// <summary>
    /// 路径分段集合（不可变）。
    /// </summary>
    public IReadOnlyList<string> Segments => _segments;

    /// <summary>
    /// 父级路径，根目录返回自身。
    /// </summary>
    public ArchivePath Parent => IsRoot ? this : new ArchivePath(_segments[..^1]);

    /// <summary>
    /// 尝试根据原始字符串创建路径。空/空白视为根目录。
    /// </summary>
    public static ArchivePath From(string? raw) => string.IsNullOrWhiteSpace(raw) ? Root : new(Normalize(raw!, allowRoot: true));

    /// <summary>
    /// 拼接子路径。
    /// </summary>
    public ArchivePath Combine(string? child)
    {
        if (string.IsNullOrWhiteSpace(child))
        {
            return this;
        }

        var childSegments = Normalize(child!, allowRoot: false);
        if (childSegments.IsDefaultOrEmpty)
        {
            return this;
        }

        var builder = ImmutableArray.CreateBuilder<string>(_segments.Length + childSegments.Length);
        builder.AddRange(_segments);
        builder.AddRange(childSegments);
        return new ArchivePath(builder.ToImmutable());
    }

    /// <summary>
    /// 判断当前路径是否为目标的祖先（包含自身）。
    /// </summary>
    public bool IsAncestorOf(ArchivePath other)
    {
        if (other is null)
        {
            return false;
        }

        if (_segments.Length == 0)
        {
            return true;
        }

        if (_segments.Length > other._segments.Length)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Value;

    public bool Equals(ArchivePath? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || _segments.Length != other._segments.Length)
        {
            return false;
        }

        for (var i = 0; i < _segments.Length; i++)
        {
            if (!string.Equals(_segments[i], other._segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is ArchivePath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var segment in _segments)
        {
            hash.Add(segment, StringComparer.OrdinalIgnoreCase);
        }

        return hash.ToHashCode();
    }

    public int CompareTo(ArchivePath? other)
    {
        if (other is null)
        {
            return 1;
        }

        return string.Compare(Value, other.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static bool operator ==(ArchivePath? left, ArchivePath? right) => Equals(left, right);
    public static bool operator !=(ArchivePath? left, ArchivePath? right) => !Equals(left, right);

    public static ImmutableArray<string> Normalize(string raw, bool allowRoot)
    {
        if (raw is null)
        {
            throw new ArgumentNullException(nameof(raw));
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (allowRoot)
            {
                return ImmutableArray<string>.Empty;
            }

            throw new ArgumentException("子路径不能为空", nameof(raw));
        }

        if (raw.StartsWith('/') || raw.StartsWith('\\') || Path.IsPathFullyQualified(raw))
        {
            throw new ArgumentException("压缩包条目不允许使用绝对路径", nameof(raw));
        }

        var buffer = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            buffer.Append(ch == '\\' ? '/' : ch);
        }

        var segments = buffer
            .ToString()
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            if (allowRoot)
            {
                return ImmutableArray<string>.Empty;
            }

            throw new ArgumentException("子路径不能为空", nameof(raw));
        }

        var builder = ImmutableArray.CreateBuilder<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new ArgumentException("路径中不允许出现 ..", nameof(raw));
            }

            if (segment.IndexOfAny(InvalidCharacters) >= 0)
            {
                throw new ArgumentException($"路径片段包含非法字符: {segment}", nameof(raw));
            }

            builder.Add(segment);
        }

        return builder.ToImmutable();
    }
}
