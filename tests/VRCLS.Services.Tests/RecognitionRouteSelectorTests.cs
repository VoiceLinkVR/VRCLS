using VRCLS.Core.Models;
using VRCLS.Services.Audio;
using Xunit;

namespace VRCLS.Services.Tests;

public sealed class RecognitionRouteSelectorTests
{
    [Theory]
    [InlineData(LocalRecognitionModel.Sherpa, (int)RecognitionRoute.LocalSherpa)]
    [InlineData(LocalRecognitionModel.SenseVoice, (int)RecognitionRoute.LocalSenseVoice)]
    public void Select_RoutesLocalMicrophoneToConfiguredModel(
        LocalRecognitionModel model,
        int expected)
    {
        var route = RecognitionRouteSelector.Select(
            "Mic",
            RecognitionBackendMode.Local,
            model);

        Assert.Equal((RecognitionRoute)expected, route);
    }

    [Theory]
    [InlineData("Desktop", RecognitionBackendMode.Local)]
    [InlineData("Mic", RecognitionBackendMode.Remote)]
    public void Select_KeepsDesktopAndRemoteModeOnRemoteService(
        string source,
        RecognitionBackendMode mode)
    {
        var route = RecognitionRouteSelector.Select(
            source,
            mode,
            LocalRecognitionModel.SenseVoice);

        Assert.Equal(RecognitionRoute.Remote, route);
    }
}
