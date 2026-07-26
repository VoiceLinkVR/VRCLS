using VRCLS.Core.Models;
using VRCLS.Services.Recognition;
using Xunit;

namespace VRCLS.Services.Tests;

public sealed class SenseVoiceAsrServiceTests
{
    [Fact]
    public void ProcessAudio_EmitsOneFinalResultWithSegmentMetadata()
    {
        using var recognizer = new FakeRecognizer { Result = "  hello world  " };
        using var service = new SenseVoiceAsrService(recognizer, "Mic");
        var segment = new RecognitionSegmentInfo
        {
            SegmentId = 42,
            CapturedAtUtc = new DateTime(2026, 7, 27, 1, 2, 3, DateTimeKind.Utc)
        };
        var results = new List<TranscriptionResult>();
        service.TextRecognized += (_, result) => results.Add(result);

        service.Start();
        service.ProcessAudio([0.1f, -0.1f], segment);
        service.FinishSegment();

        var result = Assert.Single(results);
        Assert.Equal("hello world", result.Text);
        Assert.Equal("hello world", result.Original);
        Assert.True(result.IsFinal);
        Assert.Equal("Mic", result.Source);
        Assert.Equal(segment.SegmentId, result.SegmentId);
        Assert.Equal(segment.CapturedAtUtc, result.CapturedAtUtc);
        Assert.Equal(1, recognizer.DecodeCount);
    }

    [Fact]
    public void ProcessAudio_IgnoresEmptyAudioAndBlankRecognition()
    {
        using var recognizer = new FakeRecognizer { Result = "  " };
        using var service = new SenseVoiceAsrService(recognizer);
        var results = new List<TranscriptionResult>();
        service.TextRecognized += (_, result) => results.Add(result);

        service.Start();
        service.ProcessAudio([]);
        service.ProcessAudio([0.1f]);

        Assert.Empty(results);
        Assert.Equal(1, recognizer.DecodeCount);
    }

    [Fact]
    public void ProcessAudio_ReportsRecognitionFailure()
    {
        using var recognizer = new FakeRecognizer
        {
            Exception = new InvalidOperationException("decode failed")
        };
        using var service = new SenseVoiceAsrService(recognizer);
        var errors = new List<ServiceErrorEvent>();
        var results = new List<TranscriptionResult>();
        service.ErrorOccurred += (_, error) => errors.Add(error);
        service.TextRecognized += (_, result) => results.Add(result);

        service.Start();
        service.ProcessAudio([0.1f]);

        var error = Assert.Single(errors);
        Assert.Equal(ServiceErrorCode.RecognitionFailed, error.Code);
        Assert.Equal("decode failed", error.GetContext("Reason"));
        Assert.Same(recognizer.Exception, error.Exception);
        Assert.Empty(results);
    }

    [Fact]
    public void StopAndDispose_PreventFurtherRecognition()
    {
        var recognizer = new FakeRecognizer { Result = "ignored" };
        var service = new SenseVoiceAsrService(recognizer);
        var eventCount = 0;
        service.TextRecognized += (_, _) => eventCount++;
        service.ErrorOccurred += (_, _) => eventCount++;

        service.Start();
        service.Stop();
        service.ProcessAudio([0.1f]);
        service.Dispose();
        service.ProcessAudio([0.1f]);

        Assert.False(service.IsRunning);
        Assert.Equal(0, eventCount);
        Assert.Equal(0, recognizer.DecodeCount);
        Assert.Equal(1, recognizer.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => service.Start());
    }

    private sealed class FakeRecognizer : IOfflineSenseVoiceRecognizer
    {
        public string Result { get; init; } = string.Empty;
        public Exception? Exception { get; init; }
        public int DecodeCount { get; private set; }
        public int DisposeCount { get; private set; }

        public string Decode(float[] audioData)
        {
            DecodeCount++;
            if (Exception != null)
            {
                throw Exception;
            }

            return Result;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
