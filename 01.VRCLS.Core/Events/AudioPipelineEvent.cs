using System;
using VRCLS.Core.Models;

namespace VRCLS.Core.Events
{
    public class TextRecognizedEventArgs : EventArgs
    {
        public TranscriptionResult Result { get; set; } = new();
    }
}
