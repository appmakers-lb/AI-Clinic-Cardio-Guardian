using System.Windows;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.NativeCodec;

namespace AIClinic.CardioGuardian.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        new DicomSetupBuilder()
            .RegisterServices(services =>
                services.AddFellowOakDicom()
                        .AddImageManager<WinFormsImageManager>()
                        .AddTranscoderManager<NativeTranscoderManager>())
            .Build();

        base.OnStartup(e);
    }
}
