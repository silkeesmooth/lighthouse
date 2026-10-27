using Lighthouse.Core.Native;

namespace Lighthouse.Tests;

public class KnownFoldersTests
{
    private const string ProgramFiles = "{6D809377-6AF0-444B-8957-A3773F02200E}";

    [Fact]
    public void The_tray_s_program_files_path_is_the_installed_exe()
    {
        // As Windows wrote it for the Program Files install, which Lighthouse failed to match before.
        string stored = ProgramFiles + @"\Lighthouse\Lighthouse.exe";
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Lighthouse\Lighthouse.exe");
        Assert.True(KnownFolders.SamePath(stored, installed));
    }

    [Fact]
    public void Plain_and_unknown_paths_are_left_as_they_are()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local\Programs\Lighthouse\Lighthouse.exe",
            KnownFolders.Expand(@"C:\Users\u\AppData\Local\Programs\Lighthouse\Lighthouse.exe"));
        Assert.Equal("{not a guid}\\x.exe", KnownFolders.Expand("{not a guid}\\x.exe"));
        Assert.Equal(ProgramFiles + @"\x.exe", KnownFolders.Expand(ProgramFiles + @"\x.exe", _ => null));
        Assert.Equal(@"D:\Apps\x.exe", KnownFolders.Expand(ProgramFiles + @"\x.exe", _ => @"D:\Apps\"));
    }
}
