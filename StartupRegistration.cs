using System;
using System.IO;
using Microsoft.Win32;

namespace Anotador;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "AnotadorDeTela";

    internal static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key != null && Matches(key, CommandFor(ExecutablePath()));
    }

    internal static bool Toggle()
    {
        string path = ExecutablePath();
        if (!File.Exists(path)) throw new FileNotFoundException("Abra o Anotador.exe da pasta extraída.", path);
        string command = CommandFor(path);
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        return ToggleValue(key, command);
    }

    internal static bool ToggleValue(RegistryKey key, string command)
    {
        if (Matches(key, command))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return false;
        }
        if (command.Length > 260)
            throw new IOException("O caminho do Anotador é longo demais para a inicialização do Windows.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
        return true;
    }

    private static bool Matches(RegistryKey key, string command) =>
        string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase);

    internal static string CommandFor(string path) => $"\"{path}\"";
    private static string ExecutablePath() => Path.Combine(AppContext.BaseDirectory, "Anotador.exe");
}
