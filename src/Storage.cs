using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AnbyPet
{
    internal sealed class LocalStore
    {
        internal readonly string Folder;
        internal string Error { get; private set; }

        internal LocalStore(string exe)
        {
            Folder = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe));
        }

        internal string CheckedDirectory(bool create, params string[] children)
        {
            var directory = Folder;
            for (int i = 0; ; i++)
            {
                if (create) Directory.CreateDirectory(directory);
                var attributes = File.GetAttributes(directory);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new IOException("The data directory is a reparse point or is not a directory.");
                if (i == children.Length) return directory;
                directory = Path.Combine(directory, children[i]);
            }
        }

        internal string Read(string name)
        {
            try
            {
                var path = Path.Combine(Folder, name);
                if (!File.Exists(path)) return null;
                if ((File.GetAttributes(Folder) & FileAttributes.ReparsePoint) != 0 ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 128 * 1024) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal bool Write(string name, string value)
        {
            string temporary = null;
            try
            {
                CheckedDirectory(true);
                var target = Path.Combine(Folder, name);
                temporary = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(value);
                if (!MoveFileEx(temporary, target, 1))
                    throw new IOException("Unable to replace settings or cache.");
                Error = null;
                return true;
            }
            catch (IOException) { Error = "同名数据目录不可写"; }
            catch (UnauthorizedAccessException) { Error = "同名数据目录不可写"; }
            finally
            {
                if (temporary != null)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return false;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string current, string target, int flags);
    }

    internal static class Startup
    {
        private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "AnbyPet";

        internal static string Command(string exe)
        {
            return "\"" + exe + "\" --startup";
        }

        internal static string RegisteredCommand()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Run))
                return key == null ? null : key.GetValue(Name) as string;
        }

        internal static bool Apply(string exe, bool enabled)
        {
            try
            {
                var command = Command(exe);
                if (enabled && command.Length > 260) return false;
                using (var key = Registry.CurrentUser.CreateSubKey(Run))
                {
                    if (key == null) return false;
                    if (enabled) key.SetValue(Name, command, RegistryValueKind.String);
                    else key.DeleteValue(Name, false);
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
            catch (IOException) { return false; }
        }
    }

    internal static class Json
    {
        internal static IDictionary<string, object> Object(object value)
        {
            return value as IDictionary<string, object>;
        }

        internal static IDictionary<string, object> Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            try
            {
                return Object(new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(value));
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        internal static string Serialize(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        internal static object Value(IDictionary<string, object> obj, string key)
        {
            object value;
            return obj != null && obj.TryGetValue(key, out value) ? value : null;
        }

        internal static string Text(IDictionary<string, object> obj, string key)
        {
            return Value(obj, key) as string;
        }

        internal static double? Number(IDictionary<string, object> obj, string key)
        {
            var value = Value(obj, key);
            if (!(value is int || value is long || value is double || value is decimal)) return null;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(number) || double.IsInfinity(number) ? (double?)null : number;
        }

        internal static bool Flag(IDictionary<string, object> obj, string key, bool fallback = false)
        {
            var value = Value(obj, key);
            return value is bool ? (bool)value : fallback;
        }
    }
}
