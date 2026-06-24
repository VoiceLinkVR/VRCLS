using System.Threading.Tasks;

namespace VRCLS.Core.Interfaces
{
    public interface ITranslationService
    {
        Task<string> TranslateAsync(string text, string targetLanguage, string? sourceLanguage = null, string? routeHint = null);
        Task<string> TranslateBatchAsync(string[] texts, string targetLanguage, string? sourceLanguage = null, string? routeHint = null);
    }
}
