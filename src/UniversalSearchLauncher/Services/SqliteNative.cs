using System.Runtime.InteropServices;
using System.Text;

namespace UniversalSearchLauncher.Services;

/// <summary>
/// Minimal binding to winsqlite3.dll, which ships with Windows 10/11. Using the
/// OS component keeps the application free of native NuGet payloads.
/// </summary>
internal static class SqliteNative
{
    private const string Library = "winsqlite3.dll";

    internal const int Ok = 0;
    internal const int Row = 100;
    internal const int Done = 101;
    internal const int OpenReadWrite = 0x00000002;
    internal const int OpenCreate = 0x00000004;
    internal const int OpenFullMutex = 0x00010000;

    private static readonly IntPtr Transient = new(-1);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_close_v2(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr errorMessage);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_prepare_v2(
        IntPtr database,
        byte[] sql,
        int byteCount,
        out IntPtr statement,
        IntPtr tail);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_reset(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_clear_bindings(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int length, IntPtr destructor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_null(IntPtr statement, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long sqlite3_column_int64(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_libversion();

    internal static byte[] Utf8(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    internal static string Error(IntPtr database)
    {
        var pointer = sqlite3_errmsg(database);
        return pointer == IntPtr.Zero ? "unknown SQLite error" : Marshal.PtrToStringUTF8(pointer) ?? "unknown SQLite error";
    }

    internal static void ThrowOnError(IntPtr database, int code, string operation)
    {
        if (code != Ok)
        {
            throw new InvalidOperationException($"{operation}: {Error(database)} (code {code})");
        }
    }

    internal sealed class Statement : IDisposable
    {
        private readonly IntPtr _database;
        private IntPtr _statement;

        private Statement(IntPtr database, IntPtr statement)
        {
            _database = database;
            _statement = statement;
        }

        public static Statement Prepare(IntPtr database, string sql)
        {
            var bytes = Utf8(sql);
            var code = sqlite3_prepare_v2(database, bytes, -1, out var statement, IntPtr.Zero);
            ThrowOnError(database, code, "prepare");
            return new Statement(database, statement);
        }

        public void Bind(int index, string? value)
        {
            if (value is null)
            {
                var nullCode = sqlite3_bind_null(_statement, index);
                ThrowOnError(_database, nullCode, "bind null");
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            var result = sqlite3_bind_text(_statement, index, bytes, bytes.Length, Transient);
            ThrowOnError(_database, result, "bind text");
        }

        public void Bind(int index, long value)
        {
            var code = sqlite3_bind_int64(_statement, index, value);
            ThrowOnError(_database, code, "bind int64");
        }

        public bool Read()
        {
            var code = sqlite3_step(_statement);
            if (code == Row)
            {
                return true;
            }

            if (code == Done)
            {
                return false;
            }

            throw new InvalidOperationException("SQLite read: " + Error(_database));
        }

        public void Execute()
        {
            var code = sqlite3_step(_statement);
            if (code != Done)
            {
                throw new InvalidOperationException("SQLite execute: " + Error(_database));
            }
        }

        public void Reset()
        {
            sqlite3_reset(_statement);
            sqlite3_clear_bindings(_statement);
        }

        public string? GetString(int column)
        {
            var pointer = sqlite3_column_text(_statement, column);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
        }

        public long GetInt64(int column)
        {
            return sqlite3_column_int64(_statement, column);
        }

        public void Dispose()
        {
            if (_statement != IntPtr.Zero)
            {
                sqlite3_finalize(_statement);
                _statement = IntPtr.Zero;
            }
        }
    }
}
