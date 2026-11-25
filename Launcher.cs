using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

internal static class AntigravityProxy
{
    private const string ExecutableName = "Antigravity.exe";
    private const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\Antigravity.exe";
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsProxy
    {
        public int AutoDetect;
        public IntPtr AutoConfigUrl;
        public IntPtr Server;
        public IntPtr Bypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out WindowsProxy proxy);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string message, string caption, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string windowClass, string title);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string executable = FindAntigravity();
            if (executable == null)
                throw new InvalidOperationException("找不到 Antigravity 桌面版。请先安装 Antigravity。");

            CheckForOpenWindow(executable);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable),
                Arguments = JoinArguments(args),
                UseShellExecute = false
            };
            // Electron uses Windows proxy settings. Pass the same manual proxy to the backend.
            ConfigureProxy(start);
            using (Process process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("Windows 未能启动 Antigravity。");
            }

            return 0;
        }
        catch (Exception error)
        {
            MessageBox(IntPtr.Zero, error.Message, "Antigravity", 0x00010030);
            return 1;
        }
    }

    private static void CheckForOpenWindow(string executable)
    {
        // Chromium keeps this window while the profile is open, even when the UI is hidden in the tray.
        string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity");
        if (FindWindowEx(new IntPtr(-3), IntPtr.Zero, "Chrome_MessageWindow", profile) != IntPtr.Zero)
            throw new InvalidOperationException("Antigravity 已在运行。请完全退出，再双击启动器，让新进程读取当前系统代理。");

        foreach (Process process in Process.GetProcessesByName("Antigravity"))
        {
            using (process)
            {
                if (process.MainWindowHandle == IntPtr.Zero)
                    continue;

                string fileName;
                try
                {
                    fileName = process.MainModule.FileName;
                }
                catch (Win32Exception)
                {
                    continue;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (String.Equals(fileName, executable, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Antigravity 已打开。请先退出，再双击启动器，让新进程读取当前系统代理。");
            }
        }
    }

    private static void ConfigureProxy(ProcessStartInfo start)
    {
        WindowsProxy system;
        if (!WinHttpGetIEProxyConfigForCurrentUser(out system))
            throw new InvalidOperationException("无法读取 Windows 系统代理。请确认代理软件已开启“系统代理”。");

        string server;
        string bypass;
        try
        {
            server = Marshal.PtrToStringUni(system.Server);
            bypass = Marshal.PtrToStringUni(system.Bypass);
        }
        finally
        {
            if (system.AutoConfigUrl != IntPtr.Zero)
                GlobalFree(system.AutoConfigUrl);
            if (system.Server != IntPtr.Zero)
                GlobalFree(system.Server);
            if (system.Bypass != IntPtr.Zero)
                GlobalFree(system.Bypass);
        }

        string http, https;
        ParseProxy(server, out http, out https);
        if (http == null || https == null)
            throw new InvalidOperationException("Windows 当前没有可用的 HTTP/HTTPS 手动代理。请先在代理软件中开启“系统代理”。");

        IDictionary<string, string> env = start.Environment;
        SetEnvironmentVariable(env, "HTTP_PROXY", http);
        SetEnvironmentVariable(env, "HTTPS_PROXY", https);
        // Do not set ALL_PROXY to an http:// URL: non-HTTP / local WebSocket / IPC clients can stall on it.
        RemoveEnvironmentVariable(env, "ALL_PROXY");
        SetEnvironmentVariable(env, "NO_PROXY", BuildBypassList(bypass));
    }

    private static void SetEnvironmentVariable(IDictionary<string, string> env, string name, string value)
    {
        RemoveEnvironmentVariable(env, name);
        env[name] = value;
    }

    private static void RemoveEnvironmentVariable(IDictionary<string, string> env, string name)
    {
        List<string> matches = null;
        foreach (string key in env.Keys)
        {
            if (String.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                if (matches == null)
                    matches = new List<string>();
                matches.Add(key);
            }
        }

        if (matches != null)
        {
            foreach (string key in matches)
                env.Remove(key);
        }
    }

    private static void ParseProxy(string settings, out string http, out string https)
    {
        string shared = null;
        http = null;
        https = null;
        foreach (string part in (settings ?? "").Split(';'))
        {
            string entry = part.Trim();
            if (entry.Length == 0)
                continue;

            int equals = entry.IndexOf('=');
            if (equals < 0)
            {
                shared = NormalizeProxy(entry);
                continue;
            }

            string scheme = entry.Substring(0, equals).Trim();
            string endpoint = NormalizeProxy(entry.Substring(equals + 1).Trim());
            if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                http = endpoint;
            else if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                https = endpoint;
        }
        http = http ?? shared;
        https = https ?? shared;
    }

    private static string NormalizeProxy(string endpoint)
    {
        if (String.IsNullOrEmpty(endpoint))
            return null;

        Uri uri;
        if (endpoint.IndexOf("://", StringComparison.Ordinal) < 0)
            endpoint = "http://" + endpoint;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Port <= 0 || uri.UserInfo.Length != 0 || uri.PathAndQuery != "/" ||
            uri.Fragment.Length != 0)
            return null;

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string BuildBypassList(string settings)
    {
        // Include both ::1 and [::1] because Node.js WHATWG URL.hostname keeps brackets around IPv6 addresses.
        var values = new List<string> { "localhost", "127.0.0.1", "::1", "[::1]", "0.0.0.0" };
        var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        var private172 = new HashSet<int>();

        foreach (string part in (settings ?? "").Split(';'))
        {
            string entry = part.Trim();
            if (entry.Length == 0 || String.Equals(entry, "<local>", StringComparison.OrdinalIgnoreCase))
                continue;

            string cidr;
            int private172SecondOctet;
            if (TryConvertWildcardPrefixToCidr(entry, out cidr, out private172SecondOctet))
            {
                if (private172SecondOctet >= 16 && private172SecondOctet <= 31)
                {
                    private172.Add(private172SecondOctet);
                    continue;
                }
                entry = cidr;
            }
            else if (entry.StartsWith("*.", StringComparison.Ordinal) && entry.Length > 2)
            {
                // Standard NO_PROXY syntax uses a leading dot (e.g. ".local") rather than "*.local".
                entry = entry.Substring(1);
            }

            if (seen.Add(entry))
                values.Add(entry);
        }

        if (private172.Count == 16)
        {
            if (seen.Add("172.16.0.0/12"))
                values.Add("172.16.0.0/12");
        }
        else if (private172.Count > 0)
        {
            for (int octet = 16; octet <= 31; octet++)
            {
                if (private172.Contains(octet))
                {
                    string cidr = "172." + octet + ".0.0/16";
                    if (seen.Add(cidr))
                        values.Add(cidr);
                }
            }
        }

        return String.Join(",", values);
    }

    private static bool TryConvertWildcardPrefixToCidr(string entry, out string cidr, out int private172SecondOctet)
    {
        cidr = null;
        private172SecondOctet = -1;
        if (entry.Length < 3 || !entry.EndsWith(".*", StringComparison.Ordinal))
            return false;

        string[] parts = entry.Substring(0, entry.Length - 2).Split('.');
        if (parts.Length < 1 || parts.Length > 3)
            return false;

        byte[] octets = new byte[4];
        for (int index = 0; index < parts.Length; index++)
        {
            byte value;
            if (parts[index].Length == 0 || parts[index].Length > 3 || !Byte.TryParse(parts[index], out value))
                return false;
            octets[index] = value;
        }

        if (parts.Length == 2 && octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            private172SecondOctet = octets[1];

        cidr = octets[0] + "." + octets[1] + "." + octets[2] + "." + octets[3] + "/" + (parts.Length * 8);
        return true;
    }

    private static string FindAntigravity()
    {
        // Fast path: check standard installation directories first before touching the registry.
        string direct = InDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity"))
            ?? InDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity"))
            ?? InDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Antigravity"));
        if (direct != null)
            return direct;

        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using (RegistryKey key = hive.OpenSubKey(AppPaths))
            {
                string executable = key == null ? null : ExistingExecutable(key.GetValue("") as string);
                if (executable != null)
                    return executable;
            }
        }

        return FindInUninstallRegistry(Registry.CurrentUser, Uninstall)
            ?? FindInUninstallRegistry(Registry.LocalMachine, Uninstall)
            ?? FindInUninstallRegistry(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private static string FindInUninstallRegistry(RegistryKey hive, string path)
    {
        using (RegistryKey entries = hive.OpenSubKey(path))
        {
            if (entries == null)
                return null;

            foreach (string name in entries.GetSubKeyNames())
            {
                using (RegistryKey entry = entries.OpenSubKey(name))
                {
                    if (entry == null)
                        continue;

                    string title = entry.GetValue("DisplayName") as string;
                    if (!IsAntigravityDisplayName(title))
                        continue;

                    string executable = InDirectory(entry.GetValue("InstallLocation") as string)
                        ?? ExistingExecutable(GetExecutablePath(entry.GetValue("DisplayIcon") as string));
                    if (executable != null)
                        return executable;

                    string uninstall = GetExecutablePath(entry.GetValue("UninstallString") as string);
                    if (uninstall != null)
                    {
                        executable = InDirectory(Path.GetDirectoryName(uninstall));
                        if (executable != null)
                            return executable;
                    }
                }
            }
        }
        return null;
    }

    private static bool IsAntigravityDisplayName(string title)
    {
        if (title == null || !title.StartsWith("Antigravity", StringComparison.OrdinalIgnoreCase))
            return false;

        int index = "Antigravity".Length;
        if (index == title.Length)
            return true;

        if (!Char.IsWhiteSpace(title[index]))
            return false;

        while (index < title.Length && Char.IsWhiteSpace(title[index]))
            index++;

        return index < title.Length && Char.IsDigit(title[index]);
    }

    private static string GetExecutablePath(string setting)
    {
        if (String.IsNullOrWhiteSpace(setting))
            return null;

        string value = setting.Trim();
        if (value[0] == '"')
        {
            int closingQuote = value.IndexOf('"', 1);
            return closingQuote < 0 ? null : value.Substring(1, closingQuote - 1);
        }

        int searchFrom = 0;
        while (searchFrom < value.Length)
        {
            int index = value.IndexOf(".exe", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            int end = index + 4;
            if (end == value.Length || value[end] == ',' || Char.IsWhiteSpace(value[end]))
                return value.Substring(0, end);

            searchFrom = end;
        }

        return null;
    }

    private static string InDirectory(string directory)
    {
        return String.IsNullOrWhiteSpace(directory)
            ? null
            : ExistingExecutable(Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), ExecutableName));
    }

    private static string ExistingExecutable(string value)
    {
        if (String.IsNullOrWhiteSpace(value))
            return null;

        string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!Path.IsPathRooted(path) ||
            !String.Equals(Path.GetFileName(path), ExecutableName, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            return null;
        return Path.GetFullPath(path);
    }

    private static string JoinArguments(string[] args)
    {
        if (args == null || args.Length == 0)
            return "";

        var builder = new StringBuilder();
        for (int index = 0; index < args.Length; index++)
        {
            if (index > 0)
                builder.Append(' ');
            builder.Append(QuoteArgument(args[index] ?? ""));
        }
        return builder.ToString();
    }

    private static string QuoteArgument(string arg)
    {
        if (arg.Length == 0)
            return "\"\"";

        bool needsQuotes = false;
        for (int i = 0; i < arg.Length; i++)
        {
            if (Char.IsWhiteSpace(arg[i]) || arg[i] == '"')
            {
                needsQuotes = true;
                break;
            }
        }
        if (!needsQuotes)
            return arg;

        var builder = new StringBuilder();
        builder.Append('"');
        int backslashes = 0;
        for (int i = 0; i < arg.Length; i++)
        {
            char ch = arg[i];
            if (ch == '\\')
            {
                backslashes++;
            }
            else if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }
                builder.Append(ch);
            }
        }
        if (backslashes > 0)
            builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
