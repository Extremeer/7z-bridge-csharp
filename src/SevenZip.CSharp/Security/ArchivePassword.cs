using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SevenZip.CSharp;

public delegate TResult PasswordReader<TResult>(ReadOnlySpan<char> password);

/// <summary>
/// Owns an archive password in clearable memory. Callers can inspect the value only
/// inside <see cref="Use{TResult}"/> and must dispose the handle when the archive session ends.
/// </summary>
public sealed class ArchivePassword : IDisposable
{
    private readonly object syncRoot = new();
    private char[] password = Array.Empty<char>();
    private bool isDisposed;

    public ArchivePassword(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("密码不能为空", nameof(value));
        }

        password = value.ToArray();
    }

    ~ArchivePassword()
    {
        Clear();
    }

    public bool IsDisposed
    {
        get
        {
            lock (syncRoot)
            {
                return isDisposed;
            }
        }
    }

    public TResult Use<TResult>(PasswordReader<TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            return reader(password);
        }
    }

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }

    private void Clear()
    {
        lock (syncRoot)
        {
            if (isDisposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
            isDisposed = true;
        }
    }

    public override string ToString() => "[protected archive password]";
}
