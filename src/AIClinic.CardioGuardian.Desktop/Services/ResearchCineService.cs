using System;
using System.IO;
using System.Windows.Controls;

namespace AIClinic.CardioGuardian.Desktop.Services;

public sealed class ResearchCineService
{
    public string? CurrentFile { get; private set; }

    public void Load(MediaElement media, string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Video file not found.", path);
        CurrentFile = path;
        media.Source = new Uri(path, UriKind.Absolute);
        media.LoadedBehavior = MediaState.Manual;
        media.UnloadedBehavior = MediaState.Manual;
        media.Stop();
    }
}
