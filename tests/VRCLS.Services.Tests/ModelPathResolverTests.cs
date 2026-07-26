using VRCLS.Core.Models;
using VRCLS.Services.Audio;
using Xunit;

namespace VRCLS.Services.Tests;

public sealed class ModelPathResolverTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"vrcls-sensevoice-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveSenseVoiceModelDir_ReturnsConfiguredCompleteModel()
    {
        var modelDir = CreateModelDirectory(includeModel: true, includeTokens: true);
        var resolver = CreateResolver(modelDir);

        var resolved = resolver.ResolveSenseVoiceModelDir();

        Assert.Equal(modelDir, resolved);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ResolveSenseVoiceModelDir_RejectsIncompleteModel(
        bool includeModel,
        bool includeTokens)
    {
        var modelDir = CreateModelDirectory(includeModel, includeTokens);
        var resolver = CreateResolver(modelDir);

        var resolved = resolver.ResolveSenseVoiceModelDir();

        Assert.Null(resolved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private ModelPathResolver CreateResolver(string modelDir)
    {
        return new ModelPathResolver(new AppConfig
        {
            SenseVoiceModelDir = modelDir,
            PreModelsDir = Path.Combine(_tempRoot, "unused-pre-models")
        });
    }

    private string CreateModelDirectory(bool includeModel, bool includeTokens)
    {
        var modelDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelDir);

        if (includeModel)
        {
            File.WriteAllText(Path.Combine(modelDir, "model.onnx"), "model");
        }

        if (includeTokens)
        {
            File.WriteAllText(Path.Combine(modelDir, "tokens.txt"), "tokens");
        }

        return modelDir;
    }
}
