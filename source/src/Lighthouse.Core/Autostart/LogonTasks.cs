namespace Lighthouse.Core.Autostart;

/// <summary>
/// Where Install.cmd puts an account's logon tasks: \Lighthouse\&lt;user name&gt;\, one folder per Windows account. A
/// second account's install used to replace the first one's tasks, because both were \Lighthouse\Start at logon.
/// </summary>
public static class LogonTasks
{
    public const string App = "Start at logon";
    public const string Sensors = "Sensors";

    public static string Folder(string userName) => $@"\Lighthouse\{userName}\";

    /// <summary>This account's folder.</summary>
    public static string Mine => Folder(Environment.UserName);

    /// <summary>
    /// A user name the elevated --set-startup may take from its command line: one folder name, nothing that leads
    /// out of \Lighthouse\.
    /// </summary>
    public static bool IsUserName(string name) =>
        name.Trim().Length > 0 && name.IndexOfAny(['\\', '/']) < 0 && name.Trim('.').Length > 0;
}
