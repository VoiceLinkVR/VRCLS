using System;

namespace VRCLS.Core.Interfaces
{
    /// <summary>
    /// Provides resolved file system paths for models (e.g., VAD/Sherpa).
    /// </summary>
    public interface IModelPathResolver
    {
        /// <summary>
        /// Resolve the VAD model path (silero_vad.onnx). Should return an existing file path or null.
        /// </summary>
        string? ResolveVadModelPath();

        /// <summary>
        /// Resolve the Sherpa model directory for local recognition, if available.
        /// </summary>
        string? ResolveSherpaModelDir();

        /// <summary>
        /// Resolve a complete SenseVoice model directory containing model.onnx and tokens.txt.
        /// </summary>
        string? ResolveSenseVoiceModelDir();
    }
}
